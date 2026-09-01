namespace LAM.Core.Model;

/// <summary>
/// What the app learns about an account on its own — from the local League client immediately after
/// a successful login, and from the Riot API on refresh. You type none of this.
/// </summary>
public sealed class IdentitySnapshot
{
    /// <summary>
    /// The account's permanent Riot id — the RSO subject, a GUID such as
    /// <c>11111111-2222-3333-4444-555555555555</c>.
    ///
    /// This is the identifier worth keeping. It survives every Riot ID change, it is what the client
    /// and the id_token expose, and it is what a support ticket can be traced by. Captured locally;
    /// no API key involved.
    ///
    /// It is emphatically **not** the value Riot's public API wants — see <see cref="ApiPuuid"/>.
    /// Conflating the two is what made every rank lookup return 400 and the card read "Unranked".
    /// </summary>
    public string? RiotAccountId { get; set; }

    /// <summary>
    /// The PUUID for Riot's public API: a long opaque string, roughly 78 characters.
    ///
    /// Riot encrypts this **per API key**, so it is not a durable property of the account — the same
    /// account yields a different value under a different key. Cached only as a hint; every refresh
    /// re-resolves it from the Riot ID, because a stored one goes silently wrong the moment a key is
    /// regenerated.
    /// </summary>
    public string? ApiPuuid { get; set; }

    /// <summary>
    /// Legacy name kept so existing vaults keep loading. Old files stored the client's GUID here,
    /// which is <see cref="RiotAccountId"/>, so that is where it is routed.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("Puuid")]
    public string? LegacyPuuid
    {
        get => null;   // never written again; the value now lives in RiotAccountId
        set { if (!string.IsNullOrWhiteSpace(value)) RiotAccountId ??= value; }
    }

    public string? GameName { get; set; }
    public string? TagLine { get; set; }

    public string? SummonerId { get; set; }
    public string? AccountId { get; set; }

    public int? SummonerLevel { get; set; }
    public int? ProfileIconId { get; set; }

    /// <summary>Platform routing value, e.g. "br1", "na1", "euw1".</summary>
    public string? Platform { get; set; }

    public RankInfo? SoloRank { get; set; }
    public RankInfo? FlexRank { get; set; }

    /// <summary>summoner-v4's revisionDate — last time the account did anything. Not a creation date.</summary>
    public DateTimeOffset? LastActivityUtc { get; set; }

    /// <summary>
    /// Oldest match we could find, used as a *lower bound* on the account's age. match-v5 only
    /// reaches back to roughly 2021, so for an older account this proves "at least this old" and
    /// nothing more. Never present it as a creation date.
    /// </summary>
    public string? OldestKnownMatchId { get; set; }
    public DateTimeOffset? OldestKnownMatchUtc { get; set; }

    /// <summary>True when the oldest match sits at the edge of API coverage, i.e. the real account
    /// is probably older than <see cref="OldestKnownMatchUtc"/> suggests.</summary>
    public bool AgeIsLowerBoundOnly { get; set; }

    /// <summary>Every Riot ID this PUUID has been seen under, oldest first.</summary>
    public List<NameHistoryEntry> NameHistory { get; set; } = [];

    public DateTimeOffset? LastRefreshedUtc { get; set; }

    /// <summary>
    /// What the client last reported about this account — email mask, MFA, phone, region history.
    ///
    /// Kept separate from the typed recovery fields on purpose. These are observations of a session;
    /// the recovery fields are what you asserted about the account. Storing them apart is what lets
    /// the editor show both and flag a disagreement instead of silently picking one.
    /// </summary>
    public Riot.ClientAccountFacts? Observed { get; set; }

    /// <summary>
    /// Rank, wallet and collection as the client last reported them.
    ///
    /// A snapshot from the last sign-in rather than a live reading — the client can only be asked
    /// about the account it is signed into. Stored whole so the UI can say when it was taken.
    /// </summary>
    public Riot.ClientStats? ClientStats { get; set; }

    /// <summary>
    /// Loot as the client last reported it — shards, essence, keys and chests.
    ///
    /// Stored whole rather than as counts because the individual shards are the interesting part:
    /// which skins are one disenchant away, and which of them expire.
    /// </summary>
    public Riot.LootSnapshot? Loot { get; set; }

    /// <summary>Skins this account owns, by id. Names and art come from the shared catalogue.</summary>
    public int[] OwnedSkinIds { get; set; } = [];

    /// <summary>Champions this account owns, by id.</summary>
    public int[] OwnedChampionIds { get; set; } = [];

    /// <summary>
    /// When each owned skin was acquired, as seconds since the epoch.
    ///
    /// Seconds, not milliseconds - the catalogue reports this field in seconds while the champions
    /// endpoint reports its own purchase dates in milliseconds, and reading one as the other lands
    /// every skin in January 1970.
    ///
    /// Stored as a plain id-to-epoch map because that is the compact form: a full collection is a
    /// few tens of kilobytes, where dates as text would be several times that.
    /// </summary>
    public Dictionary<int, int> SkinAcquiredEpochSeconds { get; set; } = [];

    /// <summary>When a skin was acquired, if that has been captured.</summary>
    public DateTimeOffset? SkinAcquiredUtc(int skinId)
        => SkinAcquiredEpochSeconds.TryGetValue(skinId, out var epoch) && epoch > 0
            ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : null;

    /// <summary>
    /// Records the current Riot ID, appending to the history only when it actually changed.
    /// Returns true if this was a new name.
    /// </summary>
    public bool RecordName(string? gameName, string? tagLine, DateTimeOffset seenUtc)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return false;

        GameName = gameName;
        TagLine = tagLine;

        var last = NameHistory.Count > 0 ? NameHistory[^1] : null;
        if (last is not null && last.GameName == gameName && last.TagLine == tagLine)
            return false;

        NameHistory.Add(new NameHistoryEntry
        {
            GameName = gameName,
            TagLine = tagLine,
            FirstSeenUtc = seenUtc,
        });
        return true;
    }
}

public sealed class NameHistoryEntry
{
    public string GameName { get; set; } = string.Empty;
    public string? TagLine { get; set; }
    public DateTimeOffset FirstSeenUtc { get; set; }

    public override string ToString() => string.IsNullOrEmpty(TagLine) ? GameName : $"{GameName}#{TagLine}";
}

public sealed class RankInfo
{
    public string Tier { get; set; } = "UNRANKED";
    public string? Division { get; set; }
    public int LeaguePoints { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }

    /// <summary>
    /// Placement games still to play. Only the client reports this; the public API does not, and it
    /// is the difference between "unranked" and "nearly ranked".
    /// </summary>
    public int PlacementsRemaining { get; set; }

    public bool IsRanked => !string.Equals(Tier, "UNRANKED", StringComparison.OrdinalIgnoreCase);

    public int Games => Wins + Losses;

    /// <summary>Win rate as a percentage, or null with too few games for it to mean anything.</summary>
    public int? WinRate => Games >= 5 ? (int)Math.Round(100.0 * Wins / Games) : null;

    public override string ToString()
    {
        if (!IsRanked) return "Unranked";
        var tier = char.ToUpperInvariant(Tier[0]) + Tier[1..].ToLowerInvariant();
        var name = string.IsNullOrEmpty(Division) ? tier : tier + " " + Division;

        return PlacementsRemaining > 0
            ? name + " — " + PlacementsRemaining + " placement" + (PlacementsRemaining == 1 ? "" : "s") + " left"
            : name + " — " + LeaguePoints + " LP";
    }
}
