using System.Text.Json;
using LAM.Core.Model;
using LAM.Core.Riot;
using Xunit;

namespace LAM.Tests;

/// <summary>
/// Reading ownership out of the client, and the flag that looks like it means the opposite of what
/// it does.
/// </summary>
public sealed class ChampionOwnershipTests
{
    /// <summary>
    /// Shaped like the real response: everything owned, and some of it flagged for this week's free
    /// rotation despite having been bought years ago.
    /// </summary>
    private const string OwnedChampionsMinimal = """
        [
          {"id":1,  "name":"Annie",  "freeToPlay":false,
           "ownership":{"owned":true,"rental":{"rented":false,"purchaseDate":1370310999000}}},
          {"id":14, "name":"Sion",   "freeToPlay":true,
           "ownership":{"owned":true,"rental":{"rented":false,"purchaseDate":1411923717000}}},
          {"id":23, "name":"Tryndamere","freeToPlay":true,
           "ownership":{"owned":true,"rental":{"rented":false,"purchaseDate":1376051146000}}},
          {"id":60001,"name":"Annie","freeToPlay":false,
           "ownership":{"owned":true,"rental":{"rented":false,"purchaseDate":1370310999000}}}
        ]
        """;

    [Fact]
    public void A_free_rotation_champion_that_is_owned_is_kept()
    {
        // Deliberately locked in. `freeToPlay` on this endpoint marks the *current rotation*, not a
        // temporary grant — Sion here was bought in 2014. Filtering on it, by analogy with the skin
        // inventory's `f2p` flag where filtering IS correct, silently deletes twenty owned champions.
        using var document = JsonDocument.Parse(OwnedChampionsMinimal);

        var ids = ClientStatsProbe.ReadChampionIds(document);

        Assert.NotNull(ids);
        Assert.Contains(14, ids!);
        Assert.Contains(23, ids!);
        Assert.Equal(4, ids!.Length);
    }

    [Fact]
    public void A_temporary_skin_grant_is_excluded()
    {
        // The opposite case, and the reason the asymmetry above is easy to mistake for a bug: on the
        // skin inventory `f2p` really does mean "not yours".
        using var document = JsonDocument.Parse("""
            [
              {"itemId":78002,"owned":true,"f2p":false,"rental":false},
              {"itemId":103001,"owned":true,"f2p":true, "rental":false},
              {"itemId":266001,"owned":true,"f2p":false,"rental":true},
              {"itemId":432001,"owned":false,"f2p":false,"rental":false}
            ]
            """);

        var ids = ClientStatsProbe.ReadOwnedSkinIds(document);

        Assert.Equal([78002], ids!);
    }
}

/// <summary>The recovery dossier read from the live client.</summary>
public sealed class UserInfoTests
{
    /// <summary>The real response shape: a JSON *string* under "userInfo", not a JWT.</summary>
    private const string UserInfo = """
        {"userInfo":"{\"country\":\"bra\",\"sub\":\"11111111-2222-3333-4444-555555555555\",
         \"username\":\"legacylogin\",\"preferred_username\":\"Testplayer\",
         \"original_platform_id\":\"BR1\",\"original_account_id\":1234567,
         \"pvpnet_account_id\":987654321,\"age_category\":\"ADULT\",\"email_set\":true,
         \"account_verified\":true,\"phone_number_verified\":true,\"country_at\":1611017707000,
         \"acct\":{\"game_name\":\"Testplayer\",\"tag_line\":\"00000\",\"state\":\"ENABLED\",
                   \"created_at\":1351544771000},
         \"pw\":{\"cng_at\":1567751618000,\"must_reset\":false,\"reset\":false},
         \"lol_account\":{\"summoner_name\":\"Testplayer\",\"summoner_level\":625},
         \"lol\":{\"uid\":987654321,\"cpid\":\"BR1\"}}"}
        """;

