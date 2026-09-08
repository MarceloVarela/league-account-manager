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

/// <summary>Whether anyone other than you could still recover the account.</summary>
public sealed class RecoveryExposureTests
{
    [Fact]
    public void An_uncontrolled_mailbox_is_the_check_that_fails()
    {
        var account = new AccountEntry { Label = "bought" };
        account.Recovery.Email = "someone.else@example.com";
        account.Recovery.EmailStillControlled = false;

        var checks = Provenance.RecoveryExposure(account);

        var mailbox = checks.First(c => c.Title.Contains("Registered email"));
        Assert.False(mailbox.Passed);
        Assert.Contains("recovery", mailbox.Why);
    }

    [Fact]
    public void Every_check_can_actually_be_satisfied()
    {
        // The password-change check used to compare against a date the app had no way to set, so it
        // could never do anything but tell you to set it. A check that cannot pass is worse than no
        // check: it trains the reader to ignore the section.
        var account = new AccountEntry { Label = "mine" };
        account.Recovery.Email = "me@example.com";
        account.Recovery.ApproximateCreated = new DateOnly(2012, 10, 29);
        account.Recovery.MfaEnabled = true;
        account.Identity.Observed = new ClientAccountFacts { PhoneOnFile = true };

        Assert.All(Provenance.RecoveryExposure(account), check => Assert.True(check.Passed, check.Title));
    }
}
