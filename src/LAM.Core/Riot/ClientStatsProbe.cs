using System.Text.Json;
using LAM.Core.Model;

namespace LAM.Core.Riot;

/// <summary>
/// Reads rank, currency and collection straight from the League client.
///
/// Everything here was previously fetched through Riot's developer API, which turned out to be the
/// source of nearly every failure in this app: keys that expire daily, 403s that meant an endpoint
/// problem rather than a key problem, and an "encrypted PUUID" that is unique per key and therefore
/// silently wrong the moment a key is regenerated. The client answers all of it with no key, no rate
/// limit and no expiry.
///
/// The trade is freshness: these can only be read while that account is signed in, so what the app
/// stores is a snapshot from the last sign-in. That is worth being explicit about in the UI rather
/// than passing off as live — and it is still the better source, because it is exact at the moment
/// of capture. The API key remains useful for one thing: refreshing an account you are *not* signed
/// into.
/// </summary>
public sealed class ClientStatsProbe
{
    private readonly RiotPaths _paths;

    public ClientStatsProbe(RiotPaths paths) => _paths = paths;

    public async Task<ClientStats?> TryReadAsync(CancellationToken cancellationToken)
    {
        var lockfile = Lockfile.Read(_paths.LeagueLockfile);
        if (lockfile is null) return null;

        using var client = new RiotLocalApiClient(lockfile);

        var stats = new ClientStats { CapturedUtc = DateTimeOffset.UtcNow };

        stats = await AddRanksAsync(client, stats, cancellationToken);

        // The wallet is served by the SAME lol-inventory plugin the collection waits on, so it loses
        // the identical race: read at step two it answers nothing while ranks and mastery already
        // reply, and the account then shows "—" for RP and BE beside a complete collection. Wait for
        // the gate first, then read it — but attempt it even when the gate timed out, because a
        // readiness endpoint that merely 404s on some patch must not permanently suppress a wallet
        // read that would have worked.
        var inventoryReady = await WaitForInventoryAsync(client, cancellationToken);

        stats = await AddWalletAsync(client, stats, cancellationToken);
        stats = await AddCollectionAsync(client, stats, inventoryReady, cancellationToken);
        stats = await AddHonorAsync(client, stats, cancellationToken);
        stats = await AddMasteryAsync(client, stats, cancellationToken);
        stats = await AddSeasonRewardsAsync(client, stats, cancellationToken);

        return stats.IsEmpty ? null : stats;
    }

    private static async Task<ClientStats> AddRanksAsync(
        RiotLocalApiClient client, ClientStats stats, CancellationToken cancellationToken)
    {
        using var document = await client.GetJsonAsync("/lol-ranked/v1/current-ranked-stats", cancellationToken);
        if (document is null) return stats;

        var root = document.RootElement;
        if (!root.TryGetProperty("queueMap", out var queues) || queues.ValueKind != JsonValueKind.Object)
            return stats;

        return stats with
        {
            Solo = ReadQueue(queues, "RANKED_SOLO_5x5"),
            Flex = ReadQueue(queues, "RANKED_FLEX_SR"),
            PeakTier = root.TryGetProperty("highestRankedEntry", out var peak) && peak.ValueKind == JsonValueKind.Object
                ? ReadString(peak, "tier")
                : null,

            // Already in this response and previously discarded. "What did you finish last season
            // at" is a different question from the current rank and a better measure of an account.
            PreviousSeasonPeak = Describe(
                ReadString(root, "highestPreviousSeasonEndTier"),
                ReadString(root, "highestPreviousSeasonEndDivision")),
        };
    }

