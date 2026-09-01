using System.Diagnostics;
using LAM.Core.Model;
using LAM.Core.Recovery;
using LAM.Core.Riot;
using LAM.Core.Vault;
using Xunit;
using Xunit.Abstractions;

namespace LAM.Tests;

/// <summary>
/// The rest of the suite uses deliberately weak Argon2 parameters so it runs fast. That leaves a gap:
/// nothing would catch a production cost setting that is wrong, unusable, or so slow the unlock feels
/// broken. This closes it by exercising the real defaults exactly once.
/// </summary>
public sealed class ProductionConfigTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lam-prod", Guid.NewGuid().ToString("N"));

    public ProductionConfigTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void The_real_kdf_settings_round_trip_and_unlock_quickly_enough_to_feel_instant()
    {
        var repository = new VaultRepository(new VaultPaths(_root));

        using (var password = SecretBuffer.FromString("a realistic master password"))
        using (var vault = repository.Create(password, bindToMachine: true))   // production defaults
        {
            vault.Document.Accounts.Add(new AccountEntry
            {
                Label = "main",
                LoginUsername = "marc@example.com",
                Password = "a-real-looking-password",
                Region = "BR",
            });
            vault.Save();
        }

        var stopwatch = Stopwatch.StartNew();
        using var reopen = SecretBuffer.FromString("a realistic master password");
        using var reopened = repository.Unlock(reopen);
        stopwatch.Stop();

        _output.WriteLine("Argon2id unlock took " + stopwatch.ElapsedMilliseconds + " ms at "
                          + KdfParameters.Default.MemoryKiB / 1024 + " MiB, t="
                          + KdfParameters.Default.Iterations + ", p=" + KdfParameters.Default.Parallelism);

        Assert.Equal("a-real-looking-password", Assert.Single(reopened.Document.Accounts).Password!.Value);

        // Slow enough to punish offline guessing, fast enough that a person does not think it hung.
        // A generous ceiling: the point is to catch a mis-set cost, not to benchmark the CI machine.
        Assert.InRange(stopwatch.ElapsedMilliseconds, 0, 5000);
    }

    [Fact]
    public void The_production_kdf_parameters_are_not_accidentally_weak()
    {
        var parameters = KdfParameters.Default;

        parameters.Validate();                                  // throws if implausible
        Assert.True(parameters.MemoryKiB >= 19 * 1024, "OWASP's Argon2id floor is 19 MiB.");
        Assert.True(parameters.Iterations >= 2);
        Assert.NotEqual(KdfParameters.TestOnlyFast, parameters); // the test setting must never ship
    }
}

/// <summary>The recovery dossier: what it includes, and — more importantly — what it withholds.</summary>
public sealed class RecoverySheetTests
{
    private static AccountEntry Populated()
    {
        var account = new AccountEntry
        {
            Label = "main",
            LoginUsername = "marc@example.com",
            Password = "AccountPassword123",
            Region = "BR",
            Recovery = new RecoveryInfo
            {
                Email = "marc@example.com",
                EmailPassword = "MailboxPassword456",
                ApproximateCreated = new DateOnly(2014, 7, 2),
                FirstChampionPurchased = "Ashe",
                FirstPurchaseReference = "order 12345",
                MfaEnabled = true,
                MfaBackupCodes = { "AAAA-1111", "BBBB-2222" },
                SecurityAnswers = "mother: Smith",
            },
        };

        account.Identity.RecordName("Caio", "BR1", DateTimeOffset.UtcNow.AddYears(-2));
        account.Identity.RecordName("CaioRenamed", "BR1", DateTimeOffset.UtcNow);
        account.Identity.RiotAccountId = "11111111-2222-3333-4444-555555555555";
        account.Identity.SummonerLevel = 214;
        return account;
    }

