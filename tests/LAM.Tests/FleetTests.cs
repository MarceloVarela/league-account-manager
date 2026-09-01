using LAM.Core.Fleet;
using LAM.Core.Model;
using LAM.Core.Riot;
using Xunit;

namespace LAM.Tests;

/// <summary>
/// The cross-account questions — the ones no single-account tool can answer, and the reason a vault
/// holding every account is worth more than the sum of its entries.
/// </summary>
public sealed class FleetOwnershipTests
{
    private static AccountEntry Account(string label, params int[] skins)
    {
        var account = new AccountEntry { Label = label };
        account.Identity.OwnedSkinIds = skins;
        return account;
    }

    private static readonly CatalogueSkin Lux = new(99001, "Elementalist Lux", 99, null, null, null, 3250);

    [Fact]
    public void Ownership_splits_into_who_has_it_and_where_to_buy_it()
    {
        var accounts = new[] { Account("main", 99001, 78002), Account("smurf", 78002) };

        var result = FleetAnalysis.WhoOwns(accounts, Lux);

        Assert.True(result.OwnedAnywhere);
        Assert.Equal("main", Assert.Single(result.Owners).Label);
        Assert.Equal("smurf", Assert.Single(result.Without).Label);
    }

    [Fact]
    public void An_account_nobody_has_read_is_in_neither_column()
    {
        // The important case. An account never signed into knows nothing about its own collection,
        // and listing it under "does not own" would send you to buy a skin you may already have.
        var accounts = new[] { Account("main", 78002), new AccountEntry { Label = "never used" } };

        var catalogue = new SkinCatalogue(Path.Combine(Path.GetTempPath(), "lam-fleet-" + Guid.NewGuid().ToString("N")));
        var results = FleetAnalysis.Search(accounts, catalogue, "Lux");

        // No catalogue loaded, so no matches — but the filtering rule is what is under test here.
        Assert.Empty(results);
        Assert.True(FleetAnalysis.HasCollection(accounts[0]));
        Assert.False(FleetAnalysis.HasCollection(accounts[1]));
    }

    [Fact]
    public void A_skin_owned_nowhere_still_returns_a_row()
    {
        // "Nobody has this" is a useful answer, not an empty result.
        var result = FleetAnalysis.WhoOwns([Account("main", 78002)], Lux);

        Assert.False(result.OwnedAnywhere);
        Assert.Single(result.Without);
    }
}

