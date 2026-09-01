using LAM.Core.Model;
using LAM.Core.Vault;
using Xunit;

namespace LAM.Tests;

/// <summary>
/// Deletion that can be taken back.
///
/// Removing an account used to be instant and final, and it took the password, the saved session and
/// the whole recovery dossier — the only things in this vault that signing in again cannot rebuild.
/// </summary>
public sealed class TrashTests
{
    private static VaultDocument DocumentWith(params AccountEntry[] accounts)
        => new() { Accounts = [.. accounts] };

    [Fact]
    public void A_trashed_account_disappears_from_the_live_list_but_not_from_the_vault()
    {
        var account = new AccountEntry { Label = "smurf", Password = "pw" };
        var document = DocumentWith(account, new AccountEntry { Label = "main" });

        account.DeletedUtc = DateTimeOffset.UtcNow;

        Assert.Single(document.Live);
        Assert.Single(document.Trashed);
        Assert.Equal(2, document.Accounts.Count);

        // And the thing that made deletion irreversible is still there.
        Assert.Equal("pw", document.Trashed.Single().Password!.Value);
    }

    [Fact]
    public void Search_never_returns_a_trashed_account()
    {
        // Otherwise a deleted account would reappear the moment you typed its name, which defeats
        // the point of having deleted it.
        var account = new AccountEntry { Label = "smurf", DeletedUtc = DateTimeOffset.UtcNow };
        var document = DocumentWith(account, new AccountEntry { Label = "main" });

        Assert.Empty(document.Search("smurf"));
        Assert.Single(document.Search("main"));
    }

    [Fact]
    public void A_trashed_accounts_tags_leave_the_filter_bar()
    {
        var document = DocumentWith(
            new AccountEntry { Label = "a", Tags = ["ranked"], DeletedUtc = DateTimeOffset.UtcNow },
            new AccountEntry { Label = "b", Tags = ["casual"] });

        Assert.Equal(["casual"], document.AllTags());
    }

    [Fact]
    public void Restoring_puts_it_back_untouched()
    {
        var account = new AccountEntry { Label = "smurf", Password = "pw", DeletedUtc = DateTimeOffset.UtcNow };
        var document = DocumentWith(account);

        account.DeletedUtc = null;

        Assert.Single(document.Live);
        Assert.Equal("pw", document.Live.Single().Password!.Value);
    }
}

/// <summary>Keeping the password you just replaced, in case the replacement was mistyped.</summary>
public sealed class PasswordHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Replacing_a_password_keeps_the_old_one()
    {
        var account = new AccountEntry { Label = "main", Password = "old-one" };

        account.SetPassword("new-one", Now);

        Assert.Equal("new-one", account.Password!.Value);
        Assert.Equal("old-one", Assert.Single(account.PasswordHistory).Password!.Value);
    }

    [Fact]
    public void Saving_without_changing_anything_does_not_pile_up_duplicates()
    {
        // Opening the editor to change a tag and pressing save must not push a history entry, or the
        // history fills with copies of the current password and becomes useless.
        var account = new AccountEntry { Label = "main", Password = "same" };

        account.SetPassword("same", Now);
        account.SetPassword("same", Now);

        Assert.Empty(account.PasswordHistory);
    }

    [Fact]
    public void Setting_a_first_password_records_no_history()
    {
        var account = new AccountEntry { Label = "main" };

        account.SetPassword("first", Now);

        Assert.Empty(account.PasswordHistory);
    }

    [Fact]
    public void Newest_replaced_password_comes_first_and_the_list_is_bounded()
    {
        var account = new AccountEntry { Label = "main", Password = "p0" };

        for (var i = 1; i <= 15; i++) account.SetPassword("p" + i, Now.AddDays(i));

        Assert.Equal(10, account.PasswordHistory.Count);
        Assert.Equal("p14", account.PasswordHistory[0].Password!.Value);
    }

    [Fact]
    public void Clearing_the_password_still_keeps_what_was_there()
    {
        var account = new AccountEntry { Label = "main", Password = "was-here" };

        account.SetPassword(null, Now);

        Assert.Null(account.Password);
        Assert.Equal("was-here", Assert.Single(account.PasswordHistory).Password!.Value);
    }
}

/// <summary>What the vault can tell you about its own condition.</summary>
public sealed class VaultHealthTests
{
    private static AccountEntry With(string label, string? password)
        => new() { Label = label, Password = password is null ? null : new SecretText(password) };

    [Fact]
    public void Accounts_sharing_a_password_are_reported_together()
    {
        // The realistic disaster for a pile of smurfs: one leaked pair, tried everywhere.
        var findings = VaultHealth.ReusedPasswords(
            [With("a", "shared"), With("b", "shared"), With("c", "unique")]);

        var finding = Assert.Single(findings);
        Assert.Equal(HealthSeverity.Serious, finding.Severity);
        Assert.Contains("2 accounts share one password", finding.Title);
        Assert.Contains("a", finding.Detail);
        Assert.Contains("b", finding.Detail);
    }

    [Fact]
    public void The_finding_never_contains_the_password_itself()
    {
        // A health report that quotes the secret is a worse artefact than the problem it describes.
        var findings = VaultHealth.ReusedPasswords([With("a", "hunter2"), With("b", "hunter2")]);

        Assert.DoesNotContain("hunter2", findings.Single().ToString());
    }

    [Fact]
    public void Distinct_passwords_raise_nothing()
        => Assert.Empty(VaultHealth.ReusedPasswords([With("a", "one"), With("b", "two")]));

    [Fact]
    public void Accounts_without_a_password_are_not_treated_as_sharing_an_empty_one()
    {
        // Three accounts with no password are three gaps, not a shared credential.
        Assert.Empty(VaultHealth.ReusedPasswords([With("a", null), With("b", null), With("c", null)]));
    }

    [Fact]
    public void An_account_with_no_way_in_is_flagged()
    {
        var document = new VaultDocument { Accounts = [With("stranded", null)] };

        var findings = VaultHealth.Inspect(document, [], DateTimeOffset.UtcNow);

        Assert.Contains(findings, f => f.Title.Contains("cannot be signed into"));
    }

    [Fact]
    public void Backups_on_the_same_drive_as_the_vault_are_called_out()
    {
        var backups = new List<FileInfo> { new(Path.Combine(Path.GetTempPath(), "vault-x.dat")) };

        var findings = VaultHealth.BackupFindings(
            backups, DateTimeOffset.UtcNow, @"C:\vault\backups", @"C:\vault").ToList();

        Assert.Contains(findings, f => f.Title.Contains("same drive"));
    }

    [Fact]
    public void Trashed_accounts_are_mentioned_so_they_are_not_forgotten()
    {
        var document = new VaultDocument
        {
            Accounts = [new AccountEntry { Label = "gone", DeletedUtc = DateTimeOffset.UtcNow }],
        };

        var findings = VaultHealth.Inspect(document, [], DateTimeOffset.UtcNow);

        Assert.Contains(findings, f => f.Title.Contains("in the trash"));
    }

    [Fact]
    public void The_worst_findings_come_first()
    {
        var document = new VaultDocument
        {
            Accounts =
            [
                With("a", "shared"),
                With("b", "shared"),
                new AccountEntry { Label = "gone", DeletedUtc = DateTimeOffset.UtcNow },
            ],
        };

        var findings = VaultHealth.Inspect(document, [], DateTimeOffset.UtcNow);

        Assert.Equal(HealthSeverity.Serious, findings[0].Severity);
    }
}