    private static ClientAccountFacts Parse()
    {
        using var document = JsonDocument.Parse(UserInfo.Replace("\r", "").Replace("\n", ""));
        var facts = ClientAccountProbe.ParseUserInfo(document.RootElement);

        Assert.NotNull(facts);
        return facts!;
    }

    [Fact]
    public void The_real_creation_date_is_read()
    {
        // 1351544771000 == 29 Oct 2012. The app previously showed `country_at` here, which for this
        // account is Jan 2021 — nine years out, on the one field a recovery ticket turns on.
        var created = Parse().CreatedUtc;

        Assert.NotNull(created);
        Assert.Equal(2012, created!.Value.UtcDateTime.Year);
        Assert.Equal(10, created.Value.UtcDateTime.Month);
    }

    [Fact]
    public void The_creation_date_beats_the_country_date_on_the_recovery_sheet()
    {
        var account = new AccountEntry { Label = "main" };

        Parse().ApplyTo(account, DateTimeOffset.UtcNow);

        Assert.Equal(2012, account.Recovery.ApproximateCreated?.Year);
    }

    [Fact]
    public void A_typed_creation_date_is_never_overwritten()
    {
        var account = new AccountEntry { Label = "main" };
        account.Recovery.ApproximateCreated = new DateOnly(2011, 5, 1);

        Parse().ApplyTo(account, DateTimeOffset.UtcNow);

        Assert.Equal(new DateOnly(2011, 5, 1), account.Recovery.ApproximateCreated);
    }

    [Fact]
    public void The_fields_a_recovery_form_asks_for_are_all_present()
    {
        var facts = Parse();

        Assert.Equal("legacylogin", facts.LegacyUsername);
        Assert.Equal("BR1", facts.OriginalPlatform);
        Assert.Equal("1234567", facts.LegacyAccountId);
        Assert.Equal("987654321", facts.PvpnetAccountId);
        Assert.Equal("ADULT", facts.AgeCategory);
        Assert.Equal("ENABLED", facts.AccountState);
        Assert.Equal("Testplayer", facts.SummonerName);
        Assert.True(facts.EmailOnFile);
        Assert.False(facts.MustResetPassword);
        Assert.Equal(2019, facts.PasswordChangedUtc?.UtcDateTime.Year);
    }

    [Fact]
    public void Merging_keeps_what_each_source_alone_would_lose()
    {
        // userinfo has the creation date but no region history; the id_token has the history but not
        // the date. Taking either one on its own drops half the dossier.
        var live = Parse();
        var stored = new ClientAccountFacts
        {
            Regions = [new AccountRegion("BR1", true), new AccountRegion("NA1", false)],
            MfaEnabled = true,
        };

        var merged = live.MergedWith(stored);

        Assert.Equal(2, merged.Regions.Count);
        Assert.True(merged.MfaEnabled);
        Assert.Equal(2012, merged.CreatedUtc?.UtcDateTime.Year);
        Assert.Equal("BR1", merged.OriginalPlatform);
    }

    [Fact]
    public void A_response_that_is_not_the_expected_shape_is_declined_rather_than_thrown()
    {
        using var document = JsonDocument.Parse("""{"userInfo":"not json at all"}""");

        Assert.Null(ClientAccountProbe.ParseUserInfo(document.RootElement));
    }
}

