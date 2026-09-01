using System.Text.Json;
using LAM.Core.Fleet;
using LAM.Core.Model;
using LAM.Core.Riot;
using Xunit;

namespace LAM.Tests;

/// <summary>
/// Reading acquisition dates off the catalogue, where the unit is the trap.
/// </summary>
public sealed class AcquisitionDateTests
{
    [Fact]
    public void The_catalogue_reports_seconds_not_milliseconds()
    {
        // 1371662514 is 19 June 2013 read as seconds, and 16 January 1970 read as milliseconds. The
        // champions endpoint genuinely uses milliseconds for its own purchase dates, so treating the
        // two alike would date an entire collection to 1970 and look like corrupt data.
        using var document = JsonDocument.Parse(
            """[{"itemId":12002,"name":"Golden Alistar","owned":true,"purchaseDate":1371662514}]""");

        var dates = ClientStatsProbe.ReadSkinAcquisitionDates(document);

        var account = new AccountEntry { Label = "main" };
        account.Identity.SkinAcquiredEpochSeconds = dates;

        Assert.Equal(2013, account.Identity.SkinAcquiredUtc(12002)!.Value.UtcDateTime.Year);
    }

    [Fact]
    public void Only_owned_items_get_a_date()
    {
        using var document = JsonDocument.Parse("""
            [{"itemId":1,"owned":true,"purchaseDate":1371662514},
             {"itemId":2,"owned":false,"purchaseDate":1371662514},
             {"itemId":3,"owned":true,"purchaseDate":0}]
            """);

        var dates = ClientStatsProbe.ReadSkinAcquisitionDates(document);

        Assert.Equal([1], dates.Keys);
    }

    [Fact]
    public void An_endpoint_that_returns_nothing_does_not_erase_what_was_captured()
    {
        // Provenance is expensive to rebuild — it needs a sign-in — so a capture that simply could
        // not read the catalogue must leave the existing dates alone.
        var account = new AccountEntry { Label = "main" };
        new ClientStats
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            OwnedSkinIds = [12002],
            OwnedChampionIds = [12],
            CollectionIsComplete = true,
            SkinAcquiredEpochSeconds = new Dictionary<int, int> { [12002] = 1371662514 },
        }.ApplyTo(account);

        new ClientStats
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            OwnedSkinIds = [12002],
            OwnedChampionIds = [12],
            CollectionIsComplete = true,
        }.ApplyTo(account);

        Assert.Single(account.Identity.SkinAcquiredEpochSeconds);
    }
}

/// <summary>Telling what the previous owner did from what you did.</summary>
public sealed class OwnershipSplitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lam-prov", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private SkinCatalogue Catalogue()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "catalogue.json"), """
            {"SchemaVersion":3,"CapturedUtc":"2026-01-01T00:00:00+00:00","Champions":[{"Id":99,"Name":"Lux"}],
             "Skins":[
               {"Id":99001,"Name":"Old Skin","ChampionId":99,"RpCost":1350,"IsAvailable":true},
               {"Id":99002,"Name":"New Skin","ChampionId":99,"RpCost":1350,"IsAvailable":true},
               {"Id":12002,"Name":"Golden Alistar","ChampionId":12,"IsAvailable":false}
             ]}
            """);

        var catalogue = new SkinCatalogue(_root);
        Assert.True(catalogue.TryLoad());
        return catalogue;
    }

    private static AccountEntry Bought()
    {
        var account = new AccountEntry { Label = "bought" };
        account.Identity.OwnedSkinIds = [99001, 99002, 12002];
        account.Identity.SkinAcquiredEpochSeconds = new Dictionary<int, int>
        {
            [99001] = (int)new DateTimeOffset(2015, 3, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
            [99002] = (int)new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
        };
        account.Recovery.OwnedSince = new DateOnly(2020, 1, 1);
        return account;
    }

    [Fact]
    public void Skins_are_split_at_the_date_the_account_changed_hands()
    {
        var split = Provenance.SplitByOwnership(Bought(), Catalogue())!;

        Assert.Equal("Old Skin", Assert.Single(split.BeforeYou).Name);
        Assert.Equal("New Skin", Assert.Single(split.SinceYou).Name);
    }

    [Fact]
    public void A_skin_with_no_recorded_date_is_counted_rather_than_assigned_to_a_side()
    {
        // Putting an undated skin on either side would be a guess presented as a fact.
        var split = Provenance.SplitByOwnership(Bought(), Catalogue())!;

        Assert.Equal(1, split.Undated);
        Assert.Contains("no recorded date", split.Describe());
    }

    [Fact]
    public void An_account_you_have_always_owned_has_no_split_at_all()
    {
        var account = Bought();
        account.Recovery.OwnedSince = null;

        Assert.Null(Provenance.SplitByOwnership(account, Catalogue()));
    }

    [Fact]
    public void The_first_skin_is_the_oldest_dated_one()
    {
        var first = Provenance.FirstSkin(Bought())!;

        Assert.Equal(99001, first.Value.SkinId);
        Assert.Equal(2015, first.Value.AcquiredUtc.UtcDateTime.Year);
    }

    [Fact]
    public void A_jade_duplicate_is_never_reported_as_the_first_skin()
    {
        var account = Bought();
        account.Identity.SkinAcquiredEpochSeconds[60001008] =
            (int)new DateTimeOffset(2011, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        Assert.Equal(99001, Provenance.FirstSkin(account)!.Value.SkinId);
    }
}

/// <summary>Whether a bought account could still be taken back.</summary>
public sealed class RecallExposureTests
{
    [Fact]
    public void An_uncontrolled_mailbox_is_the_check_that_fails()
    {
        var account = new AccountEntry { Label = "bought" };
        account.Recovery.Email = "seller@example.com";
        account.Recovery.EmailStillControlled = false;

        var checks = Provenance.RecallExposure(account);

        var mailbox = checks.First(c => c.Title.Contains("Registered email"));
        Assert.False(mailbox.Passed);
        Assert.Contains("recovery", mailbox.Why);
    }

    [Fact]
    public void A_password_changed_before_you_owned_it_is_flagged()
    {
        var account = new AccountEntry { Label = "bought" };
        account.Recovery.OwnedSince = new DateOnly(2024, 1, 1);
        account.Identity.Observed = new ClientAccountFacts
        {
            PasswordChangedUtc = new DateTimeOffset(2019, 9, 6, 0, 0, 0, TimeSpan.Zero),
        };

        var check = Provenance.RecallExposure(account).First(c => c.Title.Contains("Password changed"));

        Assert.False(check.Passed);
    }

    [Fact]
    public void A_password_changed_after_you_took_over_passes()
    {
        var account = new AccountEntry { Label = "bought" };
        account.Recovery.OwnedSince = new DateOnly(2024, 1, 1);
        account.Identity.Observed = new ClientAccountFacts
        {
            PasswordChangedUtc = new DateTimeOffset(2025, 2, 2, 0, 0, 0, TimeSpan.Zero),
        };

        Assert.True(Provenance.RecallExposure(account).First(c => c.Title.Contains("Password changed")).Passed);
    }

    [Fact]
    public void Without_an_owned_since_date_the_password_check_asks_for_one_instead_of_failing()
    {
        // An account you made yourself should not be told it has a problem it cannot have.
        var account = new AccountEntry { Label = "mine" };

        var check = Provenance.RecallExposure(account).First(c => c.Title.Contains("Password changed"));

        Assert.True(check.Passed);
        Assert.Contains("owned since", check.Why);
    }
}
