using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace LAM.Core.Vault;

/// <summary>
/// Binds a blob to the current Windows user profile via DPAPI.
///
/// This is the layer that makes a stolen <c>vault.dat</c> useless on another machine: without the
/// user's DPAPI master key the body cannot even be reduced to "AES ciphertext I could brute-force
/// the password against". It is deliberately *not* applied to portable exports.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DpapiProtector
{
    // Fixed secondary entropy. Not a secret (it ships in the binary); it scopes the protection to
    // this application so another program running as the same user cannot unprotect our blobs by
    // accident, and it means a blob from a different app cannot be swapped in.
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("LeagueAccountManager/v1/vault-body");

    public static byte[] Protect(ReadOnlySpan<byte> plaintext)
        => ProtectedData.Protect(plaintext.ToArray(), Entropy, DataProtectionScope.CurrentUser);

    public static byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
        => ProtectedData.Unprotect(ciphertext.ToArray(), Entropy, DataProtectionScope.CurrentUser);

    /// <summary>
    /// True when the blob can be unprotected by the current profile. Used to give a precise error
    /// ("this vault belongs to a different Windows profile") instead of a generic decrypt failure.
    /// </summary>
    public static bool CanUnprotect(ReadOnlySpan<byte> ciphertext)
    {
        try
        {
            var plain = Unprotect(ciphertext);
            CryptographicOperations.ZeroMemory(plain);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