/// <summary>Reading the loot inventory.</summary>
public sealed class LootTests
{
    /// <summary>
    /// Shaped like the real response, including its awkward parts: currencies arrive with no name
    /// and no category at all, and Mythic Essence is filed under CHEST.
    /// </summary>
    private const string PlayerLoot = """
        [
          {"lootId":"","count":0,"type":"","displayCategories":""},
          {"lootId":"CURRENCY_champion","count":272382,"type":"CURRENCY","displayCategories":"",
           "itemDesc":"","localizedName":""},
          {"lootId":"CURRENCY_cosmetic","count":6367,"type":"CURRENCY","displayCategories":"",
           "itemDesc":"","localizedName":""},
          {"lootId":"CURRENCY_mythic","count":20,"type":"CURRENCY","displayCategories":"CHEST",
           "localizedName":"Mythic Essence"},
          {"lootId":"MATERIAL_key","count":4,"type":"MATERIAL","displayCategories":""},
          {"lootId":"CHAMPION_SKIN_RENTAL_143046","count":1,"type":"SKIN_RENTAL",
           "displayCategories":"SKIN","rarity":"EPIC","itemDesc":"Street Demons Zyra",
           "disenchantValue":242,"expiryTime":1790000000000,
           "tilePath":"/lol-game-data/assets/ASSETS/Characters/Zyra/x.jpg"},
          {"lootId":"STATSTONE_66600384","count":1,"type":"STATSTONE","displayCategories":"ETERNALS",
           "localizedName":"Braum - Series 2","rarity":"DEFAULT"}
        ]
        """;

    private static LootSnapshot Parse()
    {
        using var document = JsonDocument.Parse(PlayerLoot);
        var loot = ClientLootProbe.Parse(document, DateTimeOffset.UnixEpoch);

        Assert.NotNull(loot);
        return loot!;
    }

    [Fact]
    public void An_empty_placeholder_entry_is_dropped()
    {
        // The client includes one zero-count entry with no id. Showing it would put a nameless blank
        // card at the top of the view.
        Assert.DoesNotContain(Parse().Items, item => item.Count == 0);
        Assert.Equal(6, Parse().Items.Count);
    }

    [Fact]
    public void Currencies_are_named_even_though_the_client_does_not_name_them()
    {
        var items = Parse().Items;

        Assert.Contains(items, i => i.Name == "Blue Essence" && i.Count == 272382);
        Assert.Contains(items, i => i.Name == "Orange Essence" && i.Count == 6367);
        Assert.Contains(items, i => i.Name == "Hextech Key" && i.Count == 4);
    }

    [Fact]
    public void Every_currency_lands_in_one_section()
    {
        // Mythic Essence arrives categorised as CHEST. Left alone it would sit under "Chests and
        // keys" while the other essences sat elsewhere.
        var essence = Parse().Grouped().Single(g => g.Category == "CURRENCY");

        Assert.Equal(3, essence.Items.Count);
        Assert.Contains(essence.Items, i => i.Name == "Mythic Essence");
        Assert.DoesNotContain(Parse().Grouped(), g => g.Category == "CHEST");
    }

    [Fact]
    public void A_category_the_client_leaves_blank_falls_back_to_the_type()
    {
        var groups = Parse().Grouped();

        Assert.Contains(groups, g => g.Category == "MATERIAL");
        Assert.DoesNotContain(groups, g => string.IsNullOrEmpty(g.Category));
    }

    [Fact]
    public void Shards_keep_their_name_rarity_disenchant_value_and_expiry()
    {
        var shard = Parse().Items.Single(i => i.LootId == "CHAMPION_SKIN_RENTAL_143046");

        Assert.Equal("Street Demons Zyra", shard.Name);
        Assert.Equal("EPIC", shard.Rarity);
        Assert.Equal(242, shard.DisenchantValue);
        Assert.True(shard.IsExpiring);
    }

    [Fact]
    public void Loot_that_does_not_expire_is_not_flagged_as_expiring()
    {
        // expiryTime is 0 for permanent loot, and 0 as an epoch is 1970 — which would render as
        // "expired long ago" on everything you own.
        Assert.False(Parse().Items.Single(i => i.LootId == "MATERIAL_key").IsExpiring);
    }

    [Fact]
    public void Groups_come_out_in_the_clients_own_order()
    {
        var titles = Parse().Grouped().Select(g => g.Title).ToList();

        Assert.Equal("Materials", titles[0]);
        Assert.True(titles.IndexOf("Skin shards") < titles.IndexOf("Essence"));
    }

