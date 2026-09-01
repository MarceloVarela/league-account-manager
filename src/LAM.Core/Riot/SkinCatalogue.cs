using System.Text.Json;
using System.Text.Json.Serialization;

namespace LAM.Core.Riot;

/// <summary>One skin, as much of it as is worth displaying.</summary>
public sealed record CatalogueSkin(
    int Id,
    string Name,
    int ChampionId,
    string? Rarity,
    string? TilePath,
    DateTimeOffset? Released,
    int? RpCost = null,
    bool IsAvailable = true)
{
    /// <summary>Skin 0 for a champion is the base — worth telling apart from a bought skin.</summary>
    public bool IsBaseSkin => Id % 1000 == 0;

    /// <summary>
    /// No longer purchasable — retired, event-exclusive or otherwise gone.
    ///
    /// The two signals agree completely in practice: on the account this was built against, all 619
    /// owned skins with no listed price are exactly the 619 marked unavailable. That makes an
    /// unavailable skin the strongest evidence of an account's age that its collection can offer.
    /// </summary>
    public bool IsLegacy => !IsAvailable;
}

public sealed record CatalogueChampion(int Id, string Name);

/// <summary>
/// Names, rarity and art paths for every skin and champion, shared by all accounts.
///
/// Accounts store only ids. This is why: the catalogue runs to seven and a half thousand entries, and
/// duplicating it per account would put megabytes of identical strings into the vault. One shared
/// copy on disk — public data, so not encrypted — serves every account, and the join happens when
/// something is actually displayed.
///
/// The client hands over the whole thing in a single six-megabyte response, of which only the fields
/// below are kept.
/// </summary>
public sealed class SkinCatalogue
{
    /// <summary>
    /// Bumped whenever a fix changes what a *correct* cache looks like.
    ///
    /// Without this a bug fix here silently does nothing. The composition root calls
    /// <see cref="TryLoad"/> at startup, which sets <see cref="IsLoaded"/> whenever the file exists,
    /// so <see cref="RefreshAsync"/> is never reached and the cache is never rewritten — a catalogue
    /// captured by a broken build would be believed for ever. Rejecting an older stamp discards it
    /// once, automatically, instead of asking anyone to go and delete a file.
    ///
    /// 2: champion ids resolve properly (were all 0) and champion names come from the global list.
    /// 3: RP price and availability kept, for collection value and legacy detection.
    /// </summary>
    internal const int CurrentSchemaVersion = 3;

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly string _path;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Dictionary<int, CatalogueSkin>? _skins;
    private Dictionary<int, CatalogueChampion>? _champions;

    public SkinCatalogue(string directory, HttpClient? http = null)
    {
        _path = Path.Combine(directory, "catalogue.json");
        _http = http ?? SharedHttp;
    }

    public bool IsLoaded => _skins is not null;

    public int SkinCount => _skins?.Count ?? 0;

    public DateTimeOffset? CapturedUtc { get; private set; }

