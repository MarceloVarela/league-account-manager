using LAM.Core.Fleet;
using LAM.Core.Model;
using LAM.Core.Riot;
using Xunit;

namespace LAM.Tests;

/// <summary>
/// Exporting the fleet. The export is the one artefact that leaves the vault, so what it must not
/// contain matters more than what it does.
/// </summary>
public sealed class FleetExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lam-export", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private SkinCatalogue Catalogue()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "catalogue.json"), """
            {"SchemaVersion":3,"CapturedUtc":"2026-01-01T00:00:00+00:00","Champions":[{"Id":86,"Name":"Garen"}],
             "Skins":[{"Id":86001,"Name":"Desert Trooper Garen","ChampionId":86,"RpCost":520,"IsAvailable":true}]}
            """);

        var catalogue = new SkinCatalogue(_root);
        Assert.True(catalogue.TryLoad());
        return catalogue;
    }

    private static AccountEntry Loaded()
    {
        var account = new AccountEntry
        {
            Label = "main",
            LoginUsername = "loginname",
            Password = "SuperSecret123",
            Region = "BR",
            Tags = ["ranked"],
        };

        account.Recovery.Email = "me@example.com";
        account.Recovery.EmailPassword = "MailboxSecret";
        account.Recovery.MfaBackupCodes = [new SecretText("AAAA-1111")];
        account.Identity.GameName = "Testplayer";
        account.Identity.TagLine = "00000";
        account.Identity.SummonerLevel = 625;
        account.Identity.OwnedSkinIds = [86001];
        account.Session = new StoredSession
        {
            Yaml = new SecretText("refresh_token: \"TOKENVALUE\""),
            CapturedUtc = DateTimeOffset.UtcNow,
        };

        return account;
    }

    [Fact]
    public void No_secret_of_any_kind_reaches_the_export()
    {
        var csv = FleetExport.ToCsv([Loaded()], Catalogue(), DateTimeOffset.UtcNow);

        Assert.DoesNotContain("SuperSecret123", csv);
        Assert.DoesNotContain("MailboxSecret", csv);
        Assert.DoesNotContain("AAAA-1111", csv);
        Assert.DoesNotContain("TOKENVALUE", csv);
        Assert.DoesNotContain("me@example.com", csv);
    }

    [Fact]
    public void The_useful_columns_are_all_there()
    {
        var csv = FleetExport.ToCsv([Loaded()], Catalogue(), DateTimeOffset.UtcNow);

        Assert.Contains("Testplayer#00000", csv);
        Assert.Contains("625", csv);
        Assert.Contains("520", csv);   // RP at listed prices
        Assert.Contains("ranked", csv);
    }

    [Fact]
    public void A_label_containing_a_comma_or_a_quote_cannot_break_a_row()
    {
        // A spreadsheet silently shifting every column is the kind of damage nobody notices until
        // the export is the only copy left.
        var account = Loaded();
        account.Label = "main, \"the\" one";

        var csv = FleetExport.ToCsv([account], Catalogue(), DateTimeOffset.UtcNow);
        var rows = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, rows.Length);
        Assert.Contains("\"main, \"\"the\"\" one\"", csv);
    }

    [Fact]
    public void A_trashed_account_is_not_exported()
    {
        var account = Loaded();
        account.DeletedUtc = DateTimeOffset.UtcNow;

        var rows = FleetExport.ToCsv([account], Catalogue(), DateTimeOffset.UtcNow)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Single(rows);   // the header only
    }
}

/// <summary>Hiding names while something is recording the screen.</summary>
public sealed class StreamerModeTests
{
    [Fact]
    public void A_riot_id_is_masked_but_still_tells_cards_apart()
    {
        // Unusable for finding the account, still usable for knowing which card you are looking at.
        var redacted = StreamerMode.Redact("Testplayer#00000");

        Assert.StartsWith("T", redacted);
        Assert.EndsWith("#•••", redacted);
        Assert.DoesNotContain("estplayer", redacted);
        Assert.DoesNotContain("00000", redacted);
    }

    [Fact]
    public void A_name_without_a_tag_is_still_masked()
    {
        var redacted = StreamerMode.Redact("loginname");

        Assert.StartsWith("l", redacted);
        Assert.DoesNotContain("oginname", redacted);
        Assert.DoesNotContain("#", redacted);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Nothing_to_redact_still_yields_something_printable(string? value)
        => Assert.False(string.IsNullOrWhiteSpace(StreamerMode.Redact(value)));

    [Fact]
    public void A_single_character_name_does_not_leak_by_being_too_short_to_mask()
        => Assert.Equal("•", StreamerMode.Redact("x"));

    [Fact]
    public void The_mask_length_does_not_reveal_a_long_name()
    {
        // Capped, so a 30-character name and an 8-character one look the same.
        Assert.Equal(
            StreamerMode.Redact("abcdefghijklmnopqrstuvwxyz").Length,
            StreamerMode.Redact("abcdefgh").Length);
    }
}

/// <summary>Spotting a lockfile that outlived the process which wrote it.</summary>
public sealed class ClientRepairTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lam-repair", Guid.NewGuid().ToString("N"));

    public ClientRepairTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string Lockfile(string contents)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".lockfile");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void A_lockfile_naming_a_dead_process_is_stale()
    {
        // The interesting failure: the file says "listening on port N" and nothing is, so every read
        // fails in a way that looks like the client refusing to answer.
        Assert.True(ClientRepair.IsLockfileStale(Lockfile("LeagueClient:999999:52001:abcdef:https")));
    }

    [Fact]
    public void A_lockfile_naming_a_live_process_is_not_stale()
    {
        var path = Lockfile("LeagueClient:" + Environment.ProcessId + ":52001:abcdef:https");

        Assert.False(ClientRepair.IsLockfileStale(path));
    }

    [Fact]
    public void An_unparseable_lockfile_counts_as_stale()
    {
        // Truncated, or written by a client version with a different shape. Either way it cannot
        // yield a port, so treating it as usable would just move the failure somewhere less obvious.
        Assert.True(ClientRepair.IsLockfileStale(Lockfile("garbage")));
    }

    [Fact]
    public void A_lockfile_that_is_not_there_is_not_stale()
        => Assert.False(ClientRepair.IsLockfileStale(Path.Combine(_root, "absent")));

    [Fact]
    public void A_zero_pid_is_treated_as_dead()
        => Assert.True(ClientRepair.IsLockfileStale(Lockfile("LeagueClient:0:52001:abcdef:https")));
}
