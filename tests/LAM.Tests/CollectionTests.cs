using System.Text.Json;
using LAM.Core.Model;
using LAM.Core.Riot;
using LAM.Core.Vault;
using Xunit;

namespace LAM.Tests;

/// <summary>
/// Storing a collection of fifteen hundred skins per account, without bloating the vault.
/// </summary>
public sealed class CollectionStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lam-collection", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void Owned_ids_survive_a_vault_round_trip()
    {
        var repository = new VaultRepository(new VaultPaths(_root));

        using (var password = SecretBuffer.FromString("pw"))
        using (var vault = repository.Create(password, bindToMachine: false, KdfParameters.TestOnlyFast))
        {
            var account = new AccountEntry { Label = "main" };
            account.Identity.OwnedSkinIds = [78002, 103001, 266000];
            account.Identity.OwnedChampionIds = [1, 2, 3];

            vault.Document.Accounts.Add(account);
            vault.Save();
        }

        using var reopen = SecretBuffer.FromString("pw");
        using var reopened = repository.Unlock(reopen);

        var stored = Assert.Single(reopened.Document.Accounts);
        Assert.Equal([78002, 103001, 266000], stored.Identity.OwnedSkinIds);
        Assert.Equal([1, 2, 3], stored.Identity.OwnedChampionIds);
    }

    [Fact]
    public void A_full_collection_costs_kilobytes_not_megabytes()
    {
        // The reason ids are stored instead of names. A realistic collection is ~1,600 skins; with
        // names, rarity and art paths inlined per account that would be megabytes duplicated across
        // every account, all of it identical.
        var account = new AccountEntry { Label = "big collection" };
        account.Identity.OwnedSkinIds = Enumerable.Range(0, 1600).Select(i => 1000 + i).ToArray();
        account.Identity.OwnedChampionIds = Enumerable.Range(1, 170).ToArray();

        var json = JsonSerializer.SerializeToUtf8Bytes(account, VaultJson.Storage);

        Assert.True(json.Length < 40_000,
            "A full collection should cost tens of kilobytes, not megabytes; it was " + json.Length + " bytes.");
    }
}

/// <summary>The shared catalogue that turns ids back into names and art.</summary>
public sealed class SkinCatalogueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lam-catalogue", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void An_unknown_id_still_produces_a_row()
    {
        // A skin released after the cached catalogue was captured must not vanish from a collection.
        // A row that admits it does not know the name is far less confusing than a silent gap.
        var catalogue = new SkinCatalogue(_root);

        var resolved = catalogue.Resolve([78002]);

        var row = Assert.Single(resolved);
        Assert.Equal(78002, row.Id);
        Assert.Contains("78002", row.Name);
    }

    [Fact]
    public void Resolve_keeps_the_order_it_was_given()
    {
        var catalogue = new SkinCatalogue(_root);

        var resolved = catalogue.Resolve([300, 100, 200]);

        Assert.Equal([300, 100, 200], resolved.Select(r => r.Id));
    }

    [Fact]
    public void An_absent_cache_loads_as_absent_rather_than_throwing()
    {
        var catalogue = new SkinCatalogue(Path.Combine(_root, "nothing-here"));

        Assert.False(catalogue.TryLoad());
        Assert.False(catalogue.IsLoaded);
        Assert.Equal("Unknown champion", catalogue.ChampionName(266));
    }

    [Fact]
    public void A_base_skin_is_distinguishable_from_a_bought_one()
    {
        Assert.True(new CatalogueSkin(266000, "Aatrox", 266, null, null, null).IsBaseSkin);
        Assert.False(new CatalogueSkin(266001, "Justicar Aatrox", 266, null, null, null).IsBaseSkin);
    }
}

/// <summary>Mapping client asset paths onto the public mirror.</summary>
public sealed class SkinArtUrlTests
{
    [Fact]
    public void The_asset_prefix_is_stripped_and_the_path_lowercased()
    {
        // Community Dragon serves the same tree without the prefix, and 404s on the original casing —
        // which is the whole reason this is a function rather than string concatenation at the call site.
        var url = SkinArtCache.ToCommunityDragonUrl(
            "/lol-game-data/assets/ASSETS/Characters/Poppy/Skins/Skin02/Images/poppy_splash_tile_2.jpg");

        Assert.Equal(
            "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default"
            + "/assets/characters/poppy/skins/skin02/images/poppy_splash_tile_2.jpg",
            url);
    }