    [Fact]
    public void An_id_keyed_object_response_parses_as_well_as_an_array()
    {
        using var document = JsonDocument.Parse(
            """{"MATERIAL_key":{"lootId":"MATERIAL_key","count":4,"type":"MATERIAL"}}""");

        var loot = ClientLootProbe.Parse(document, DateTimeOffset.UnixEpoch);

        Assert.Equal("Hextech Key", Assert.Single(loot!.Items).Name);
    }
}

/// <summary>
/// The first champion ever bought — recovery evidence derived from purchase dates rather than
/// remembered.
/// </summary>
public sealed class FirstPurchaseTests
{
    private const string Champions = """
        [
          {"id":22,"name":"Ashe",
           "ownership":{"owned":true,"rental":{"rented":false,"purchaseDate":1352894280000}}},
          {"id":86,"name":"Garen",
           "ownership":{"owned":true,"rental":{"rented":false,"purchaseDate":1352462700000}}},
          {"id":60086,"name":"Garen",
           "ownership":{"owned":true,"rental":{"rented":false,"purchaseDate":1352462700000}}},
          {"id":31,"name":"Cho'Gath",
           "ownership":{"owned":true,"rental":{"rented":false,"purchaseDate":1354000440000}}}
        ]
        """;

    [Fact]
    public void The_oldest_purchase_wins()
    {
        using var document = JsonDocument.Parse(Champions);

        var first = ClientStatsProbe.ReadFirstPurchase(document);

        Assert.NotNull(first);
        Assert.Equal(86, first!.Value.ChampionId);
        Assert.Equal(2012, first.Value.PurchasedUtc.UtcDateTime.Year);
    }

    [Fact]
    public void A_jade_twin_never_wins_the_tie()
    {
        // 86 and 60086 are the same champion with an identical timestamp. Reporting 60086 would name
        // a champion id that resolves to a duplicate entry rather than the real one.
        using var document = JsonDocument.Parse(Champions);

        Assert.Equal(86, ClientStatsProbe.ReadFirstPurchase(document)!.Value.ChampionId);
    }

    [Fact]
    public void A_missing_or_zero_purchase_date_is_ignored_rather_than_treated_as_1970()
    {
        using var document = JsonDocument.Parse("""
            [
              {"id":1,"name":"Annie","ownership":{"rental":{"purchaseDate":0}}},
              {"id":2,"name":"Olaf","ownership":{"rental":{}}},
              {"id":3,"name":"Galio","ownership":{"rental":{"purchaseDate":1352462700000}}}
            ]
            """);

        var first = ClientStatsProbe.ReadFirstPurchase(document);

        Assert.Equal(3, first!.Value.ChampionId);
    }

    [Fact]
    public void No_purchase_data_at_all_is_null_rather_than_a_guess()
    {
        using var document = JsonDocument.Parse("""[{"id":1,"name":"Annie"}]""");

        Assert.Null(ClientStatsProbe.ReadFirstPurchase(document));
    }
}

/// <summary>
/// Correcting a value an earlier build wrote into a field meant for the user's own answers.
/// </summary>
public sealed class CreationDateRepairTests
{
    private static ClientAccountFacts Facts() => new()
    {
        RiotAccountId = "11111111-2222-3333-4444-555555555555",
        CreatedUtc = new DateTimeOffset(2012, 10, 29, 0, 0, 0, TimeSpan.Zero),
        CountrySetUtc = new DateTimeOffset(2021, 1, 18, 0, 0, 0, TimeSpan.Zero),
    };

    /// <summary>
    /// Dates are stored as UTC now. Rendering through the machine's local zone was itself one of the
    /// bugs — it shifted the recorded day either side of midnight.
    /// </summary>
    private static DateOnly Utc(DateTimeOffset value) => DateOnly.FromDateTime(value.UtcDateTime);

