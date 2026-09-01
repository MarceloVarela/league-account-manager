using System.Security.Cryptography;
using System.Text;

namespace LAM.Core.Vault;

/// <summary>
/// A byte buffer holding key material or a plaintext secret, zeroed on dispose.
///
/// This is defence in depth, not a guarantee. The CLR can move or copy a managed array during
/// a GC compaction, and Windows can page it to disk, so a determined attacker with memory access
/// can still win. What this *does* buy is that secrets are not left sitting in the heap for the
/// lifetime of the process, which is the realistic exposure for a desktop app that stays open
/// all day. Every path that decrypts a password wraps it in one of these and disposes promptly.
/// </summary>
public sealed class SecretBuffer : IDisposable
{
    private byte[]? _bytes;

    public SecretBuffer(byte[] bytes) => _bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));

    public SecretBuffer(int length) => _bytes = new byte[length];

    public static SecretBuffer FromString(string value)
        => new(Encoding.UTF8.GetBytes(value));

    public bool IsDisposed => _bytes is null;

    public int Length => Bytes.Length;

    public byte[] Bytes => _bytes ?? throw new ObjectDisposedException(nameof(SecretBuffer));

    public ReadOnlySpan<byte> Span => Bytes;

    /// <summary>Copies the contents into a new buffer the caller owns and must dispose.</summary>
    public SecretBuffer Clone() => new((byte[])Bytes.Clone());

    /// <summary>
    /// Materialises the secret as a string. Unavoidable at the boundary where we hand a password
    /// to the typing routine, but the string itself is immutable and cannot be zeroed, so callers
    /// must keep the scope as tight as possible.
    /// </summary>
    public string AsString() => Encoding.UTF8.GetString(Bytes);

    public void Dispose()
    {
        if (_bytes is null) return;
        CryptographicOperations.ZeroMemory(_bytes);
        _bytes = null;
    }
}
