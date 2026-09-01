using System.Text.Json;

namespace LAM.Core.Riot;

/// <summary>
/// Reads the account's loot — shards, essence, keys, chests and eternals.
///
/// Worth keeping alongside the collection because loot is the part of an account that quietly decays:
/// shards expire, chests go unopened, and orange essence sitting idle is a skin not owned. Seeing it
/// without signing in is the whole point of this app.
///
/// Strictly read-only. This app can display loot and must never be able to disenchant, reroll or
/// redeem it — an automation bug there would destroy something unrecoverable.
/// </summary>
public sealed class ClientLootProbe
{
    internal const string LootPath = "/lol-loot/v1/player-loot";

    private readonly RiotPaths _paths;

    public ClientLootProbe(RiotPaths paths) => _paths = paths;

    public async Task<LootSnapshot?> TryReadAsync(CancellationToken cancellationToken)
    {
        var lockfile = Lockfile.Read(_paths.LeagueLockfile);
        if (lockfile is null) return null;

        using var client = new RiotLocalApiClient(lockfile);
        using var document = await client.GetJsonAsync(LootPath, cancellationToken);

        return Parse(document, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Turns the client's loot response into a snapshot.
    ///
    /// The endpoint has been seen returning both an array and an id-keyed object, so both are
    /// accepted rather than relying on whichever shape happened to ship this patch.
    /// </summary>
    internal static LootSnapshot? Parse(JsonDocument? document, DateTimeOffset nowUtc)
    {
        if (document is null) return null;

        var root = document.RootElement;
        var entries = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray().ToList(),
            JsonValueKind.Object => root.EnumerateObject().Select(p => p.Value).ToList(),
            _ => null,
        };

        if (entries is null) return null;

        var items = new List<LootItem>();

        foreach (var entry in entries)
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;

            var id = ReadString(entry, "lootId") ?? ReadString(entry, "lootName");
            if (string.IsNullOrWhiteSpace(id)) continue;

            var count = ReadInt(entry, "count") ?? 0;
            if (count <= 0) continue;

            var type = ReadString(entry, "type") ?? string.Empty;

            items.Add(new LootItem
            {
                LootId = id,
                // Skins and wards name themselves in itemDesc, eternals in localizedName, and the
                // currencies in neither — blue and orange essence come back with both fields empty,
                // so without the fallback the most valuable rows would be blank.
                Name = FirstNonBlank(
                           ReadString(entry, "itemDesc"),
                           ReadString(entry, "localizedName"),
                           FriendlyName(id))
                       ?? id,
                Count = count,
                Type = type,
                Rarity = ReadString(entry, "rarity"),
                Category = CategoryOf(NullIfBlank(ReadString(entry, "displayCategories")), type),
                TilePath = NullIfBlank(ReadString(entry, "tilePath")),
                DisenchantValue = ReadInt(entry, "disenchantValue"),
                Value = ReadInt(entry, "value"),
                ExpiresUtc = ReadEpochMilliseconds(entry, "expiryTime"),
                IsRental = ReadBool(entry, "isRental") ?? false,
            });
        }

        return new LootSnapshot { CapturedUtc = nowUtc, Items = items };
    }

