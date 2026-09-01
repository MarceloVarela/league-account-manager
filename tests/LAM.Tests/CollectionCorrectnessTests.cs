using System.Text.Json;
using LAM.Core.Model;
using LAM.Core.Riot;
using Xunit;

namespace LAM.Tests;

/// <summary>
/// The join from an owned id back to a champion name.
///
/// This was broken for every skin an account owned, and the failure was invisible in the sense that
/// mattered: the list rendered, scrolled and searched — it just said "Unknown champion" 7,300 times.
/// </summary>
public sealed class ChampionResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lam-champ", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void A_skin_tagged_with_champion_zero_still_finds_its_champion()
    {
        // The original bug, exactly as the client presents it. Every catalog entry carries
        // `taggedChampionsIds: [0]`; the `??` fallback that derives the champion from the skin id
        // only fires on null, so a boxed zero sailed straight through and champion 0 — which exists
        // in no dictionary — became the answer for all 7,300 skins.
        using var document = JsonDocument.Parse("""
            [{"itemId":78002,"name":"Lollipoppy","taggedChampionsIds":[0],
              "tilePath":"/lol-game-data/assets/x.jpg","rarity":"EPIC"}]
            """);

        var skin = Assert.Single(SkinCatalogue.ParseSkins(document));

        Assert.Equal(78, skin.ChampionId);
    }

    [Fact]
    public void A_genuine_champion_tag_is_preferred_over_the_derived_one()
    {
        // The fallback must not shadow real data when the catalogue does tag an entry.
        using var document = JsonDocument.Parse(
            """[{"itemId":78002,"name":"Lollipoppy","taggedChampionsIds":[99]}]""");

        Assert.Equal(99, Assert.Single(SkinCatalogue.ParseSkins(document)).ChampionId);
    }

    [Fact]
    public void A_jade_skin_resolves_to_the_champion_it_duplicates()
    {
        using var document = JsonDocument.Parse(
            """[{"itemId":60001007,"name":"Jade Annie","taggedChampionsIds":[0]}]""");

        Assert.Equal(1, Assert.Single(SkinCatalogue.ParseSkins(document)).ChampionId);
    }

    [Fact]
    public void A_jade_variant_folds_onto_the_champion_it_duplicates()
    {
        // The game data ships a second champion set offset by 60,000 — 60001 "Jade_Annie" beside
        // 1 "Annie". Their skins derive to a champion id nothing can resolve.
        Assert.Equal(1, SkinCatalogue.NormaliseChampionId(60001));
        Assert.Equal(103, SkinCatalogue.NormaliseChampionId(60103));

        // A real champion id must pass through untouched.
        Assert.Equal(266, SkinCatalogue.NormaliseChampionId(266));
    }

    [Fact]
    public void The_champion_summary_placeholder_is_not_a_champion()
    {
        // champion-summary.json opens with an `id: -1, name: "None"` entry. Admitting it would put a
        // champion called "None" in the list and, worse, make -1 resolvable.
        using var document = JsonDocument.Parse(
            """[{"id":-1,"name":"None"},{"id":1,"name":"Annie"},{"id":266,"name":"Aatrox"}]""");

        var champions = SkinCatalogue.ParseChampions(document);

        Assert.Equal(2, champions.Count);
        Assert.DoesNotContain(champions, c => c.Id <= 0);
        Assert.Contains(champions, c => c is { Id: 1, Name: "Annie" });
    }

    [Fact]
    public void A_catalogue_from_an_older_schema_is_refused()
    {
        // Without this the fix above is invisible. The composition root calls TryLoad at startup,
        // which marks the catalogue loaded whenever the file exists, so RefreshAsync is never reached
        // and a cache written by a broken build would be believed for ever.
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "catalogue.json"),
            """
            {"SchemaVersion":1,"CapturedUtc":"2026-01-01T00:00:00+00:00",
             "Skins":[{"Id":78002,"Name":"Lollipoppy","ChampionId":0}],
             "Champions":[{"Id":78,"Name":"Poppy"}]}
            """);

        var catalogue = new SkinCatalogue(_root);

        Assert.False(catalogue.TryLoad());
        Assert.False(catalogue.IsLoaded);
    }

    [Fact]
    public void A_catalogue_at_the_current_schema_still_loads()
    {
        // Written against the constant rather than a literal: a bump is a routine part of fixing a
        // catalogue bug, and this test should keep proving that a current cache loads, not fail
        // every time one happens.
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "catalogue.json"),
            """
            {"SchemaVersion":__V__,"CapturedUtc":"2026-01-01T00:00:00+00:00",
             "Skins":[{"Id":78002,"Name":"Lollipoppy","ChampionId":78}],
             "Champions":[{"Id":78,"Name":"Poppy"}]}
            """.Replace("__V__", SkinCatalogue.CurrentSchemaVersion.ToString()));

        var catalogue = new SkinCatalogue(_root);

        Assert.True(catalogue.TryLoad());
        Assert.Equal("Poppy", catalogue.ChampionName(78));

        // And a Jade id resolves through the same map rather than falling to "Unknown champion".
        Assert.Equal("Poppy", catalogue.ChampionName(60078));
    }
}