/// <summary>
/// Expressing a collection in RP without inventing numbers, and without implying a sale price.
/// </summary>
public sealed class CollectionValueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lam-value", Guid.NewGuid().ToString("N"));

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
               {"Id":99001,"Name":"Elementalist Lux","ChampionId":99,"RpCost":3250,"IsAvailable":true},
               {"Id":99002,"Name":"Lunar Empress Lux","ChampionId":99,"RpCost":1350,"IsAvailable":true},
               {"Id":12002,"Name":"Golden Alistar","ChampionId":12,"IsAvailable":false}
             ]}
            """);

        var catalogue = new SkinCatalogue(_root);
        Assert.True(catalogue.TryLoad());
        return catalogue;
    }

    private static AccountEntry With(params int[] skins)
    {
        var account = new AccountEntry { Label = "main" };
        account.Identity.OwnedSkinIds = skins;
        return account;
    }

    [Fact]
    public void Priced_skins_are_summed_and_unpriced_ones_are_counted_not_guessed()
    {
        // An unavailable skin has no listed price. Substituting a plausible number would turn a real
        // figure into a fabricated one, so it is reported as unknown instead.
        var value = FleetAnalysis.ValueOf(With(99001, 99002, 12002), Catalogue());

        Assert.Equal(4600, value.KnownRp);
        Assert.Equal(2, value.PricedSkins);
        Assert.Equal(1, value.UnpricedSkins);
        Assert.Equal(1, value.LegacySkins);
    }

    [Fact]
    public void A_skin_the_catalogue_has_never_seen_counts_as_unpriced_rather_than_free()
    {
        var value = FleetAnalysis.ValueOf(With(99001, 555999), Catalogue());

        Assert.Equal(3250, value.KnownRp);
        Assert.Equal(1, value.UnpricedSkins);
    }

    [Fact]
    public void The_wording_never_implies_a_sale_price_and_admits_the_gap()
    {
        var text = FleetAnalysis.ValueOf(With(99001, 12002), Catalogue()).Describe();

        Assert.Contains("at listed prices", text);
        Assert.Contains("no listed price", text);
        Assert.DoesNotContain("worth", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("value", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_empty_collection_says_so_rather_than_claiming_zero_RP()
    {
        Assert.Contains("No collection captured", FleetAnalysis.ValueOf(With(), Catalogue()).Describe());
    }

    [Fact]
    public void The_portfolio_counts_each_skin_once_across_accounts()
    {
        // Two accounts both owning Elementalist Lux is one distinct skin, not two — the number is
        // "how much of the game do I have access to", not a running total.
        var catalogue = Catalogue();
        var portfolio = FleetAnalysis.Summarise(
            [With(99001, 99002), With(99001)], catalogue, DateTimeOffset.UtcNow);

        Assert.Equal(2, portfolio.DistinctSkinsOwned);
        Assert.Equal(3, portfolio.TotalSkins);
        Assert.Equal(4600 + 3250, portfolio.TotalKnownRp);
    }
}

/// <summary>Spotting the accounts that quietly rot while you are not looking at them.</summary>
public sealed class DormancyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);

    private static AccountEntry Used(int daysAgo, bool ranked)
    {
        var account = new AccountEntry { Label = "a", LastUsedUtc = Now.AddDays(-daysAgo) };
        if (ranked) account.Identity.SoloRank = new RankInfo { Tier = "DIAMOND", Division = "III" };
        return account;
    }

    [Fact]
    public void A_ranked_account_untouched_for_months_is_flagged_as_decaying()
    {
        var dormancy = FleetAnalysis.DormancyOf(Used(100, ranked: true), Now);

        Assert.Equal(DormancyLevel.Stale, dormancy.Level);
        Assert.Contains("decayed", dormancy.Describe());
    }

    [Fact]
    public void An_unranked_account_is_only_idle_because_it_has_no_LP_to_lose()
    {
        var dormancy = FleetAnalysis.DormancyOf(Used(100, ranked: false), Now);

        Assert.Equal(DormancyLevel.Idle, dormancy.Level);
        Assert.DoesNotContain("decayed", dormancy.Describe());
    }

    [Theory]
    [InlineData(3, DormancyLevel.Active)]
    [InlineData(30, DormancyLevel.Idle)]
    [InlineData(200, DormancyLevel.Stale)]
    public void Thresholds_behave(int days, DormancyLevel expected)
        => Assert.Equal(expected, FleetAnalysis.DormancyOf(Used(days, ranked: true), Now).Level);

    [Fact]
    public void An_account_never_used_from_here_says_unknown_rather_than_guessing()
        => Assert.Equal(DormancyLevel.Unknown,
            FleetAnalysis.DormancyOf(new AccountEntry { Label = "fresh" }, Now).Level);
}

/// <summary>Knowing which sign-ins will still be instant before you need them to be.</summary>
public sealed class SessionStandingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);

    private static AccountEntry WithSession(TimeSpan age)
    {
        var account = new AccountEntry { Label = "a" };
        account.Session = new StoredSession
        {
            Yaml = new SecretText("psl:\n    authorization:\n        riot-client:\n            refresh_token: \"t\""),
            CapturedUtc = Now - age,
        };
        return account;
    }

    [Fact]
    public void A_fresh_session_is_usable()
        => Assert.Equal(SessionState.Usable,
            FleetAnalysis.SessionStandings([WithSession(TimeSpan.FromDays(1))], Now)[0].State);

    [Fact]
    public void One_about_to_lapse_is_called_out_before_it_does()
    {
        var standing = FleetAnalysis.SessionStandings(
            [WithSession(StoredSession.StaleAfter - TimeSpan.FromDays(1))], Now)[0];

        Assert.Equal(SessionState.ExpiringSoon, standing.State);
        Assert.Contains("expiring", standing.Describe());
    }

    [Fact]
    public void An_account_with_no_session_says_it_will_type()
        => Assert.Contains("type the password",
            FleetAnalysis.SessionStandings([new AccountEntry { Label = "a" }], Now)[0].Describe());

    [Fact]
    public void The_ones_needing_attention_sort_first()
    {
        var accounts = new[]
        {
            WithSession(TimeSpan.FromDays(1)),
            new AccountEntry { Label = "none" },
            WithSession(StoredSession.StaleAfter + TimeSpan.FromDays(1)),
        };

        var order = FleetAnalysis.SessionStandings(accounts, Now).Select(s => s.State).ToList();

        Assert.Equal(SessionState.Expired, order[0]);
        Assert.Equal(SessionState.Usable, order[^1]);
    }
}

/// <summary>
/// The second half of the duplicate problem. Champions carried Jade_* twins that inflated 173 into
/// 236; skins carry them too — 305 of one account's 1,582 "owned" skins are Jade, and 234 of those
/// have no name in the catalogue at all, so they would list as bare id numbers.
/// </summary>
public sealed class JadeSkinTests
{
    [Fact]
    public void A_jade_skin_id_is_recognised()
    {
        Assert.True(SkinCatalogue.IsJadeSkin(60001008));   // Jade Annie, skin 8
        Assert.True(SkinCatalogue.IsJadeSkin(60117014));
        Assert.False(SkinCatalogue.IsJadeSkin(99001));     // Elementalist Lux
        Assert.False(SkinCatalogue.IsJadeSkin(78002));
    }

    [Fact]
    public void Jade_skins_are_left_out_of_the_count_and_the_total()
    {
        var probe = new ClientStats
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            OwnedSkinIds = [99001, 78002, 60001008, 60117014],
            OwnedChampionIds = [99, 78],
            SkinsOwned = 2,
            ChampionsOwned = 2,
            CollectionIsComplete = true,
        };

        var account = new AccountEntry { Label = "main" };
        probe.ApplyTo(account);

        // The ids are all kept — nothing is thrown away — but only the real ones are counted.
        Assert.Equal(4, account.Identity.OwnedSkinIds.Length);
        Assert.Equal(2, account.Identity.ClientStats!.SkinsOwned);
    }

    [Fact]
    public void A_jade_duplicate_never_counts_toward_the_distinct_total()
    {
        var account = new AccountEntry { Label = "main" };
        account.Identity.OwnedSkinIds = [99001, 60001008];

        var catalogue = new SkinCatalogue(
            Path.Combine(Path.GetTempPath(), "lam-jade-" + Guid.NewGuid().ToString("N")));

        Assert.Equal(1, FleetAnalysis.Summarise([account], catalogue, DateTimeOffset.UtcNow).DistinctSkinsOwned);
    }
}
