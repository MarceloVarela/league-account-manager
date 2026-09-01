using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using LAM.Core.Model;

namespace LAM.Core.Vault;

/// <summary>
/// Reads and writes <c>vault.dat</c>.
///
/// Every save is atomic (temp file, flush to disk, then replace) and takes a timestamped backup
/// first, so the failure mode of a crash or a power cut mid-write is "you lose the last save",
/// never "the vault is now an unreadable stub".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VaultRepository
{
    private readonly VaultPaths _paths;
    private readonly TimeProvider _clock;

    public VaultRepository(VaultPaths paths, TimeProvider? clock = null)
    {
        _paths = paths;
        _clock = clock ?? TimeProvider.System;
    }

    public VaultPaths Paths => _paths;

    /// <summary>Creates a brand-new vault and returns the unlocked session for it.</summary>
    public UnlockedVault Create(SecretBuffer masterPassword, bool bindToMachine = true, KdfParameters? kdf = null)
    {
        if (_paths.VaultExists)
            throw new InvalidOperationException("A vault already exists at " + _paths.VaultFile + ".");

        _paths.EnsureCreated();

        var parameters = kdf ?? KdfParameters.Default;
        var salt = VaultCrypto.RandomBytes(VaultCrypto.SaltSize);
        var key = VaultCrypto.DeriveKey(masterPassword, salt, parameters);

        var document = new VaultDocument();
        var session = new UnlockedVault(this, document, key, salt, parameters, bindToMachine);
        session.Save();
        return session;
    }

    /// <summary>Opens the vault with a master password.</summary>
    public UnlockedVault Unlock(SecretBuffer masterPassword)
    {
        var (file, header) = ReadFile();
        var kdf = new KdfParameters(header.MemoryKiB, header.Iterations, header.Parallelism);
        var key = VaultCrypto.DeriveKey(masterPassword, header.Salt, kdf);

        try
        {
            return Decrypt(file, header, key, kdf);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens the vault with a key recovered by another means — currently Windows Hello. The key
    /// still has to authenticate against GCM, so a wrong key fails exactly like a wrong password.
    /// </summary>
    /// <param name="vaultKey">
    /// Stays owned by the caller, who should dispose it as usual. The returned vault gets its own
    /// copy.
    ///
    /// The copy is the whole point. <see cref="UnlockedVault"/> owns the key it is constructed with
    /// and zeroes it on lock, so passing the caller's buffer straight through made the obvious call
    /// site — <c>using var key = …; repository.UnlockWithKey(key)</c> — leave the vault holding a
    /// zeroed key. Reads still worked because the document was already decrypted, so the failure
    /// only appeared later, on the first save, as a bare ObjectDisposedException.
    /// </param>
    public UnlockedVault UnlockWithKey(SecretBuffer vaultKey)
    {
        var (file, header) = ReadFile();
        var kdf = new KdfParameters(header.MemoryKiB, header.Iterations, header.Parallelism);

        var owned = vaultKey.Clone();
        try
        {
            return Decrypt(file, header, owned, kdf);
        }
        catch
        {
            owned.Dispose();
            throw;
        }
    }

    /// <summary>Reads the header only — used to tell "wrong password" from "wrong machine".</summary>
    public VaultHeader ReadHeader()
    {
        var (_, header) = ReadFile();
        return header;
    }

    private UnlockedVault Decrypt(byte[] file, VaultHeader header, SecretBuffer key, KdfParameters kdf)
    {
        var plaintext = VaultCrypto.Decrypt(file, key, header);
        try
        {
            var document = JsonSerializer.Deserialize<VaultDocument>(plaintext, VaultJson.Storage)
                           ?? throw new VaultFormatException("Vault decrypted but contained no document.");

            if (document.SchemaVersion > VaultDocument.CurrentSchemaVersion)
                throw new VaultFormatException(
                    "Vault holds schema v" + document.SchemaVersion +
                    "; this build understands v" + VaultDocument.CurrentSchemaVersion + ".");

            return new UnlockedVault(
                this, document, key, header.Salt, kdf,
                bindToMachine: header.Flags.HasFlag(VaultFlags.DpapiWrapped));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private (byte[] File, VaultHeader Header) ReadFile()
    {
        if (!_paths.VaultExists)
            throw new FileNotFoundException("No vault found.", _paths.VaultFile);

        var file = File.ReadAllBytes(_paths.VaultFile);
        var header = VaultHeader.Parse(file);

        var expected = VaultHeader.Size + (long)header.BodyLength;
        if (file.LongLength < expected)
            throw new VaultFormatException(
                "Vault is truncated: header declares " + expected + " bytes, file is " + file.LongLength + ".");

        return (file, header);
    }

    /// <summary>Serialises, encrypts and atomically replaces the vault file.</summary>
    internal void Write(VaultDocument document, SecretBuffer key, byte[] salt, KdfParameters kdf, bool bindToMachine)
    {
        _paths.EnsureCreated();
        document.LastSavedUtc = _clock.GetUtcNow();

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(document, VaultJson.Storage);
        byte[] image;
        try
        {
            image = VaultCrypto.Encrypt(plaintext, key, kdf, salt, bindToMachine);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        BackupExisting(document.Settings.BackupsToKeep);
        AtomicWrite(_paths.VaultFile, image);
    }

    private static void AtomicWrite(string path, byte[] contents)
    {
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            // File.Replace swaps the inode atomically, so a crash lands on one of the two complete
            // files and never on a half-written one.
            File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    private void BackupExisting(int keep)
    {
        if (keep <= 0 || !_paths.VaultExists) return;

        Directory.CreateDirectory(_paths.BackupDirectory);
        var stamp = _clock.GetUtcNow().ToString("yyyyMMdd-HHmmss");
        var target = Path.Combine(_paths.BackupDirectory, "vault-" + stamp + ".dat");

        try
        {
            File.Copy(_paths.VaultFile, target, overwrite: true);
        }
        catch (IOException)
        {
            // A backup that cannot be written must not block the save that follows it — the live
            // write is the one that matters, and it is itself atomic.
            return;
        }

        PruneBackups(keep);
    }

    private void PruneBackups(int keep)
    {
        var stale = new DirectoryInfo(_paths.BackupDirectory)
            .GetFiles("vault-*.dat")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(keep)
            .ToList();

        foreach (var file in stale)
        {
            try { file.Delete(); } catch (IOException) { /* the next prune will get it */ }
        }
    }

    public IReadOnlyList<FileInfo> ListBackups()
        => Directory.Exists(_paths.BackupDirectory)
            ? new DirectoryInfo(_paths.BackupDirectory)
                .GetFiles("vault-*.dat")
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .ToList()
            : [];
}