    [Fact]
    public void By_default_the_sheet_carries_no_secrets()
    {
        var sheet = RecoverySheet.Render(Populated(), includeSecrets: false);

        // The common use is pasting this into a support ticket, so the default must be safe.
        Assert.DoesNotContain("AccountPassword123", sheet);
        Assert.DoesNotContain("MailboxPassword456", sheet);
        Assert.DoesNotContain("AAAA-1111", sheet);
        Assert.DoesNotContain("mother: Smith", sheet);

        // But the evidence Riot actually asks for must be there.
        Assert.Contains("11111111-2222-3333-4444-555555555555", sheet);
        Assert.Contains("marc@example.com", sheet);
        Assert.Contains("Ashe", sheet);
        Assert.Contains("order 12345", sheet);
        Assert.Contains("2 July 2014", sheet);
    }

    [Fact]
    public void Opting_in_includes_the_secrets()
    {
        var sheet = RecoverySheet.Render(Populated(), includeSecrets: true);

        Assert.Contains("AccountPassword123", sheet);
        Assert.Contains("MailboxPassword456", sheet);
        Assert.Contains("AAAA-1111", sheet);
    }

    [Fact]
    public void Rename_history_is_reported_because_puuid_outlives_every_name()
    {
        var sheet = RecoverySheet.Render(Populated());

        Assert.Contains("Previous Riot IDs", sheet);
        Assert.Contains("Caio#BR1", sheet);
        Assert.Contains("CaioRenamed#BR1", sheet);
    }

    [Fact]
    public void An_empty_account_lists_the_gaps_rather_than_looking_complete()
    {
        var bare = new AccountEntry { Label = "bought this one" };
        var gaps = RecoverySheet.MissingFields(bare).ToList();

        Assert.Contains(gaps, g => g.Contains("Registered email"));
        Assert.Contains(gaps, g => g.Contains("Riot account id"));
        Assert.Contains(gaps, g => g.Contains("created"));

        // Section headings are emitted upper-case by Render.
        var sheet = RecoverySheet.Render(bare);
        Assert.Contains("GAPS WORTH FILLING IN", sheet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_lost_mailbox_is_called_out_specifically()
    {
        var account = new AccountEntry
        {
            Recovery = new RecoveryInfo { Email = "dead@example.com", EmailStillControlled = false },
        };

        Assert.Contains(RecoverySheet.MissingFields(account), g => g.Contains("no longer control"));
        Assert.Contains("NO — flagged", RecoverySheet.Render(account));
    }

    [Fact]
    public void Two_factor_without_backup_codes_is_flagged_as_a_real_risk()
    {
        var account = new AccountEntry { Recovery = new RecoveryInfo { MfaEnabled = true } };

        Assert.Contains(RecoverySheet.MissingFields(account),
            g => g.Contains("lose the authenticator"));
    }

    [Fact]
    public void An_age_at_the_coverage_boundary_is_reported_as_a_floor_not_a_birthday()
    {
        var account = new AccountEntry();
        account.Identity.OldestKnownMatchUtc = RiotApiClient.MatchHistoryCoverageStart.AddDays(20);
        account.Identity.OldestKnownMatchId = "BR1_1234567890";
        account.Identity.AgeIsLowerBoundOnly = true;

        var sheet = RecoverySheet.Render(account);

        Assert.Contains("AT LEAST", sheet);
        Assert.Contains("may be considerably older", sheet);
    }
}

public sealed class RiotRegionsTests
{
    [Theory]
    [InlineData("BR", "br1", "americas")]
    [InlineData("NA", "na1", "americas")]
    [InlineData("EUW", "euw1", "europe")]
    [InlineData("KR", "kr", "asia")]
    [InlineData("OCE", "oc1", "sea")]
    public void Maps_a_region_to_both_of_the_routes_riot_needs(string region, string platform, string regional)
    {
        // Mixing these up returns a 404 that reads like "no such account", so it is worth pinning.
        Assert.Equal(platform, RiotRegions.PlatformFor(region));
        Assert.Equal(regional, RiotRegions.RegionalFor(region));
    }

    [Fact]
    public void An_unknown_region_routes_nowhere_rather_than_guessing()
    {
        Assert.Null(RiotRegions.PlatformFor("ATLANTIS"));
        Assert.Null(RiotRegions.RegionalFor(null));
        Assert.False(RiotRegions.IsKnown("ATLANTIS"));
    }

    [Fact]
    public void Region_lookup_is_case_insensitive()
        => Assert.Equal("br1", RiotRegions.PlatformFor("br"));
}
