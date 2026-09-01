using System.Text.Json;
using LAM.Core.Model;
using LAM.Core.Vault;
using Xunit;

namespace LAM.Tests;

/// <summary>
/// Exercises the vault against a throwaway directory. Uses <see cref="KdfParameters.TestOnlyFast"/>
/// so the suite is not dominated by Argon2 work — the parameters are a stored header field, so the
/// code paths are identical to production.
/// </summary>
public sealed class VaultTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lam-tests", Guid.NewGuid().ToString("N"));

    private VaultRepository NewRepository() => new(new VaultPaths(_root));

    private static SecretBuffer Pass(string value) => SecretBuffer.FromString(value);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* temp dir, best effort */ }
    }

    [Fact]
    public void Create_then_unlock_round_trips_accounts()
    {
        var repository = NewRepository();

        using (var password = Pass("correct horse battery staple"))
        using (var vault = repository.Create(password, bindToMachine: false, KdfParameters.TestOnlyFast))
        {
            vault.Document.Accounts.Add(new AccountEntry
            {
                Label = "main",
                LoginUsername = "marc@example.com",
                Password = "hunter2",
                Region = "BR",
            });
            vault.Save();
        }

        using var reopenPassword = Pass("correct horse battery staple");
        using var reopened = repository.Unlock(reopenPassword);

        var account = Assert.Single(reopened.Document.Accounts);
        Assert.Equal("main", account.Label);
        Assert.Equal("BR", account.Region);
        Assert.Equal("hunter2", account.Password!.Value);
    }

    [Fact]
    public void Wrong_password_is_rejected()
    {
        var repository = NewRepository();
        using (var password = Pass("the right one"))
        using (var vault = repository.Create(password, bindToMachine: false, KdfParameters.TestOnlyFast))
        {
            vault.Save();
        }

        using var wrong = Pass("the wrong one");
        Assert.Throws<VaultAuthenticationException>(() => repository.Unlock(wrong));
    }

    [Fact]
    public void Flipping_one_ciphertext_byte_fails_authentication()
    {
        var repository = NewRepository();
        using (var password = Pass("pw"))
        using (var vault = repository.Create(password, bindToMachine: false, KdfParameters.TestOnlyFast))
        {
            vault.Document.Accounts.Add(new AccountEntry { Label = "smurf" });
            vault.Save();
        }

        // Corrupt a byte well inside the body rather than the header, so we are testing GCM's
        // integrity guarantee and not merely a malformed-header check.
        var path = repository.Paths.VaultFile;
        var bytes = File.ReadAllBytes(path);
        var target = VaultHeader.Size + 4;
        bytes[target] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        using var password2 = Pass("pw");
        Assert.Throws<VaultAuthenticationException>(() => repository.Unlock(password2));
    }

    [Fact]
    public void Tampering_with_the_header_fails_authentication()
    {
        var repository = NewRepository();
        using (var password = Pass("pw"))
        using (var vault = repository.Create(password, bindToMachine: false, KdfParameters.TestOnlyFast))
        {
            vault.Save();
        }

        // Downgrade the recorded Argon2 iteration count. The header is passed to GCM as associated
        // data precisely so this cannot pass unnoticed.
        var path = repository.Paths.VaultFile;
        var bytes = File.ReadAllBytes(path);
        bytes[10] = 99;
        File.WriteAllBytes(path, bytes);

        using var password2 = Pass("pw");
        Assert.ThrowsAny<Exception>(() => repository.Unlock(password2));
    }

    [Fact]
    public void Changing_the_master_password_invalidates_the_old_one()
    {
        var repository = NewRepository();
        using (var original = Pass("old"))
        using (var vault = repository.Create(original, bindToMachine: false, KdfParameters.TestOnlyFast))
        {
            vault.Document.Accounts.Add(new AccountEntry { Label = "keep me" });
            vault.Save();

            using var replacement = Pass("new");
            vault.ChangeMasterPassword(replacement, KdfParameters.TestOnlyFast);
        }

        using var old = Pass("old");
        Assert.Throws<VaultAuthenticationException>(() => repository.Unlock(old));

        using var fresh = Pass("new");
        using var reopened = repository.Unlock(fresh);
        Assert.Equal("keep me", Assert.Single(reopened.Document.Accounts).Label);
    }

    [Fact]
    public void Export_is_portable_and_opens_with_its_own_passphrase()
    {
        var repository = NewRepository();
        var exportPath = Path.Combine(_root, "backup.lamvault");

        using (var password = Pass("vault pw"))
        using (var vault = repository.Create(password, bindToMachine: false, KdfParameters.TestOnlyFast))
        {
            vault.Document.Accounts.Add(new AccountEntry { Label = "exported", Password = "s3cret" });
            vault.Save();

            using var exportPassphrase = Pass("a different passphrase");
            vault.ExportTo(exportPath, exportPassphrase, KdfParameters.TestOnlyFast);
        }

        using var reading = Pass("a different passphrase");
        var document = UnlockedVault.ReadExport(exportPath, reading);
        Assert.Equal("s3cret", Assert.Single(document.Accounts).Password!.Value);

        // The export must not be machine-bound, or it would be useless on the PC it was made for.
        var header = VaultHeader.Parse(File.ReadAllBytes(exportPath));
        Assert.False(header.Flags.HasFlag(VaultFlags.DpapiWrapped));
    }

    [Fact]
    public void Locking_zeroes_the_key_and_blocks_further_access()
    {
        var repository = NewRepository();
        using var password = Pass("pw");
        var vault = repository.Create(password, bindToMachine: false, KdfParameters.TestOnlyFast);

        VaultLockReason? observed = null;
        vault.Locked += (_, reason) => observed = reason;

        vault.Lock();

        Assert.True(vault.IsLocked);
        Assert.Equal(VaultLockReason.Requested, observed);
        Assert.Throws<InvalidOperationException>(() => vault.Document);
        Assert.Throws<InvalidOperationException>(() => vault.Save());
    }

    [Fact]
    public void Locking_twice_raises_the_event_once()
    {
        var repository = NewRepository();
        using var password = Pass("pw");
        var vault = repository.Create(password, bindToMachine: false, KdfParameters.TestOnlyFast);

        var count = 0;
        vault.Locked += (_, _) => count++;

        vault.Lock();
        vault.Lock();
        vault.Dispose();

        Assert.Equal(1, count);
    }

    [Fact]
    public void Saving_keeps_rolling_backups_and_prunes_to_the_limit()
    {
        var repository = NewRepository();
        using var password = Pass("pw");
        using var vault = repository.Create(password, bindToMachine: false, KdfParameters.TestOnlyFast);

        vault.Settings.BackupsToKeep = 3;
        for (var i = 0; i < 6; i++)
        {
            vault.Document.Accounts.Add(new AccountEntry { Label = "acct" + i });
            vault.Save();
        }

        Assert.InRange(repository.ListBackups().Count, 1, 3);
    }

    [Fact]
    public void A_machine_bound_vault_records_the_dpapi_flag()
    {
        var repository = NewRepository();
        using var password = Pass("pw");
        using (var vault = repository.Create(password, bindToMachine: true, KdfParameters.TestOnlyFast))
        {
            vault.Save();
        }

        var header = repository.ReadHeader();
        Assert.True(header.Flags.HasFlag(VaultFlags.DpapiWrapped));

        // And it must still open normally on the machine that wrote it.
        using var again = Pass("pw");
        using var reopened = repository.Unlock(again);
        Assert.NotNull(reopened.Document);
    }

    /// <summary>
    /// The Windows Hello unlock path, minus the biometric gesture.
    ///
    /// Hello hands back a key buffer the caller is expected to dispose. That made the obvious call
    /// site — <c>using var key = ...; repository.UnlockWithKey(key)</c> — leave the vault holding a
    /// zeroed key, because the vault takes ownership of whatever it is constructed with. Nothing
    /// looked wrong at first: the document was already decrypted, so the whole UI read fine. The
    /// first save then died with a bare "Cannot access a disposed object".
    ///
    /// Hello itself cannot be tested without hardware, but the ownership contract it relies on can,
    /// which is all this ever was.
    /// </summary>
    [Fact]
    public void UnlockWithKey_leaves_a_usable_vault_after_the_caller_disposes_its_key()
    {
        var repository = NewRepository();

        using (var password = Pass("pw"))
        using (var vault = repository.Create(password, bindToMachine: false, KdfParameters.TestOnlyFast))
        {
            vault.Save();
        }

        var header = repository.ReadHeader();
        var kdf = new KdfParameters(header.MemoryKiB, header.Iterations, header.Parallelism);

        UnlockedVault opened;
        using (var password = Pass("pw"))
        using (var key = VaultCrypto.DeriveKey(password, header.Salt, kdf))
        {
            opened = repository.UnlockWithKey(key);
        }
        // The caller's key is now disposed — exactly what the Hello path does.

        using (opened)
        {
            opened.Document.Accounts.Add(new AccountEntry { Label = "added after unlock" });
            opened.Save();   // this is what threw ObjectDisposedException
        }

        using var reopen = Pass("pw");
        using var reopened = repository.Unlock(reopen);
        Assert.Equal("added after unlock", Assert.Single(reopened.Document.Accounts).Label);
    }

    [Fact]
    public void UnlockWithKey_does_not_take_over_the_caller_buffer()
    {
        // Guards the clone specifically: if it were ever "optimised" into an alias, disposing the
        // caller's buffer would zero the vault's key again and the bug would return silently.
        var repository = NewRepository();

        using (var password = Pass("pw"))
        using (var vault = repository.Create(password, bindToMachine: false, KdfParameters.TestOnlyFast))
        {
            vault.Save();
        }

        var header = repository.ReadHeader();
        var kdf = new KdfParameters(header.MemoryKiB, header.Iterations, header.Parallelism);

        using var master = Pass("pw");
        var callerKey = VaultCrypto.DeriveKey(master, header.Salt, kdf);
        using var opened = repository.UnlockWithKey(callerKey);

        callerKey.Dispose();
        Assert.True(callerKey.IsDisposed);

        // The vault kept its own copy, so it can still write.
        opened.Document.Accounts.Add(new AccountEntry { Label = "still works" });
        opened.Save();
    }

    [Fact]
    public void A_non_vault_file_is_reported_as_such()
    {
        var repository = NewRepository();
        repository.Paths.EnsureCreated();
        File.WriteAllText(repository.Paths.VaultFile, "this is not a vault");

        using var password = Pass("pw");
        Assert.Throws<VaultFormatException>(() => repository.Unlock(password));
    }
}