    [Fact]
    public void A_date_a_previous_build_auto_filled_from_the_wrong_claim_is_corrected()
    {
        // "Never overwrite what you typed" could not tell a typed value from one this app wrote, so
        // the wrong date became permanent — the correct one was blocked by the very rule meant to
        // protect the user's input.
        var account = new AccountEntry { Label = "main" };
        account.Recovery.ApproximateCreated = Utc(Facts().CountrySetUtc!.Value);

        Facts().ApplyTo(account, DateTimeOffset.UtcNow);

        Assert.Equal(Utc(Facts().CreatedUtc!.Value), account.Recovery.ApproximateCreated);
    }

    [Fact]
    public void A_genuinely_typed_date_is_still_never_touched()
    {
        var account = new AccountEntry { Label = "main" };
        account.Recovery.ApproximateCreated = new DateOnly(2013, 4, 2);

        Facts().ApplyTo(account, DateTimeOffset.UtcNow);

        Assert.Equal(new DateOnly(2013, 4, 2), account.Recovery.ApproximateCreated);
    }

    [Fact]
    public void A_typed_date_that_happens_to_be_right_is_left_alone()
    {
        // The repair must be a no-op when the stored value already agrees with the client, or it
        // would rewrite a correct answer for no reason.
        var account = new AccountEntry { Label = "main" };
        account.Recovery.ApproximateCreated = Utc(Facts().CreatedUtc!.Value);

        Facts().ApplyTo(account, DateTimeOffset.UtcNow);

        Assert.Equal(Utc(Facts().CreatedUtc!.Value), account.Recovery.ApproximateCreated);
    }

    [Fact]
    public void With_no_real_creation_date_the_stored_value_is_not_disturbed()
    {
        // An older client that cannot report created_at must not cause the stored date to be
        // second-guessed.
        var account = new AccountEntry { Label = "main" };
        account.Recovery.ApproximateCreated = new DateOnly(2021, 1, 18);

        (Facts() with { CreatedUtc = null }).ApplyTo(account, DateTimeOffset.UtcNow);

        Assert.Equal(new DateOnly(2021, 1, 18), account.Recovery.ApproximateCreated);
    }
}

/// <summary>
/// The guard on refreshing an account from the running client.
///
/// The client answers only for whoever is signed in, so this check is the entire safety of the
/// feature: get it wrong and one account's rank, collection and recovery details land on another's
/// card, permanently and plausibly.
/// </summary>
public sealed class RefreshGuardTests
{
    private const string Mine = "11111111-2222-3333-4444-555555555555";
    private const string Theirs = "99999999-0000-0000-0000-000000000000";

    private static AccountEntry Known()
    {
        var account = new AccountEntry { Label = "main" };
        account.Identity.RiotAccountId = Mine;
        return account;
    }

    [Fact]
    public void The_signed_in_account_may_refresh_itself()
        => Assert.Null(Known().RefreshRefusalReason(Mine, "Testplayer"));

    [Fact]
    public void A_different_signed_in_account_is_refused_and_named()
    {
        var reason = Known().RefreshRefusalReason(Theirs, "SomeoneElse");

        Assert.NotNull(reason);
        Assert.Contains("SomeoneElse", reason!);
        Assert.Contains("nothing was changed", reason!);
    }

    [Fact]
    public void An_account_never_signed_into_is_refused_rather_than_trusted()
    {
        // Nothing to compare against, so accepting would mean copying whichever account happens to
        // be signed in onto this card.
        var reason = new AccountEntry { Label = "fresh" }.RefreshRefusalReason(Mine, "Testplayer");

        Assert.NotNull(reason);
        Assert.Contains("never been signed into", reason!);
    }

    [Fact]
    public void A_client_that_answers_nothing_is_refused()
        => Assert.Contains("not running", Known().RefreshRefusalReason(null, null)!);

    [Fact]
    public void The_match_is_case_insensitive()
        => Assert.Null(Known().RefreshRefusalReason(Mine.ToUpperInvariant(), "Testplayer"));
}