/// <summary>
/// Guarding the collection against a half-loaded client.
///
/// An account owning every champion was recorded as owning twenty, and every subsequent sign-in
/// re-confirmed it, because a partial read was allowed to overwrite a good one.
/// </summary>
public sealed class CollectionCaptureTests
{
    private static ClientStats Complete(int[] champions, int[] skins) => new()
    {
        CapturedUtc = DateTimeOffset.UtcNow,
        ChampionsOwned = champions.Length,
        SkinsOwned = skins.Length,
        OwnedChampionIds = champions,
        OwnedSkinIds = skins,
        CollectionIsComplete = true,
    };

    [Fact]
    public void An_incomplete_capture_does_not_overwrite_a_good_collection()
    {
        var account = new AccountEntry { Label = "main" };
        Complete([1, 2, 3, 4], [78002, 103001]).ApplyTo(account);

        // A later sign-in that caught the client mid-load: the free rotation only.
        var partial = new ClientStats
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            ChampionsOwned = 1,
            SkinsOwned = 0,
            OwnedChampionIds = [1],
            OwnedSkinIds = [],
            CollectionIsComplete = false,
        };

        partial.ApplyTo(account);

        Assert.Equal([1, 2, 3, 4], account.Identity.OwnedChampionIds);
        Assert.Equal([78002, 103001], account.Identity.OwnedSkinIds);

        // And the displayed counts must not regress either, or the window would contradict the list.
        Assert.Equal(4, account.Identity.ClientStats?.ChampionsOwned);
        Assert.Equal(2, account.Identity.ClientStats?.SkinsOwned);
    }

    [Fact]
    public void A_complete_capture_replaces_the_collection()
    {
        var account = new AccountEntry { Label = "main" };
        Complete([1, 2], [78002]).ApplyTo(account);
        Complete([1, 2, 3], [78002, 103001, 266000]).ApplyTo(account);

        Assert.Equal([1, 2, 3], account.Identity.OwnedChampionIds);
        Assert.Equal(3, account.Identity.ClientStats?.ChampionsOwned);
    }

    [Fact]
    public void An_incomplete_capture_still_records_fresh_rank_and_wallet()
    {
        // The collection is the only untrustworthy part of an early read; throwing the rest away
        // would mean a sign-in that reported nothing at all.
        var account = new AccountEntry { Label = "main" };
        Complete([1, 2], [78002]).ApplyTo(account);

        new ClientStats
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            RiotPoints = 2160,
            Solo = new RankInfo { Tier = "DIAMOND", Division = "III", LeaguePoints = 27 },
            CollectionIsComplete = false,
        }.ApplyTo(account);

        Assert.Equal(2160, account.Identity.ClientStats?.RiotPoints);
        Assert.Equal("DIAMOND", account.Identity.SoloRank?.Tier);
        Assert.Equal([1, 2], account.Identity.OwnedChampionIds);
    }
}