    /// <summary>
    /// Reads one queue. Note the field names differ from the public API: the client says
    /// <c>division</c> where the API says <c>rank</c>, which is an easy way to end up with a blank
    /// division that looks like a data problem rather than a naming one.
    /// </summary>
    private static RankInfo? ReadQueue(JsonElement queues, string queueName)
    {
        if (!queues.TryGetProperty(queueName, out var entry) || entry.ValueKind != JsonValueKind.Object)
            return null;

        var tier = ReadString(entry, "tier");
        if (string.IsNullOrWhiteSpace(tier) || tier == "NONE") return null;

        return new RankInfo
        {
            Tier = tier,
            Division = ReadString(entry, "division") is { } division && division != "NA" ? division : null,
            LeaguePoints = ReadInt(entry, "leaguePoints") ?? 0,
            Wins = ReadInt(entry, "wins") ?? 0,
            Losses = ReadInt(entry, "losses") ?? 0,
            PlacementsRemaining = ReadInt(entry, "provisionalGamesRemaining") ?? 0,
        };
    }

    /// <summary>Tier and division together, skipping the placeholders the client uses for "none".</summary>
    private static string? Describe(string? tier, string? division)
    {
        if (string.IsNullOrWhiteSpace(tier) || tier == "NONE") return null;

        var name = char.ToUpperInvariant(tier[0]) + tier[1..].ToLowerInvariant();
        return string.IsNullOrWhiteSpace(division) || division == "NA" ? name : name + " " + division;
    }

    internal const string MasteryPath = "/lol-champion-mastery/v1/local-player/champion-mastery";

    /// <summary>
    /// Champion mastery, summarised.
    ///
    /// Kept as a summary rather than all 170-odd rows: the total points and the top few champions are
    /// what identifies an account, and storing the full table per account would cost far more than it
    /// tells you.
    /// </summary>
    private static async Task<ClientStats> AddMasteryAsync(
        RiotLocalApiClient client, ClientStats stats, CancellationToken cancellationToken)
    {
        using var document = await client.GetJsonAsync(MasteryPath, cancellationToken);
        return stats with { Mastery = ParseMastery(document) };
    }

    internal static MasterySummary? ParseMastery(JsonDocument? document)
    {
        if (document?.RootElement.ValueKind != JsonValueKind.Array) return null;

        long totalPoints = 0;
        var entries = new List<(int ChampionId, int Level, int Points)>();

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (ReadInt(entry, "championId") is not { } id) continue;

            var points = ReadInt(entry, "championPoints") ?? 0;
            totalPoints += points;
            entries.Add((id, ReadInt(entry, "championLevel") ?? 0, points));
        }

        if (entries.Count == 0) return null;

        var top = entries
            .OrderByDescending(e => e.Points)
            .Take(5)
            .Select(e => new MasteryEntry(e.ChampionId, e.Level, e.Points))
            .ToList();

