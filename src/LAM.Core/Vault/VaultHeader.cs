using System.Buffers.Binary;

namespace LAM.Core.Vault;

/// <summary>Which key-derivation function produced the vault key.</summary>
public enum KdfId : byte
{
    Argon2id = 1,
}

[Flags]
public enum VaultFlags : byte
{
    None = 0,
    /// <summary>The body was additionally wrapped with DPAPI(CurrentUser) — i.e. machine-bound.</summary>
    DpapiWrapped = 1,
}

/// <summary>
/// The plaintext header of a vault file.
///
/// Deliberately *not* encrypted: we need the KDF parameters and salt in order to derive the key in
/// the first place, and keeping the format legible means a future version can migrate an old file
/// instead of bricking it. The header is fed to AES-GCM as associated data, so tampering with any
/// field here (say, downgrading the Argon2 cost) fails authentication rather than silently
/// weakening the vault.
/// </summary>
public sealed record VaultHeader
{
    public const int Size = 45;
    public const byte CurrentVersion = 1;

    private static ReadOnlySpan<byte> Magic => "LAMV"u8;

    public byte Version { get; init; } = CurrentVersion;
    public KdfId Kdf { get; init; } = KdfId.Argon2id;
    public required uint MemoryKiB { get; init; }
    public required byte Iterations { get; init; }
    public required byte Parallelism { get; init; }
    public required byte[] Salt { get; init; }      // 16
    public required byte[] Nonce { get; init; }     // 12
    public required VaultFlags Flags { get; init; }
    public required uint BodyLength { get; init; }

    public byte[] ToBytes()
    {
        if (Salt.Length != VaultCrypto.SaltSize)
            throw new InvalidOperationException($"salt must be {VaultCrypto.SaltSize} bytes");
        if (Nonce.Length != VaultCrypto.NonceSize)
            throw new InvalidOperationException($"nonce must be {VaultCrypto.NonceSize} bytes");

        var buf = new byte[Size];
        var span = buf.AsSpan();
        Magic.CopyTo(span[..4]);
        span[4] = Version;
        span[5] = (byte)Kdf;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(6, 4), MemoryKiB);
        span[10] = Iterations;
        span[11] = Parallelism;
        Salt.CopyTo(span.Slice(12, 16));
        Nonce.CopyTo(span.Slice(28, 12));
        span[40] = (byte)Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(41, 4), BodyLength);
        return buf;
    }

    public static VaultHeader Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Size)
            throw new VaultFormatException("File is too short to be a vault.");
        if (!bytes[..4].SequenceEqual(Magic))
            throw new VaultFormatException("Not a vault file (bad magic).");

        var version = bytes[4];
        if (version > CurrentVersion)
            throw new VaultFormatException(
                $"Vault was written by a newer version of the app (format v{version}, this build reads v{CurrentVersion}).");

        var kdf = (KdfId)bytes[5];
        if (!Enum.IsDefined(kdf))
            throw new VaultFormatException($"Unknown key-derivation function id {bytes[5]}.");

        return new VaultHeader
        {
            Version = version,
            Kdf = kdf,
            MemoryKiB = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(6, 4)),
            Iterations = bytes[10],
            Parallelism = bytes[11],
            Salt = bytes.Slice(12, 16).ToArray(),
            Nonce = bytes.Slice(28, 12).ToArray(),
            Flags = (VaultFlags)bytes[40],
            BodyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(41, 4)),
        };
    }
}

public sealed class VaultFormatException : Exception
{
    public VaultFormatException(string message) : base(message) { }
}

/// <summary>The master password (or export passphrase) did not open the vault.</summary>
public sealed class VaultAuthenticationException : Exception
{
    public VaultAuthenticationException(string message) : base(message) { }
}

/// <summary>The vault is machine-bound and this is not the machine (or profile) that wrote it.</summary>
public sealed class VaultProfileMismatchException : Exception
{
    public VaultProfileMismatchException(string message) : base(message) { }
}