/// <summary>
/// Keeping the dossier when a capture can only see half of it.
///
/// The userinfo endpoint lives on the Riot Client and the id_token lives in a file, so a refresh
/// taken while the Riot Client is closed sees a far thinner set of claims. That must not be allowed
/// to erase what a richer capture already established.
/// </summary>
public sealed class DossierRetentionTests
{
    private static ClientAccountFacts Rich() => new()
    {
        RiotAccountId = "11111111-2222-3333-4444-555555555555",
        GameName = "Testplayer",
        TagLine = "00000",
        CreatedUtc = new DateTimeOffset(2012, 10, 29, 0, 0, 0, TimeSpan.Zero),
        PasswordChangedUtc = new DateTimeOffset(2019, 9, 6, 0, 0, 0, TimeSpan.Zero),
        LegacyUsername = "legacylogin",
        OriginalPlatform = "BR1",
        LegacyAccountId = "1234567",
        MaskedEmail = "ma***@*****.com",
        Country = "bra",
    };

    /// <summary>What the id_token alone yields: no creation date, no username, no original region.</summary>
    private static ClientAccountFacts Thin() => new()
    {
        RiotAccountId = "11111111-2222-3333-4444-555555555555",
        GameName = "Testplayer",
        TagLine = "00000",
        Country = "bra",
        Regions = [new AccountRegion("BR1", true)],
    };

    [Fact]
    public void A_thin_capture_does_not_erase_a_rich_one()
    {
        var account = new AccountEntry { Label = "main" };
        Rich().ApplyTo(account, DateTimeOffset.UtcNow);

        Thin().ApplyTo(account, DateTimeOffset.UtcNow);

        var observed = account.Identity.Observed!;
        Assert.Equal(2012, observed.CreatedUtc?.UtcDateTime.Year);
        Assert.Equal("legacylogin", observed.LegacyUsername);
        Assert.Equal("BR1", observed.OriginalPlatform);
        Assert.Equal("1234567", observed.LegacyAccountId);
        Assert.Equal(2019, observed.PasswordChangedUtc?.UtcDateTime.Year);
    }

    [Fact]
    public void The_thin_capture_still_contributes_what_it_does_know()
    {
        // Merging forward must not mean ignoring the new reading — the region history only the
        // id_token carries has to survive too.
        var account = new AccountEntry { Label = "main" };
        Rich().ApplyTo(account, DateTimeOffset.UtcNow);
        Thin().ApplyTo(account, DateTimeOffset.UtcNow);

        Assert.Single(account.Identity.Observed!.Regions);
        Assert.Equal("BR1", account.Identity.Observed!.Regions[0].Platform);
    }

    [Fact]
    public void A_fresher_value_still_wins_over_the_stored_one()
    {
        // Merging backfills; it must never make the dossier stale. A changed email has to come through.
        var account = new AccountEntry { Label = "main" };
        Rich().ApplyTo(account, DateTimeOffset.UtcNow);

        (Rich() with { MaskedEmail = "newad*****@*****.com" }).ApplyTo(account, DateTimeOffset.UtcNow);

        Assert.Equal("newad*****@*****.com", account.Identity.Observed!.MaskedEmail);
    }

    [Fact]
    public void The_creation_date_survives_a_thin_capture_on_the_recovery_sheet_too()
    {
        // The stored dossier and the recovery field are filled from the same merged view, so a thin
        // capture must not be able to leave one of them behind.
        var account = new AccountEntry { Label = "main" };
        Rich().ApplyTo(account, DateTimeOffset.UtcNow);
        Thin().ApplyTo(account, DateTimeOffset.UtcNow);

        Assert.Equal(2012, account.Recovery.ApproximateCreated?.Year);
    }
}