    private static string? FirstNonBlank(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

    /// <summary>
    /// Names for the loot the client does not name itself.
    ///
    /// These are stable identifiers rather than display strings, so hard-coding them is safe — and
    /// the alternative is showing "CURRENCY_champion: 272,382" where "Blue Essence" belongs.
    /// </summary>
    private static string? FriendlyName(string lootId) => lootId switch
    {
        "CURRENCY_champion" => "Blue Essence",
        "CURRENCY_cosmetic" => "Orange Essence",
        "CURRENCY_mythic" => "Mythic Essence",
        "CURRENCY_RP" => "Riot Points",
        "MATERIAL_key" => "Hextech Key",
        "MATERIAL_key_fragment" => "Key Fragment",
        "MATERIAL_clashtickets" => "Clash Ticket",
        "CHEST_generic" => "Hextech Chest",
        _ => null,
    };

    /// <summary>
    /// Which section an item belongs in.
    ///
    /// <c>displayCategories</c> is blank on every currency and material, so the client's own type is
    /// the fallback. Currency is forced into one group because the client files Mythic Essence under
    /// CHEST, which would scatter the essences across two sections for no benefit.
    /// </summary>
    private static string CategoryOf(string? displayCategory, string type)
    {
        if (string.Equals(type, "CURRENCY", StringComparison.OrdinalIgnoreCase)) return "CURRENCY";

        return FirstNonBlank(displayCategory, type) ?? "OTHER";
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

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

    /// <summary>Expiry is milliseconds since epoch, and 0 means "does not expire".</summary>
    private static DateTimeOffset? ReadEpochMilliseconds(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        if (!value.TryGetInt64(out var epoch) || epoch <= 0) return null;

        return DateTimeOffset.FromUnixTimeMilliseconds(epoch);
    }
}

/// <summary>One line of the loot inventory.</summary>
public sealed record LootItem
{
    public required string LootId { get; init; }
    public required string Name { get; init; }
    public int Count { get; init; }

    /// <summary>The client's own type, e.g. <c>SKIN_RENTAL</c>, <c>CHAMPION_RENTAL</c>, <c>CURRENCY</c>.</summary>
    public string Type { get; init; } = string.Empty;

    public string? Rarity { get; init; }

    /// <summary>The grouping — SKIN, WARDSKIN, CHAMPION, MATERIAL, ETERNALS, CHEST, CURRENCY.</summary>
    public string? Category { get; init; }

    public string? TilePath { get; init; }

    /// <summary>Blue or orange essence this would disenchant into.</summary>
    public int? DisenchantValue { get; init; }

    public int? Value { get; init; }

    public DateTimeOffset? ExpiresUtc { get; init; }

    public bool IsRental { get; init; }

    /// <summary>Currencies are counts, not things you own copies of, and are shown as totals.</summary>
    public bool IsCurrency => string.Equals(Type, "CURRENCY", StringComparison.OrdinalIgnoreCase);

    public bool IsExpiring => ExpiresUtc is not null;
}

/// <summary>The loot inventory as the client last reported it.</summary>
public sealed record LootSnapshot
{
    public DateTimeOffset CapturedUtc { get; init; }

    public IReadOnlyList<LootItem> Items { get; init; } = [];

    public bool IsEmpty => Items.Count == 0;

    /// <summary>
    /// Groups loot the way the client's own Crafting page does, so the two can be compared at a
    /// glance. Anything in a category not listed here still appears, at the end — a new loot type
    /// arriving in a patch must not silently vanish from the view.
    /// </summary>
    private static readonly string[] CategoryOrder =
        ["MATERIAL", "CHAMPION", "SKIN", "ETERNALS", "EMOTE", "WARDSKIN", "CHEST", "CURRENCY"];

    public IReadOnlyList<LootGroup> Grouped()
        => Items
            .GroupBy(item => item.Category ?? "OTHER")
            .Select(group => new LootGroup(
                group.Key,
                [.. group.OrderByDescending(i => i.Count).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)]))
            .OrderBy(group => Array.IndexOf(CategoryOrder, group.Category) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(group => group.Category, StringComparer.Ordinal)
            .ToList();

    /// <summary>Total of one currency, e.g. <c>CURRENCY_cosmetic</c> for orange essence.</summary>
    public int CurrencyTotal(string lootId)
        => Items.Where(i => string.Equals(i.LootId, lootId, StringComparison.OrdinalIgnoreCase))
            .Sum(i => i.Count);
}

public sealed record LootGroup(string Category, IReadOnlyList<LootItem> Items)
{
    /// <summary>Human-readable heading for the category.</summary>
    public string Title => Category switch
    {
        "MATERIAL" => "Materials",
        "CHAMPION" => "Champion shards",
        "SKIN" => "Skin shards",
        "ETERNALS" => "Eternals",
        "EMOTE" => "Emotes",
        "WARDSKIN" => "Ward skins",
        "CHEST" => "Chests and keys",
        "CURRENCY" => "Essence",
        "OTHER" => "Other",
        _ => Category,
    };

    public int TotalCount => Items.Sum(i => i.Count);
}