        return new MasterySummary(entries.Count, totalPoints, top);
    }

    internal const string SeasonRewardsPath = "/lol-banners/v1/current-summoner/flags";

    /// <summary>
    /// Ranked season rewards, which are dated and cannot be bought.
    ///
    /// A season banner earned in a given year is hard evidence the account was played then - useful
    /// both as ownership proof and as a record of what an account actually achieved.
    /// </summary>
    private static async Task<ClientStats> AddSeasonRewardsAsync(
        RiotLocalApiClient client, ClientStats stats, CancellationToken cancellationToken)
    {
        using var document = await client.GetJsonAsync(SeasonRewardsPath, cancellationToken);
        return stats with { SeasonRewards = ParseSeasonRewards(document) };
    }

    internal static IReadOnlyList<SeasonReward> ParseSeasonRewards(JsonDocument? document)
    {
        if (document?.RootElement.ValueKind != JsonValueKind.Array) return [];

        var rewards = new List<SeasonReward>();

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;

            var season = ReadInt(entry, "seasonId");
            if (season is null) continue;

            rewards.Add(new SeasonReward(
                season.Value,
                ReadInt(entry, "level") ?? 0,
                ReadString(entry, "theme"),
                DateTimeOffset.TryParse(ReadString(entry, "earnedDateIso8601"), out var earned) ? earned : null));
        }

        // One season can carry several flags - split queues and themes each earn their own - so a
        // straight list reads as "S11, S11, S11, S11, S11, S11". Keep the best per season, and the
        // earliest date it was earned.
        return [.. rewards
            .GroupBy(r => r.SeasonId)
            .Select(g => new SeasonReward(
                g.Key,
                g.Max(r => r.Level),
                g.Select(r => r.Theme).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)),
                g.Where(r => r.EarnedUtc is not null).Min(r => r.EarnedUtc)))
            .OrderBy(r => r.SeasonId)];
    }

    private static async Task<ClientStats> AddWalletAsync(
        RiotLocalApiClient client, ClientStats stats, CancellationToken cancellationToken)
    {
        // The currency types have to be named explicitly; without them the endpoint answers 400.
        const string path = "/lol-inventory/v1/wallet?currencyTypes=%5B%22RP%22,%22lol_blue_essence%22%5D";

        using var document = await client.GetJsonAsync(path, cancellationToken);
        if (document is null) return stats;

        return stats with
        {
            RiotPoints = ReadInt(document.RootElement, "RP"),
            BlueEssence = ReadInt(document.RootElement, "lol_blue_essence"),
        };
    }

    /// <summary>
    /// Reads the collection, but only once the client is actually serving it.
    ///
    /// The lockfile lands well before the inventory is loaded, and an early read of the champions
    /// endpoint returns just the twenty champions of the current free rotation. That is where the
    /// long-standing "20 champions owned" came from on an account that owns every champion — not a
    /// parsing fault, a race. <see cref="LcuIdentityProbe"/> documents the same hazard and polls; this
    /// does too.
    ///
    /// The client says so itself: <c>/lol-inventory/v1/initial-configuration-complete</c> turns true
    /// once the inventory has loaded. Stability of the count is then a second, cheap check. Neither
    /// alone is enough — a threshold ("20 looks wrong") would break on a genuinely small account, and
    /// stability alone can be fooled by the rotation sitting at twenty for longer than one poll.
    /// </summary>
    private static async Task<ClientStats> AddCollectionAsync(
        RiotLocalApiClient client, ClientStats stats, bool inventoryReady,
        CancellationToken cancellationToken)
    {
        // The gate is now awaited by the caller, so the wallet can share it.
        if (!inventoryReady) return stats with { CollectionIsComplete = false };

        var championIds = await ReadWhenStableAsync(
            client, ChampionsPath, ReadChampionIds, cancellationToken);

        var skinIds = await ReadWhenStableAsync(
            client, "/lol-inventory/v2/inventory/CHAMPION_SKIN", ReadOwnedSkinIds, cancellationToken);

        // Re-read once for the purchase dates. Worth the extra call: the earliest one answers "what
        // was the first champion you bought?", which is a standard Riot Support ownership question
        // and was previously listed in this app as something only the user could supply.
        using var champions = await client.GetJsonAsync(ChampionsPath, cancellationToken);
        var first = ReadFirstPurchase(champions);

        // Acquisition dates per skin, from the catalogue endpoint - it reports ownership and a date
        // beside every price, so the provenance data costs one call rather than a second source.
        using var catalog = await client.GetJsonAsync(SkinCatalogPath, cancellationToken);
        var acquired = ReadSkinAcquisitionDates(catalog);

        // Only real champions are counted. The game data carries Jade_* duplicates offset by 60,000
        // that share their originals' names, so counting them reports 236 for a 173-champion roster
        // and lists every champion twice.
        var realChampions = (championIds ?? []).Where(id => id < SkinCatalogue.JadeOffset).ToArray();

        // Skins carry the same duplicates as champions — 60001008 is a skin of "Jade Annie" — and
        // most of them have no name in the catalogue at all, so counting them both inflates the
        // total and would list bare id numbers in the collection.
        var realSkins = (skinIds ?? []).Where(id => !SkinCatalogue.IsJadeSkin(id)).ToArray();

        return stats with
        {
            ChampionsOwned = championIds is null ? null : realChampions.Length,
            SkinsOwned = skinIds is null ? null : realSkins.Length,
            OwnedChampionIds = championIds ?? [],
            OwnedSkinIds = skinIds ?? [],
            CollectionIsComplete = championIds is not null && skinIds is not null,
            FirstChampionId = first?.ChampionId,
            FirstChampionPurchasedUtc = first?.PurchasedUtc,
            SkinAcquiredEpochSeconds = acquired,
        };
    }

    internal const string ChampionsPath = "/lol-champions/v1/owned-champions-minimal";

    internal const string SkinCatalogPath = "/lol-catalog/v1/items/CHAMPION_SKIN";

    /// <summary>
    /// When each owned skin was acquired.
    ///
    /// Read from the catalogue endpoint, which reports ownership and an acquisition date alongside
    /// the price - so this costs one call that the shared catalogue already makes for other reasons.
    ///
    /// The unit is SECONDS here, against milliseconds on the champions endpoint. Reading one as the
    /// other silently dates an entire collection to January 1970, which looks like corrupt data
    /// rather than a unit mistake.
    /// </summary>
    internal static Dictionary<int, int> ReadSkinAcquisitionDates(JsonDocument? document)
    {
        var dates = new Dictionary<int, int>();
        if (document?.RootElement.ValueKind != JsonValueKind.Array) return dates;

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (ReadBool(entry, "owned") != true) continue;
            if (ReadInt(entry, "itemId") is not { } id) continue;
            if (ReadInt(entry, "purchaseDate") is not { } epochSeconds || epochSeconds <= 0) continue;

            dates[id] = epochSeconds;
        }

        return dates;
    }

    /// <summary>
    /// The earliest champion purchase on the account.
    ///
    /// Every entry carries <c>ownership.rental.purchaseDate</c>, so the oldest one is the first
    /// champion ever bought. Jade duplicates share their original's timestamp exactly, hence the
    /// normalisation — otherwise the answer would arrive as "Garen, or possibly Garen".
    /// </summary>
    internal static (int ChampionId, DateTimeOffset PurchasedUtc)? ReadFirstPurchase(JsonDocument? document)
    {
        if (document?.RootElement.ValueKind != JsonValueKind.Array) return null;

        (int ChampionId, DateTimeOffset PurchasedUtc)? earliest = null;

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (ReadInt(entry, "id") is not { } id) continue;

            if (!entry.TryGetProperty("ownership", out var ownership)
                || ownership.ValueKind != JsonValueKind.Object) continue;
            if (!ownership.TryGetProperty("rental", out var rental)
                || rental.ValueKind != JsonValueKind.Object) continue;
            if (!rental.TryGetProperty("purchaseDate", out var date)
                || date.ValueKind != JsonValueKind.Number) continue;
            if (!date.TryGetInt64(out var epoch) || epoch <= 0) continue;

            var purchased = DateTimeOffset.FromUnixTimeMilliseconds(epoch);

            // Prefer the real champion id over its Jade twin when the timestamps tie.
            var normalised = SkinCatalogue.NormaliseChampionId(id);

            if (earliest is null
                || purchased < earliest.Value.PurchasedUtc
                || (purchased == earliest.Value.PurchasedUtc && normalised < earliest.Value.ChampionId))
            {
                earliest = (normalised, purchased);
            }
        }

        return earliest;
    }

    /// <summary>How long to wait for the inventory to settle before giving up on this capture.</summary>
    private static readonly TimeSpan CollectionReadyTimeout = TimeSpan.FromSeconds(45);

    internal const string InventoryReadyPath = "/lol-inventory/v1/initial-configuration-complete";

    /// <summary>
    /// Waits for the client to report that it has finished loading the player's inventory.
    ///
    /// Until this is true the champions endpoint answers with the free rotation, which is exactly how
    /// an account owning every champion came to be recorded as owning twenty.
    /// </summary>
    private static async Task<bool> WaitForInventoryAsync(
        RiotLocalApiClient client, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + CollectionReadyTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var document = await client.GetJsonAsync(InventoryReadyPath, cancellationToken);
            if (document?.RootElement.ValueKind == JsonValueKind.True) return true;

            await Task.Delay(1000, cancellationToken);
        }

        return false;
    }

    /// <summary>
    /// Polls an endpoint until two consecutive reads return the same non-zero count.
    ///
    /// Returns null when it never settled, which the caller turns into "this capture is incomplete" —
    /// far better than recording a half-loaded collection over a good one.
    /// </summary>
    private static async Task<int[]?> ReadWhenStableAsync(
        RiotLocalApiClient client,
        string path,
        Func<JsonDocument?, int[]?> parse,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + CollectionReadyTimeout;
        int[]? previous = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var document = await client.GetJsonAsync(path, cancellationToken);
            var current = parse(document);

            if (current is { Length: > 0 } && previous is not null && previous.Length == current.Length)
                return current;

            previous = current;
            await Task.Delay(1500, cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// Every champion the endpoint lists.
    ///
    /// Deliberately unfiltered, unlike <see cref="ReadOwnedSkinIds"/> below. The asymmetry looks like
    /// an oversight and is not: every entry here reports <c>ownership.owned == true</c> with a real
    /// purchase date, and the <c>freeToPlay</c> flag marks the *current rotation* — a champion you
    /// bought years ago is flagged in the week it is free. Filtering on it would silently delete
    /// twenty owned champions. On the skin inventory the same-looking <c>f2p</c> flag means a
    /// temporary grant, which is the opposite, and there the filter is correct.
    /// </summary>
    internal static int[]? ReadChampionIds(JsonDocument? document) => ReadIds(document, "id");

    private static int[]? ReadIds(JsonDocument? document, string field)
    {
        if (document?.RootElement.ValueKind != JsonValueKind.Array) return null;

        var ids = new List<int>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (ReadInt(entry, field) is { } id) ids.Add(id);
        }

        return [.. ids];
    }

    /// <summary>
    /// Counts skins the account actually owns.
    ///
    /// Every entry the endpoint returns carries <c>owned: true</c>, so a naive count reports the free
    /// rotation and any rentals as part of the collection — on the account this was written against
    /// that inflates the figure considerably. Free-to-play grants and rentals are excluded.
    /// </summary>
    internal static int[]? ReadOwnedSkinIds(JsonDocument? skins)
    {
        if (skins?.RootElement.ValueKind != JsonValueKind.Array) return null;

        var owned = new List<int>();
        foreach (var entry in skins.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (ReadBool(entry, "owned") != true) continue;
            if (ReadBool(entry, "f2p") == true) continue;
            if (ReadBool(entry, "rental") == true) continue;

            if (ReadInt(entry, "itemId") is { } id) owned.Add(id);
        }

        return [.. owned];
    }

    private static async Task<ClientStats> AddHonorAsync(
        RiotLocalApiClient client, ClientStats stats, CancellationToken cancellationToken)
    {
        using var document = await client.GetJsonAsync("/lol-honor-v2/v1/profile", cancellationToken);
        if (document is null) return stats;

        return stats with { HonorLevel = ReadInt(document.RootElement, "honorLevel") };
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static bool? ReadBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}

/// <summary>
/// A snapshot of an account taken from the client at sign-in.
///
/// Explicitly a snapshot: <see cref="CapturedUtc"/> exists so the UI can say "as of last sign-in"
/// rather than implying these numbers are live.
/// </summary>
public sealed record ClientStats
{
    public DateTimeOffset CapturedUtc { get; init; }

    public RankInfo? Solo { get; init; }
    public RankInfo? Flex { get; init; }
    public string? PeakTier { get; init; }

    public int? RiotPoints { get; init; }
    public int? BlueEssence { get; init; }

    public int? ChampionsOwned { get; init; }
    public int? SkinsOwned { get; init; }

    /// <summary>
    /// What the account actually owns, as ids.
    ///
    /// Ids rather than names on purpose: names, rarity and art all come from one shared catalogue,
    /// so twenty accounts do not each store their own copy of the same few thousand strings. This
    /// keeps an account at a few kilobytes instead of megabytes.
    /// </summary>
    public int[] OwnedSkinIds { get; init; } = [];

    public int[] OwnedChampionIds { get; init; } = [];

    /// <summary>
    /// Whether the collection was read from a client that had finished loading it.
    ///
    /// False means the numbers here are a partial view and must not be written over a good capture.
    /// </summary>
    public bool CollectionIsComplete { get; init; }

    /// <summary>
    /// The first champion ever bought on this account, and when.
    ///
    /// Recovery evidence rather than a statistic: it is one of the few ownership questions Riot
    /// Support asks that an attacker cannot look up. Stored as an id, with the name resolved from the
    /// shared catalogue like every other collection id.
    /// </summary>
    public int? FirstChampionId { get; init; }

    public DateTimeOffset? FirstChampionPurchasedUtc { get; init; }

    /// <summary>Acquisition date per owned skin, in epoch SECONDS. Moved onto the identity by ApplyTo.</summary>
    public Dictionary<int, int> SkinAcquiredEpochSeconds { get; init; } = [];

    public int? HonorLevel { get; init; }

    /// <summary>Where the account finished the previous ranked season.</summary>
    public string? PreviousSeasonPeak { get; init; }

    public MasterySummary? Mastery { get; init; }

    public IReadOnlyList<SeasonReward> SeasonRewards { get; init; } = [];

    public bool IsEmpty =>
        Solo is null && Flex is null && RiotPoints is null && ChampionsOwned is null && HonorLevel is null;

    /// <summary>Folds the snapshot into an account, replacing the previous one wholesale.</summary>
    public void ApplyTo(AccountEntry account)
    {
        var identity = account.Identity;

        // Client-sourced ranks are exact at capture, so they supersede anything the API supplied.
        if (Solo is not null) identity.SoloRank = Solo;
        if (Flex is not null) identity.FlexRank = Flex;

        // The collection lives on the identity rather than inside the stats snapshot so it survives
        // a stats refresh that could not reach the inventory endpoints.
        //
        // Only a complete read may be written. The previous guard was `Length > 0`, which rejects an
        // empty read but happily accepts a *short* one — so a capture taken while the client was
        // still loading overwrote a good collection permanently, and every later sign-in simply
        // re-confirmed the wrong figure. An incomplete capture now leaves the stored collection, and
        // the counts derived from it, exactly as they were.
        if (CollectionIsComplete)
        {
            identity.OwnedSkinIds = OwnedSkinIds;
            identity.OwnedChampionIds = OwnedChampionIds;

            // Only replace the dates when this capture actually produced some; an endpoint that went
            // missing must not wipe provenance that was already established.
            if (SkinAcquiredEpochSeconds.Count > 0)
                identity.SkinAcquiredEpochSeconds = SkinAcquiredEpochSeconds;
        }

        // The wallet is preserved across BOTH branches. It is read after the readiness gate now, so
        // it should rarely be missing — but a single un-retried GET can still fail for unrelated
        // reasons, and without this a capture that missed it wrote null over a good balance and the
        // card showed "—" from then on.
        var previous = identity.ClientStats;

        var kept = this with
        {
            RiotPoints = RiotPoints ?? previous?.RiotPoints,
            BlueEssence = BlueEssence ?? previous?.BlueEssence,
        };

        identity.ClientStats = CollectionIsComplete
            ? kept
            : kept with
            {
                // Keep the fresh rank and wallet, but do not let a partial collection be displayed.
                ChampionsOwned = previous?.ChampionsOwned,
                SkinsOwned = previous?.SkinsOwned,
                OwnedChampionIds = [],
                OwnedSkinIds = [],

                // The first purchase comes from the same half-loaded list, so a partial read could
                // name whichever rotation champion happened to be oldest. Keep what was proven.
                FirstChampionId = previous?.FirstChampionId,
                FirstChampionPurchasedUtc = previous?.FirstChampionPurchasedUtc,
            };
    }
}

/// <summary>Champion mastery boiled down to what identifies an account.</summary>
public sealed record MasterySummary(int ChampionsPlayed, long TotalPoints, IReadOnlyList<MasteryEntry> Top);

public sealed record MasteryEntry(int ChampionId, int Level, int Points);

/// <summary>A ranked season reward - dated, earned, and impossible to buy.</summary>
public sealed record SeasonReward(int SeasonId, int Level, string? Theme, DateTimeOffset? EarnedUtc);
