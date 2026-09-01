using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace LAM.Core.Vault;

/// <summary>
/// The convenience unlock: Windows Hello releases a copy of the vault key so day-to-day use needs a
/// face or a PIN instead of the master password.
///
/// It is deliberately a *second path to the same key*, never a replacement. The master password
/// always opens the vault, so a wiped Windows profile or a broken fingerprint reader is an
/// inconvenience rather than the permanent loss of every account.
///
/// The mechanism: Hello signs a fixed random challenge with a hardware-backed key that cannot be
/// exported, we stretch that signature into a wrapping key, and the vault key is sealed under it.
/// Nothing usable sits in <c>hello.bin</c> without a successful biometric gesture.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class HelloUnlock
{
    private const string CredentialName = "LeagueAccountManager.Vault";
    private const int ChallengeSize = 32;

    private readonly VaultPaths _paths;

    public HelloUnlock(VaultPaths paths) => _paths = paths;

    public bool IsEnrolled => File.Exists(_paths.HelloFile);

    /// <summary>True when this machine has Hello configured with a usable gesture.</summary>
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            return await KeyCredentialManager.IsSupportedAsync();
        }
        catch (Exception ex) when (ex is COMException or PlatformNotSupportedException or TypeLoadException)
        {
            return false;
        }
    }

    /// <summary>
    /// Enrols the current vault key so Hello can release it later. Prompts twice on purpose —
    /// see <see cref="EnsureDeterministic"/>.
    /// </summary>
    public async Task EnrollAsync(UnlockedVault vault)
    {
        var challenge = VaultCrypto.RandomBytes(ChallengeSize);

        var create = await KeyCredentialManager.RequestCreateAsync(
            CredentialName, KeyCredentialCreationOption.ReplaceExisting);

        if (create.Status != KeyCredentialStatus.Success)
            throw new HelloUnlockException(Describe(create.Status));

        var signature = await SignAsync(create.Credential, challenge);
        try
        {
            await EnsureDeterministic(create.Credential, challenge, signature);

            using var wrapKey = DeriveWrapKey(signature, challenge);
            var sealedKey = vault.UseKey(key =>
                VaultCrypto.Encrypt(
                    key.Span,
                    wrapKey,
                    KdfParameters.Default,   // recorded in the header but unused: the key is already derived
                    challenge[..VaultCrypto.SaltSize],
                    dpapiWrap: true));       // DPAPI too, so hello.bin is useless if copied off the machine

            _paths.EnsureCreated();
            File.WriteAllBytes(_paths.HelloFile, Combine(challenge, sealedKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    /// <summary>
    /// Prompts for Hello and returns the vault key. The caller owns the returned buffer.
    /// </summary>
    public async Task<SecretBuffer> UnlockKeyAsync()
    {
        if (!IsEnrolled)
            throw new HelloUnlockException("Windows Hello is not set up for this vault.");

        var stored = File.ReadAllBytes(_paths.HelloFile);
        if (stored.Length <= ChallengeSize)
            throw new HelloUnlockException("The Windows Hello data for this vault is corrupt. Unlock with your master password and re-enable Hello.");

        var challenge = stored[..ChallengeSize];
        var sealedKey = stored[ChallengeSize..];

        var open = await KeyCredentialManager.OpenAsync(CredentialName);
        if (open.Status != KeyCredentialStatus.Success)
            throw new HelloUnlockException(Describe(open.Status));

        var signature = await SignAsync(open.Credential, challenge);
        try
        {
            using var wrapKey = DeriveWrapKey(signature, challenge);
            var header = VaultHeader.Parse(sealedKey);
            var key = VaultCrypto.Decrypt(sealedKey, wrapKey, header);
            return new SecretBuffer(key);
        }
        catch (VaultAuthenticationException)
        {
            throw new HelloUnlockException(
                "Windows Hello succeeded but the stored key no longer matches this vault. " +
                "Unlock with your master password and re-enable Hello.");
        }
        catch (VaultProfileMismatchException)
        {
            throw new HelloUnlockException(
                "The Windows Hello data belongs to a different Windows profile. Unlock with your master password.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    /// <summary>Forgets the Hello enrolment. The master password is unaffected.</summary>
    public async Task RemoveAsync()
    {
        if (File.Exists(_paths.HelloFile))
        {
            // Overwrite before unlinking so the sealed key is not left recoverable in free space.
            var length = (int)new FileInfo(_paths.HelloFile).Length;
            File.WriteAllBytes(_paths.HelloFile, new byte[length]);
            File.Delete(_paths.HelloFile);
        }

        try
        {
            await KeyCredentialManager.DeleteAsync(CredentialName);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // The credential may already be gone; the local file is what actually gates unlocking.
        }
    }

    /// <summary>
    /// Signs the challenge a second time and refuses enrolment if the two signatures differ.
    ///
    /// The whole scheme rests on Hello producing a stable signature for a fixed input, which holds
    /// for the RSA PKCS#1 v1.5 signing it uses today. Rather than trust that forever, we check it
    /// once at enrolment: a randomised scheme would otherwise enrol cleanly and then fail to unlock
    /// afterwards, which is exactly the sort of bug that only shows up when you need it most.
    /// </summary>
    private static async Task EnsureDeterministic(KeyCredential credential, byte[] challenge, byte[] first)
    {
        var second = await SignAsync(credential, challenge);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(first, second))
                throw new HelloUnlockException(
                    "Windows Hello on this machine produces a different signature each time, so it " +
                    "cannot be used to unlock the vault. Your master password still works.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(second);
        }
    }

    private static async Task<byte[]> SignAsync(KeyCredential credential, byte[] challenge)
    {
        var buffer = CryptographicBuffer.CreateFromByteArray(challenge);
        var result = await credential.RequestSignAsync(buffer);

        if (result.Status != KeyCredentialStatus.Success)
            throw new HelloUnlockException(Describe(result.Status));

        CryptographicBuffer.CopyToByteArray(result.Result, out var signature);
        return signature;
    }

    /// <summary>
    /// Stretches the Hello signature into an AES key. HKDF rather than a raw hash so the output is
    /// domain-separated: the same signature could never collide with key material derived for any
    /// other purpose.
    /// </summary>
    private static SecretBuffer DeriveWrapKey(byte[] signature, byte[] challenge)
        => new(HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: signature,
            outputLength: VaultCrypto.KeySize,
            salt: challenge,
            info: "LeagueAccountManager/hello-wrap/v1"u8.ToArray()));

    private static byte[] Combine(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }

    private static string Describe(KeyCredentialStatus status) => status switch
    {
        KeyCredentialStatus.NotFound =>
            "No Windows Hello key exists for this vault. Unlock with your master password and re-enable Hello.",
        KeyCredentialStatus.UserCanceled =>
            "Windows Hello was cancelled.",
        KeyCredentialStatus.UserPrefersPassword =>
            "Windows Hello was dismissed in favour of a password.",
        KeyCredentialStatus.CredentialAlreadyExists =>
            "A Windows Hello key for this vault already exists.",
        KeyCredentialStatus.SecurityDeviceLocked =>
            "The security device is locked. Sign in to Windows again, then retry.",
        KeyCredentialStatus.UnknownError =>
            "Windows Hello failed for an unknown reason. Use your master password.",
        _ => "Windows Hello is unavailable (" + status + "). Use your master password.",
    };
}

public sealed class HelloUnlockException : Exception
{
    public HelloUnlockException(string message) : base(message) { }
}
