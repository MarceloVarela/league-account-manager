using System.Runtime.Versioning;
using System.Security.Cryptography;
using Konscious.Security.Cryptography;

namespace LAM.Core.Vault;

/// <summary>Argon2id cost parameters. Higher is slower for us and for an attacker alike.</summary>
public readonly record struct KdfParameters(uint MemoryKiB, byte Iterations, byte Parallelism)
{
    /// <summary>
    /// 64 MiB, t=3, p=4 — the OWASP-recommended Argon2id baseline. Costs roughly a tenth of a
    /// second on a desktop CPU, which is imperceptible for a once-per-session unlock but makes
    /// large-scale offline guessing against a stolen vault expensive.
    /// </summary>
    public static readonly KdfParameters Default = new(64 * 1024, 3, 4);

    /// <summary>Deliberately weak. Tests only — a real vault must never use this.</summary>
    public static readonly KdfParameters TestOnlyFast = new(1024, 1, 1);

    public void Validate()
    {
        if (MemoryKiB < 1024) throw new VaultFormatException("Argon2 memory cost is implausibly low.");
        if (Iterations < 1) throw new VaultFormatException("Argon2 iteration count must be at least 1.");
        if (Parallelism < 1) throw new VaultFormatException("Argon2 parallelism must be at least 1.");
    }
}

/// <summary>
/// The vault's encryption primitives: Argon2id for key derivation, AES-256-GCM for the payload.
///
/// GCM is what gives us tamper detection for free — flipping a single byte of the ciphertext (or of
/// the header, which is passed as associated data) makes decryption throw rather than return
/// garbage. That matters for a file we rewrite on every save and auto-back-up.
/// </summary>
[SupportedOSPlatform("windows")]
public static class VaultCrypto
{
    public const int KeySize = 32;    // AES-256
    public const int SaltSize = 16;
    public const int NonceSize = 12;  // GCM standard
    public const int TagSize = 16;

    /// <summary>Derives the 32-byte vault key. The returned buffer is the caller's to dispose.</summary>
    public static SecretBuffer DeriveKey(SecretBuffer password, byte[] salt, KdfParameters p)
    {
        p.Validate();
        if (salt.Length != SaltSize)
            throw new ArgumentException($"salt must be {SaltSize} bytes", nameof(salt));

        using var argon = new Argon2id(password.Bytes)
        {
            Salt = salt,
            DegreeOfParallelism = p.Parallelism,
            MemorySize = (int)p.MemoryKiB,
            Iterations = p.Iterations,
        };
        return new SecretBuffer(argon.GetBytes(KeySize));
    }

    public static byte[] RandomBytes(int count) => RandomNumberGenerator.GetBytes(count);

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> and returns a complete vault file image.
    /// </summary>
    public static byte[] Encrypt(
        ReadOnlySpan<byte> plaintext,
        SecretBuffer key,
        KdfParameters kdf,
        byte[] salt,
        bool dpapiWrap)
    {
        var nonce = RandomBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        // The header is authenticated but not encrypted. We must build it before we know the final
        // body length (DPAPI changes it), so encrypt against a header whose BodyLength is the
        // pre-wrap size, then re-stamp the length afterwards. To keep the AAD honest, the length
        // field is excluded from the authenticated range — see AssociatedData below.
        var header = new VaultHeader
        {
            Kdf = KdfId.Argon2id,
            MemoryKiB = kdf.MemoryKiB,
            Iterations = kdf.Iterations,
            Parallelism = kdf.Parallelism,
            Salt = salt,
            Nonce = nonce,
            Flags = dpapiWrap ? VaultFlags.DpapiWrapped : VaultFlags.None,
            BodyLength = 0,
        };

        using (var gcm = new AesGcm(key.Bytes, TagSize))
        {
            gcm.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(header));
        }

        var body = new byte[ciphertext.Length + TagSize];
        ciphertext.CopyTo(body, 0);
        tag.CopyTo(body, ciphertext.Length);

        if (dpapiWrap)
        {
            var wrapped = DpapiProtector.Protect(body);
            CryptographicOperations.ZeroMemory(body);
            body = wrapped;
        }

        var finalHeader = header with { BodyLength = (uint)body.Length };
        var file = new byte[VaultHeader.Size + body.Length];
        finalHeader.ToBytes().CopyTo(file, 0);
        body.CopyTo(file, VaultHeader.Size);
        return file;
    }

    /// <summary>
    /// Decrypts a vault file image. Throws <see cref="VaultAuthenticationException"/> on a wrong
    /// password *or* on tampering — the two are cryptographically indistinguishable, which is the
    /// correct behaviour.
    /// </summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> file, SecretBuffer key, VaultHeader header)
    {
        var body = file.Slice(VaultHeader.Size, (int)header.BodyLength).ToArray();

        if (header.Flags.HasFlag(VaultFlags.DpapiWrapped))
        {
            try
            {
                var unwrapped = DpapiProtector.Unprotect(body);
                CryptographicOperations.ZeroMemory(body);
                body = unwrapped;
            }
            catch (CryptographicException)
            {
                throw new VaultProfileMismatchException(
                    "This vault is bound to a different Windows profile or machine and cannot be " +
                    "opened here. Restore it from a portable .lamvault export instead.");
            }
        }

        if (body.Length < TagSize)
            throw new VaultFormatException("Vault body is truncated.");

        var ctLen = body.Length - TagSize;
        var ciphertext = body.AsSpan(0, ctLen);
        var tag = body.AsSpan(ctLen, TagSize);
        var plaintext = new byte[ctLen];

        try
        {
            using var gcm = new AesGcm(key.Bytes, TagSize);
            gcm.Decrypt(header.Nonce, ciphertext, tag, plaintext, AssociatedData(header));
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new VaultAuthenticationException(
                "Could not open the vault. The password is wrong, or the file has been altered.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }

        return plaintext;
    }

    /// <summary>
    /// The authenticated-but-unencrypted header range: everything except the trailing 4-byte body
    /// length. Excluding the length is safe — it is implied by the file size, and any change to it
    /// produces a slice that fails GCM anyway — while letting us stamp it after DPAPI wrapping.
    /// </summary>
    private static byte[] AssociatedData(VaultHeader header)
        => header.ToBytes().AsSpan(0, VaultHeader.Size - 4).ToArray();
}
