using System.Runtime.Versioning;
using System.Text.Json;
using LAM.Core.Model;

namespace LAM.Core.Vault;

/// <summary>
/// An open vault: the decrypted document plus the key needed to write it back.
///
/// Lives only while the app is unlocked. Disposing it — whether by the idle timer, the workstation
/// locking, or the user pressing Lock — zeroes the key, drops the document and raises
/// <see cref="Locked"/> so the UI can fall back to the lock screen. After that the instance is
/// inert and every accessor throws.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UnlockedVault : IDisposable
{
    private readonly VaultRepository _repository;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    private VaultDocument? _document;
    private SecretBuffer? _key;
    private byte[] _salt;
    private KdfParameters _kdf;
    private ITimer? _idleTimer;
    private DateTimeOffset _lastActivity;

    /// <param name="key">
    /// Becomes the property of this vault, which zeroes it on lock. Callers must hand over a buffer
    /// they are not going to dispose themselves — see <see cref="VaultRepository.UnlockWithKey"/>,
    /// which clones for exactly this reason.
    /// </param>
    internal UnlockedVault(
        VaultRepository repository,
        VaultDocument document,
        SecretBuffer key,
        byte[] salt,
        KdfParameters kdf,
        bool bindToMachine,
        TimeProvider? clock = null)
    {
        _repository = repository;
        _document = document;
        _key = key;
        _salt = salt;
        _kdf = kdf;
        BindToMachine = bindToMachine;
        _clock = clock ?? TimeProvider.System;
        _lastActivity = _clock.GetUtcNow();
    }

    /// <summary>Raised when the vault locks, for any reason. Always raised exactly once.</summary>
    public event EventHandler<VaultLockReason>? Locked;

    public bool IsLocked => _document is null;

    /// <summary>Whether this vault is DPAPI-bound to the current Windows profile.</summary>
    public bool BindToMachine { get; private set; }

    public VaultDocument Document =>
        _document ?? throw new InvalidOperationException("The vault is locked.");

    public AppSettings Settings => Document.Settings;

    /// <summary>
    /// Hands the raw vault key to a callback. Used only to seal the key for the Windows Hello
    /// unlock path; the callback must not retain the buffer.
    /// </summary>
    internal T UseKey<T>(Func<SecretBuffer, T> callback)
    {
        var key = _key ?? throw new InvalidOperationException("The vault is locked.");
        return callback(key);
    }

    public void Save()
    {
        lock (_gate)
        {
            var key = _key ?? throw new InvalidOperationException("The vault is locked.");
            var document = _document ?? throw new InvalidOperationException("The vault is locked.");
            _repository.Write(document, key, _salt, _kdf, BindToMachine);
        }
        Touch();
    }

    /// <summary>Marks user activity, restarting the idle countdown.</summary>
    public void Touch() => _lastActivity = _clock.GetUtcNow();

    /// <summary>
    /// Starts the inactivity timer. Ticks once a second rather than scheduling a single long timer
    /// so that a machine waking from sleep locks promptly instead of honouring a timer that slept
    /// through the gap.
    /// </summary>
    public void StartIdleTimer()
    {
        StopIdleTimer();
        if (Settings.AutoLockMinutes <= 0) return;

        _idleTimer = _clock.CreateTimer(
            _ => CheckIdle(),
            state: null,
            dueTime: TimeSpan.FromSeconds(1),
            period: TimeSpan.FromSeconds(1));
    }

    public void StopIdleTimer()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
    }

    private void CheckIdle()
    {
        if (IsLocked) return;
        var limit = TimeSpan.FromMinutes(Settings.AutoLockMinutes);
        if (limit <= TimeSpan.Zero) return;
        if (_clock.GetUtcNow() - _lastActivity >= limit)
            Lock(VaultLockReason.Idle);
    }

    /// <summary>
    /// Re-encrypts the vault under a new master password. A fresh salt is drawn so the new key is
    /// unrelated to the old one, and the file is rewritten immediately — there is no window in
    /// which the old password still opens it.
    /// </summary>
    public void ChangeMasterPassword(SecretBuffer newPassword, KdfParameters? kdf = null)
    {
        lock (_gate)
        {
            if (_document is null) throw new InvalidOperationException("The vault is locked.");

            var parameters = kdf ?? KdfParameters.Default;
            var salt = VaultCrypto.RandomBytes(VaultCrypto.SaltSize);
            var newKey = VaultCrypto.DeriveKey(newPassword, salt, parameters);

            var oldKey = _key;
            _key = newKey;
            _salt = salt;
            _kdf = parameters;
            oldKey?.Dispose();

            _repository.Write(_document, newKey, salt, parameters, BindToMachine);
        }
        Touch();
    }

    /// <summary>Turns machine binding on or off and rewrites the file.</summary>
    public void SetMachineBinding(bool bind)
    {
        lock (_gate)
        {
            BindToMachine = bind;
        }
        Save();
    }

    /// <summary>
    /// Writes a portable copy encrypted under a separate passphrase.
    ///
    /// Deliberately never DPAPI-wrapped: the entire point is that this file opens on another
    /// machine. That makes the export passphrase the only thing protecting it, so it should be a
    /// strong one — the UI says so at the point of export.
    /// </summary>
    public void ExportTo(string path, SecretBuffer exportPassphrase, KdfParameters? kdf = null)
    {
        var document = _document ?? throw new InvalidOperationException("The vault is locked.");

        var parameters = kdf ?? KdfParameters.Default;
        var salt = VaultCrypto.RandomBytes(VaultCrypto.SaltSize);
        using var key = VaultCrypto.DeriveKey(exportPassphrase, salt, parameters);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(document, VaultJson.Storage);
        try
        {
            var image = VaultCrypto.Encrypt(plaintext, key, parameters, salt, dpapiWrap: false);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, image);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        }
        Touch();
    }

    /// <summary>Reads a portable export without touching the live vault, for import preview.</summary>
    public static VaultDocument ReadExport(string path, SecretBuffer passphrase)
    {
        var file = File.ReadAllBytes(path);
        var header = VaultHeader.Parse(file);
        var kdf = new KdfParameters(header.MemoryKiB, header.Iterations, header.Parallelism);
        using var key = VaultCrypto.DeriveKey(passphrase, header.Salt, kdf);

        var plaintext = VaultCrypto.Decrypt(file, key, header);
        try
        {
            return JsonSerializer.Deserialize<VaultDocument>(plaintext, VaultJson.Storage)
                   ?? throw new VaultFormatException("Export decrypted but contained no document.");
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Lock(VaultLockReason reason = VaultLockReason.Requested)
    {
        bool wasOpen;
        lock (_gate)
        {
            wasOpen = _document is not null;
            StopIdleTimer();
            _key?.Dispose();
            _key = null;
            _document = null;
        }

        if (wasOpen) Locked?.Invoke(this, reason);
    }

    public void Dispose() => Lock(VaultLockReason.Disposed);
}

public enum VaultLockReason
{
    Requested,
    Idle,
    WorkstationLocked,
    Disposed,
}