    /// <summary>Reads the cached catalogue from disk, if one has been captured.</summary>
    public bool TryLoad()
    {
        if (_skins is not null) return true;

        try
        {
            if (!File.Exists(_path)) return false;

            var stored = JsonSerializer.Deserialize<StoredCatalogue>(File.ReadAllText(_path));
            if (stored is null || stored.Skins.Count == 0) return false;
            if (stored.SchemaVersion != CurrentSchemaVersion) return false;

            _skins = stored.Skins.ToDictionary(s => s.Id);
            _champions = stored.Champions.ToDictionary(c => c.Id);
            CapturedUtc = stored.CapturedUtc;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Captures the catalogue from the running client and caches it.
    ///
    /// Skipped when a copy is already held and still recent — the response is six megabytes and the
    /// contents only change on a patch, so refetching it on every sign-in would be waste.
    /// </summary>
    public async Task<bool> RefreshAsync(
        RiotPaths paths, TimeSpan maxAge, CancellationToken cancellationToken)
    {
        if (TryLoad() && CapturedUtc is { } captured && DateTimeOffset.UtcNow - captured < maxAge)
            return true;

        var lockfile = Lockfile.Read(paths.LeagueLockfile);
        if (lockfile is null) return false;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var client = new RiotLocalApiClient(lockfile);

            var skins = await ReadSkinsAsync(client, cancellationToken);
            if (skins.Count == 0) return false;

            var champions = await ReadChampionsAsync(client, cancellationToken);

            // The game-data plugin can lag the rest of the client's API. Champion names being absent
            // would put "Unknown champion" on every row, so it is worth one public request to avoid.
            if (champions.Count == 0)
                champions = await ReadChampionsFromMirrorAsync(cancellationToken);

            _skins = skins.ToDictionary(s => s.Id);
            _champions = champions.ToDictionary(c => c.Id);
            CapturedUtc = DateTimeOffset.UtcNow;

            Save(new StoredCatalogue
            {
                SchemaVersion = CurrentSchemaVersion,
                CapturedUtc = CapturedUtc.Value,
                Skins = skins,
                Champions = champions,
            });

            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or HttpRequestException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<List<CatalogueSkin>> ReadSkinsAsync(
        RiotLocalApiClient client, CancellationToken cancellationToken)
    {
        using var document = await client.GetJsonAsync("/lol-catalog/v1/items/CHAMPION_SKIN", cancellationToken);
        return ParseSkins(document);
    }

    internal static List<CatalogueSkin> ParseSkins(JsonDocument? document)
    {
        if (document?.RootElement.ValueKind != JsonValueKind.Array) return [];

        var skins = new List<CatalogueSkin>();

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;

            var id = ReadInt(entry, "itemId");
            var name = ReadString(entry, "name");
            if (id is null || string.IsNullOrWhiteSpace(name)) continue;

            skins.Add(new CatalogueSkin(
                id.Value,
                name,
                NormaliseChampionId(FirstTaggedChampion(entry) ?? id.Value / 1000),
                NullIfBlank(ReadString(entry, "rarity")),
                NullIfBlank(ReadString(entry, "tilePath")),
                ReadDate(entry, "releaseDate"),
                ReadRpCost(entry),
                ReadBool(entry, "active") ?? true));
        }

        return skins;
    }

    /// <summary>
    /// Every champion in the game, from the client's static game data.
    ///
    /// Deliberately *not* <c>/lol-champions/v1/owned-champions-minimal</c>, which is what this used to
    /// read. That endpoint answers for the signed-in account, and this catalogue is shared by every
    /// account in the vault — so whichever account happened to capture it first would define champion
    /// names for all the others, and a fresh account owning a handful of champions would leave every
    /// other account's collection reading "Unknown champion". This list is account-independent.
    /// </summary>
    private static async Task<List<CatalogueChampion>> ReadChampionsAsync(
        RiotLocalApiClient client, CancellationToken cancellationToken)
    {
        using var document = await client.GetJsonAsync(ChampionSummaryPath, cancellationToken);
        return ParseChampions(document);
    }

    internal const string ChampionSummaryPath = "/lol-game-data/assets/v1/champion-summary.json";

    /// <summary>
    /// The same champion list from the public mirror, for when the client cannot supply it.
    ///
    /// Uses the mapping already proven for skin art in <see cref="SkinArtCache.ToCommunityDragonUrl"/>
    /// — the mirror serves the client's asset tree with the prefix stripped and the path lowercased.
    /// </summary>
    private async Task<List<CatalogueChampion>> ReadChampionsFromMirrorAsync(CancellationToken cancellationToken)
    {
        try
        {
            var url = SkinArtCache.ToCommunityDragonUrl(ChampionSummaryPath);
            await using var stream = await _http.GetStreamAsync(url, cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            return ParseChampions(document);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Parses the champion summary. The list carries an <c>id: -1</c> "None" placeholder that must
    /// not become a champion.
    /// </summary>
    internal static List<CatalogueChampion> ParseChampions(JsonDocument? document)
    {
        if (document?.RootElement.ValueKind != JsonValueKind.Array) return [];

        var champions = new List<CatalogueChampion>();

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;

            var id = ReadInt(entry, "id");
            var name = ReadString(entry, "name");
            if (id is null or <= 0 || string.IsNullOrWhiteSpace(name)) continue;

            champions.Add(new CatalogueChampion(id.Value, name));
        }

        return champions;
    }

    // ---- lookup ------------------------------------------------------------

    public CatalogueSkin? Skin(int id) => _skins?.GetValueOrDefault(id);

    public string ChampionName(int id)
        => _champions?.GetValueOrDefault(NormaliseChampionId(id))?.Name ?? "Unknown champion";

    /// <summary>
    /// Skins whose name contains the query, best matches first.
    ///
    /// Ordered so an exact name beats a prefix beats a substring — searching "Lux" should lead with
    /// Lux's own skins rather than whatever alphabetically happens to contain those letters.
    /// </summary>
    public IReadOnlyList<CatalogueSkin> FindByName(string query, int limit = 200)
    {
        if (_skins is null || string.IsNullOrWhiteSpace(query)) return [];

        var needle = query.Trim();

        return _skins.Values
            .Select(skin => (Skin: skin, Rank: MatchRank(skin.Name, needle)))
            .Where(match => match.Rank < int.MaxValue)
            .OrderBy(match => match.Rank)
            .ThenBy(match => match.Skin.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .Select(match => match.Skin)
            .ToList();
    }

    private static int MatchRank(string name, string needle)
    {
        if (string.Equals(name, needle, StringComparison.CurrentCultureIgnoreCase)) return 0;
        if (name.StartsWith(needle, StringComparison.CurrentCultureIgnoreCase)) return 1;
        return name.Contains(needle, StringComparison.CurrentCultureIgnoreCase) ? 2 : int.MaxValue;
    }

    /// <summary>
    /// Resolves owned ids into displayable rows.
    ///
    /// An id the catalogue has never seen still produces a row, labelled by its number. A skin
    /// released after the cache was captured must not vanish from a collection — a gap in the list is
    /// far more confusing than a row that admits it does not know the name yet.
    /// </summary>
    public IReadOnlyList<CatalogueSkin> Resolve(IEnumerable<int> ownedIds)
        => ownedIds
            .Select(id => Skin(id)
                          ?? new CatalogueSkin(id, "Skin " + id, NormaliseChampionId(id / 1000), null, null, null))
            .ToList();

    private void Save(StoredCatalogue catalogue)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(_path, JsonSerializer.Serialize(catalogue));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An uncacheable catalogue still works for this run; it simply refetches next time.
        }
    }

    private sealed class StoredCatalogue
    {
        public int SchemaVersion { get; set; }
        public DateTimeOffset CapturedUtc { get; set; }
        public List<CatalogueSkin> Skins { get; set; } = [];
        public List<CatalogueChampion> Champions { get; set; } = [];
    }

    /// <summary>
    /// The champion a skin belongs to, when the catalogue tags it explicitly.
    ///
    /// The <c>&gt; 0</c> is load-bearing. Every entry the client returns carries
    /// <c>taggedChampionsIds: [0]</c>, and returning that zero made the caller's <c>??</c> fallback
    /// dead code — so all 7,300 skins resolved to champion 0, which no dictionary contains, and every
    /// row in the collection read "Unknown champion". A boxed zero is not null.
    /// </summary>
    private static int? FirstTaggedChampion(JsonElement entry)
    {
        if (!entry.TryGetProperty("taggedChampionsIds", out var tagged) || tagged.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var value in tagged.EnumerateArray())
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var id) && id > 0)
                return id;

        return null;
    }

    /// <summary>
    /// Folds the <c>Jade_*</c> champion variants onto the champion they duplicate.
    ///
    /// The game data carries a second set of champions offset by 60,000 — <c>60001 Jade_Annie</c>
    /// alongside <c>1 Annie</c> — sharing the real champion's display name. Their skins derive to a
    /// 60,000-range champion id that no ordinary lookup resolves, so without this they would be the
    /// only rows still reading "Unknown champion" after the fix above.
    /// </summary>
    internal static int NormaliseChampionId(int championId)
        => championId >= JadeOffset ? championId - JadeOffset : championId;

    /// <summary>Champion ids at or above this are <c>Jade_*</c> duplicates of the champion below it.</summary>
    internal const int JadeOffset = 60_000;

    /// <summary>
    /// Whether a skin id belongs to a <c>Jade_*</c> champion rather than a real one.
    ///
    /// The same double-counting that reported 236 champions for a 173-champion roster applies to
    /// skins: 305 of this account's 1,582 "owned" skins are Jade duplicates, and 234 of those carry
    /// no name at all, so they would list as bare id numbers. Counting them inflates a collection by
    /// roughly a fifth.
    /// </summary>
    public static bool IsJadeSkin(int skinId) => skinId / 1000 >= JadeOffset;

    /// <summary>
    /// The RP price, if the skin still has one.
    ///
    /// The array can hold several currencies and is empty for anything unpurchasable, so only an
    /// explicit RP entry counts. Roughly one owned skin in three has no price at all — see
    /// <see cref="CatalogueSkin.IsLegacy"/> — and those must stay null rather than becoming zero, or
    /// a collection total would quietly understate itself while looking complete.
    /// </summary>
    private static int? ReadRpCost(JsonElement entry)
    {
        if (!entry.TryGetProperty("prices", out var prices) || prices.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var price in prices.EnumerateArray())
        {
            if (price.ValueKind != JsonValueKind.Object) continue;
            if (!string.Equals(ReadString(price, "currency"), "RP", StringComparison.OrdinalIgnoreCase)) continue;
            if (ReadInt(price, "cost") is { } cost and > 0) return cost;
        }

        return null;
    }

    private static bool? ReadBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static DateTimeOffset? ReadDate(JsonElement root, string name)
        => DateTimeOffset.TryParse(ReadString(root, name), out var parsed) ? parsed : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