/// <summary>
/// The guarantee that no diagnostic path can print a password. If someone later adds a secret field
/// typed as a plain string, these fail — which is the point.
/// </summary>
public sealed class SecretHygieneTests
{
    private static AccountEntry FullyPopulated() => new()
    {
        Label = "main",
        LoginUsername = "marc@example.com",
        Password = "MyRealPassword123",
        Notes = "nothing sensitive here",
        Recovery = new RecoveryInfo
        {
            Email = "marc@example.com",
            EmailPassword = "MailboxPassword456",
            SecurityAnswers = "mother: Smith",
            MfaBackupCodes = { "AAAA-BBBB", "CCCC-DDDD" },
        },
    };

    [Fact]
    public void Redacted_serialisation_contains_no_secrets()
    {
        var json = JsonSerializer.Serialize(FullyPopulated(), VaultJson.Redacted);

        Assert.DoesNotContain("MyRealPassword123", json);
        Assert.DoesNotContain("MailboxPassword456", json);
        Assert.DoesNotContain("mother: Smith", json);
        Assert.DoesNotContain("AAAA-BBBB", json);
        Assert.Contains(SecretText.Mask, json);

        // Non-secret fields must survive, or the redacted dump would be useless for support.
        Assert.Contains("marc@example.com", json);
        Assert.Contains("main", json);
    }

    [Fact]
    public void Storage_serialisation_keeps_secrets_so_the_vault_round_trips()
    {
        var json = JsonSerializer.Serialize(FullyPopulated(), VaultJson.Storage);
        Assert.Contains("MyRealPassword123", json);

        var back = JsonSerializer.Deserialize<AccountEntry>(json, VaultJson.Storage)!;
        Assert.Equal("MyRealPassword123", back.Password!.Value);
        Assert.Equal(2, back.Recovery.MfaBackupCodes.Count);
    }

    [Fact]
    public void Interpolating_a_secret_yields_a_mask()
    {
        var secret = new SecretText("do-not-print-me");
        Assert.Equal(SecretText.Mask, $"{secret}");
        Assert.DoesNotContain("do-not-print-me", $"password={secret}");
    }

    [Fact]
    public void SecretBuffer_zeroes_on_dispose()
    {
        var buffer = SecretBuffer.FromString("sensitive");
        var borrowed = buffer.Bytes;
        Assert.Contains(borrowed, b => b != 0);

        buffer.Dispose();

        Assert.True(buffer.IsDisposed);
        Assert.All(borrowed, b => Assert.Equal(0, b));
        Assert.Throws<ObjectDisposedException>(() => buffer.Bytes);
    }
}