    [Fact]
    public void A_path_without_the_prefix_is_still_handled()
    {
        var url = SkinArtCache.ToCommunityDragonUrl("assets/characters/ashe/ashe.jpg");

        Assert.Contains("/assets/characters/ashe/ashe.jpg", url);
        Assert.DoesNotContain("//assets", url);
    }
}

/// <summary>
/// Chunking a collection into rows, which is what allows the skins grid to virtualize.
///
/// WPF's wrapping panel does not virtualize, so fifteen hundred tiles have to be grouped into rows
/// for the virtualizing list to cope. Dropping the remainder would silently lose skins off the end of
/// a collection, which is exactly the kind of bug nobody notices.
/// </summary>
public sealed class ChunkingTests
{
    private static IReadOnlyList<int> Items(int count) => Enumerable.Range(1, count).ToList();

    [Fact]
    public void A_count_that_does_not_divide_evenly_keeps_the_remainder()
    {
        var rows = Chunking.IntoRows(Items(7), perRow: 5);

        Assert.Equal(2, rows.Count);
        Assert.Equal(5, rows[0].Count);
        Assert.Equal(2, rows[1].Count);
    }

    [Fact]
    public void An_exact_multiple_produces_full_rows_and_no_empty_one()
    {
        var rows = Chunking.IntoRows(Items(10), perRow: 5);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(5, row.Count));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 2)]
    public void Edge_counts_behave(int items, int expectedRows)
        => Assert.Equal(expectedRows, Chunking.IntoRows(Items(items), perRow: 5).Count);

    [Fact]
    public void A_nonsensical_row_width_does_not_divide_by_zero()
        => Assert.Equal(3, Chunking.IntoRows(Items(3), perRow: 0).Count);

    [Fact]
    public void Every_item_appears_exactly_once_and_in_order()
    {
        var items = Items(23);
        var flattened = Chunking.IntoRows(items, perRow: 5).SelectMany(row => row).ToList();

        Assert.Equal(items, flattened);
    }
}

/// <summary>Whether an account can be signed into, and what the button should say when it cannot.</summary>
public sealed class SignInAvailabilityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void An_account_with_no_password_and_no_session_cannot_sign_in()
    {
        // A disabled button that explains itself beats a click that fails afterwards.
        var account = new AccountEntry { Label = "bare" };

        Assert.False(account.CanSignIn(Now));
        Assert.Contains("No password saved", account.SignInExplanation(Now));
    }

    [Fact]
    public void A_stored_password_is_enough()
    {
        var account = new AccountEntry { Label = "typed", Password = "pw" };

        Assert.True(account.CanSignIn(Now));
        Assert.Contains("Types the saved password", account.SignInExplanation(Now));
    }

    [Fact]
    public void A_usable_session_is_enough_even_without_a_password()
    {
        var account = new AccountEntry
        {
            Label = "instant",
            Session = new StoredSession
            {
                Yaml = new SecretText("""
                    psl:
                        authorization:
                            riot-client:
                                refresh_token: "t"
                    """),
                CapturedUtc = Now,
            },
        };

        Assert.True(account.CanSignIn(Now));
        Assert.Contains("no typing", account.SignInExplanation(Now));
    }

    [Fact]
    public void An_expired_session_falls_back_to_the_password_wording()
    {
        var account = new AccountEntry
        {
            Label = "stale",
            Password = "pw",
            Session = new StoredSession
            {
                Yaml = new SecretText("""
                    psl:
                        authorization:
                            riot-client:
                                refresh_token: "t"
                    """),
                CapturedUtc = Now - StoredSession.StaleAfter - TimeSpan.FromDays(1),
            },
        };

        Assert.True(account.CanSignIn(Now));
        Assert.Contains("Types the saved password", account.SignInExplanation(Now));
    }
}