/// <summary>Reading claims where "present but empty" and "absent" must not be confused.</summary>
public sealed class ClaimReadingTests
{
    [Fact]
    public void An_empty_string_claim_does_not_beat_a_real_value_from_the_other_source()
    {
        // Riot really does return these: lol_account.summoner_name has been "" ever since Riot IDs
        // replaced summoner names. Treating "" as a value would blank a good field on merge.
        using var document = JsonDocument.Parse(
            """{"userInfo":"{\"sub\":\"abc\",\"username\":\"\",\"original_platform_id\":\"\"}"}""");

        var live = ClientAccountProbe.ParseUserInfo(document.RootElement)!;

        Assert.Null(live.LegacyUsername);
        Assert.Null(live.OriginalPlatform);

        var merged = live.MergedWith(new ClientAccountFacts
        {
            LegacyUsername = "legacylogin",
            OriginalPlatform = "BR1",
        });

        Assert.Equal("legacylogin", merged.LegacyUsername);
        Assert.Equal("BR1", merged.OriginalPlatform);
    }
}

/// <summary>
/// Riot's own record of every Riot ID the account has used, with dates — strictly better than the
/// names this app happened to observe, which only begin when it was first used here.
/// </summary>
public sealed class AliasHistoryTests
{
    private const string Aliases = """
        [{"game_name":"Testplayer","tag_line":"GOD","active":false,"created_datetime":1613000000000},
         {"game_name":"Testplayer","tag_line":"00000","active":true,"created_datetime":1698000000000},
         {"game_name":"Testplayer","tag_line":"PIS","active":false,"created_datetime":1662000000000}]
        """;

    [Fact]
    public void Aliases_come_back_oldest_first_with_the_current_one_marked()
    {
        using var document = JsonDocument.Parse(Aliases);

        var parsed = ClientAccountProbe.ParseAliases(document.RootElement);

        Assert.Equal(3, parsed.Count);
        Assert.Equal("GOD", parsed[0].TagLine);
        Assert.Equal("00000", parsed[^1].TagLine);
        Assert.True(parsed[^1].Active);
    }

    [Fact]
    public void The_created_date_is_milliseconds_here_even_though_the_catalogue_uses_seconds()
    {
        // Two endpoints, two units. 1613000000000 is Feb 2021 as milliseconds and the year 53109 as
        // seconds, so getting this backwards is loud in one direction and silent in the other.
        using var document = JsonDocument.Parse(Aliases);

        Assert.Equal(2021, ClientAccountProbe.ParseAliases(document.RootElement)[0].CreatedUtc!.Value.Year);
    }

    [Fact]
    public void An_object_wrapper_parses_as_well_as_a_bare_array()
    {
        using var document = JsonDocument.Parse(
            """{"aliases":[{"game_name":"A","tag_line":"1","active":true,"created_datetime":1613000000000}]}""");

        Assert.Single(ClientAccountProbe.ParseAliases(document.RootElement));
    }

    [Fact]
    public void An_entry_with_no_name_is_skipped_rather_than_listed_blank()
    {
        using var document = JsonDocument.Parse("""[{"game_name":"","tag_line":"X","active":false}]""");

        Assert.Empty(ClientAccountProbe.ParseAliases(document.RootElement));
    }
}

/// <summary>The phone number, as much of it as Riot will show.</summary>
public sealed class MaskedPhoneTests
{
    [Fact]
    public void The_country_code_and_last_digits_are_read()
    {
        using var document = JsonDocument.Parse("""
            {"data":{"phoneNumberObfuscated":{"countryCode":"12","endsWith":"4321","length":11}}}
            """);

        var phone = ClientAccountProbe.ParsePhone(document.RootElement);

        Assert.Equal("12", phone!.Value.CountryCode);
        Assert.Equal("4321", phone.Value.EndsWith);
    }

    [Fact]
    public void The_display_form_shows_enough_to_recognise_and_no_more()
    {
        var facts = new ClientAccountFacts { PhoneCountryCode = "12", PhoneEndsWith = "4321" };

        Assert.Equal("+12 *** 4321", facts.MaskedPhone);
    }

