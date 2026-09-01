using System.Text.Json;
using LAM.Core.Model;

namespace LAM.Core.Riot;

/// <summary>
/// Learns who just logged in, by asking the League client itself.
///
/// This is the piece that means you never type a Riot ID. Once League is up it exposes
/// <c>/lol-summoner/v1/current-summoner</c> on its loopback API, which hands back the PUUID, the
/// current Riot ID, the level and the icon for whoever is signed in. No API key, no rate limit, and
/// the PUUID it returns is the one identifier that survives every future name change — which is
/// exactly what you want on file if an account ever has to be recovered.
/// </summary>
public sealed class LcuIdentityProbe
{
    private readonly RiotPaths _paths;

    public LcuIdentityProbe(RiotPaths paths) => _paths = paths;

    /// <summary>
    /// Waits for League to finish starting, then reads the signed-in summoner.
    ///
    /// Returns null rather than throwing when League never appears — the account still launched
    /// successfully, we simply could not enrich it, and that must not be reported as a failed login.
    /// </summary>
    public async Task<IdentityReading?> TryReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var lockfile = await Lockfile.WaitForAsync(_paths.LeagueLockfile, timeout, cancellationToken);
        if (lockfile is null) return null;

        using var client = new RiotLocalApiClient(lockfile);

        // The lockfile lands before the summoner endpoint is ready to answer, so poll rather than
        // asking once and giving up.
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var document = await client.GetJsonAsync("/lol-summoner/v1/current-summoner", cancellationToken);
            var reading = Parse(document);
            if (reading is not null) return reading;

            await Task.Delay(1000, cancellationToken);
        }

        return null;
    }

    private static IdentityReading? Parse(JsonDocument? document)
    {
        if (document is null) return null;

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        var puuid = ReadString(root, "puuid");

        // Before login completes the endpoint answers with a skeleton object whose puuid is blank;
        // treating that as a reading would record an empty identity over a good one.
        if (string.IsNullOrWhiteSpace(puuid)) return null;

        return new IdentityReading
        {
            Puuid = puuid,
            GameName = ReadString(root, "gameName") ?? ReadString(root, "displayName"),
            TagLine = ReadString(root, "tagLine"),
            SummonerId = ReadNumberAsString(root, "summonerId"),
            AccountId = ReadNumberAsString(root, "accountId"),
            SummonerLevel = ReadInt(root, "summonerLevel"),
            ProfileIconId = ReadInt(root, "profileIconId"),
        };
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

    /// <summary>Summoner and account ids are large numbers; keep them as text to avoid precision loss.</summary>
    private static string? ReadNumberAsString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null,
        };
    }

    /// <summary>Reads the region the client is actually running under.</summary>
    public async Task<string?> TryReadPlatformAsync(CancellationToken cancellationToken)
    {
        var lockfile = Lockfile.Read(_paths.RiotClientLockfile);
        if (lockfile is null) return null;

        using var client = new RiotLocalApiClient(lockfile);
        using var document = await client.GetJsonAsync("/riotclient/region-locale", cancellationToken);
        if (document is null) return null;

        return ReadString(document.RootElement, "webRegion")
               ?? ReadString(document.RootElement, "region");
    }
}

/// <summary>What the local client told us about the signed-in account.</summary>
public sealed record IdentityReading
{
    public required string Puuid { get; init; }
    public string? GameName { get; init; }
    public string? TagLine { get; init; }
    public string? SummonerId { get; init; }
    public string? AccountId { get; init; }
    public int? SummonerLevel { get; init; }
    public int? ProfileIconId { get; init; }

    /// <summary>
    /// Folds the reading into an account's stored identity, appending to the name history only when
    /// the Riot ID actually changed.
    /// </summary>
    public bool ApplyTo(IdentitySnapshot snapshot, DateTimeOffset nowUtc)
    {
        var renamed = snapshot.RecordName(GameName, TagLine, nowUtc);

        snapshot.RiotAccountId = Puuid;
        if (SummonerId is not null) snapshot.SummonerId = SummonerId;
        if (AccountId is not null) snapshot.AccountId = AccountId;
        if (SummonerLevel is not null) snapshot.SummonerLevel = SummonerLevel;
        if (ProfileIconId is not null) snapshot.ProfileIconId = ProfileIconId;
        snapshot.LastRefreshedUtc = nowUtc;

        return renamed;
    }
}