    [Fact]
    public void No_phone_on_file_reads_as_nothing_rather_than_an_empty_mask()
        => Assert.Null(new ClientAccountFacts().MaskedPhone);

    [Fact]
    public void An_account_with_no_phone_returns_null_instead_of_a_blank_record()
    {
        using var document = JsonDocument.Parse("""{"data":{"phoneNumberObfuscated":{}}}""");

        Assert.Null(ClientAccountProbe.ParsePhone(document.RootElement));
    }
}

/// <summary>Mastery and season rewards — dated achievements that cannot be bought.</summary>
public sealed class MasteryAndRewardTests
{
    [Fact]
    public void Mastery_is_summarised_rather_than_stored_whole()
    {
        using var document = JsonDocument.Parse("""
            [{"championId":86,"championLevel":7,"championPoints":300000},
             {"championId":99,"championLevel":5,"championPoints":120000},
             {"championId":22,"championLevel":4,"championPoints":50000}]
            """);

        var mastery = ClientStatsProbe.ParseMastery(document)!;

        Assert.Equal(3, mastery.ChampionsPlayed);
        Assert.Equal(470000, mastery.TotalPoints);
        Assert.Equal(86, mastery.Top[0].ChampionId);
    }

    [Fact]
    public void An_account_that_has_played_nothing_reports_null_rather_than_zeroes()
    {
        using var document = JsonDocument.Parse("[]");

        Assert.Null(ClientStatsProbe.ParseMastery(document));
    }

    [Fact]
    public void Season_rewards_come_back_in_season_order_with_their_dates()
    {
        using var document = JsonDocument.Parse("""
            [{"seasonId":13,"level":2,"theme":"ranked","earnedDateIso8601":"2023-11-20T00:00:00Z"},
             {"seasonId":11,"level":1,"theme":"ranked","earnedDateIso8601":"2021-11-15T00:00:00Z"}]
            """);

        var rewards = ClientStatsProbe.ParseSeasonRewards(document);

        Assert.Equal(11, rewards[0].SeasonId);
        Assert.Equal(2021, rewards[0].EarnedUtc!.Value.Year);
    }
}

/// <summary>Seasons collapse to one row each, however many flags Riot issued for them.</summary>
public sealed class SeasonRewardDedupeTests
{
    [Fact]
    public void Several_flags_for_one_season_become_a_single_entry_at_the_best_level()
    {
        // Split queues and themes each earn their own flag, so the raw list reads
        // "S11, S11, S11, S11, S11, S11" — six rows saying one thing.
        using var document = JsonDocument.Parse("""
            [{"seasonId":11,"level":1,"theme":"ranked","earnedDateIso8601":"2021-11-15T00:00:00Z"},
             {"seasonId":11,"level":3,"theme":"ranked","earnedDateIso8601":"2021-12-01T00:00:00Z"},
             {"seasonId":11,"level":2,"theme":null,"earnedDateIso8601":"2021-11-20T00:00:00Z"},
             {"seasonId":14,"level":1,"theme":"ranked","earnedDateIso8601":"2024-11-10T00:00:00Z"}]
            """);

        var rewards = ClientStatsProbe.ParseSeasonRewards(document);

        Assert.Equal(2, rewards.Count);

        var eleven = rewards.Single(r => r.SeasonId == 11);
        Assert.Equal(3, eleven.Level);
        Assert.Equal("ranked", eleven.Theme);

        // The earliest date is the one that dates the account's activity.
        Assert.Equal(15, eleven.EarnedUtc!.Value.UtcDateTime.Day);
    }

    [Fact]
    public void A_season_with_no_dated_flag_still_appears()
        => Assert.Single(ClientStatsProbe.ParseSeasonRewards(
            JsonDocument.Parse("""[{"seasonId":9,"level":1}]""")));
}
