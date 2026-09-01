using System.Net;
using System.Text.Json;
using LAM.Core.Model;

namespace LAM.Core.Riot;

/// <summary>
/// Reads public account data from Riot's developer API: rank, level, last activity, and an estimate
/// of how old the account is.
///
/// Everything here is optional enrichment. The app signs accounts in perfectly well without an API
/// key, so every method degrades to "leave the snapshot alone" rather than failing a refresh.
/// </summary>
public sealed class RiotApiClient : IDisposable
{
    /// <summary>
    /// match-v5 does not reach back indefinitely. Matches before roughly mid-2021 are simply not
    /// served, so an "oldest match" at that boundary tells us the account is *at least* that old and
    /// nothing more — which is why the estimate is reported as a lower bound rather than a birthday.
    /// </summary>
    public static readonly DateTimeOffset MatchHistoryCoverageStart =
        new(2021, 6, 16, 0, 0, 0, TimeSpan.Zero);

    private readonly HttpClient _http;
    private readonly SecretText _apiKey;
    private readonly TokenBucket _limiter;
    private readonly Login.LoginTrace _trace;

    /// <summary>Set once any request succeeds, which proves the key itself is being accepted.</summary>
    private bool _keyHasWorked;

    public RiotApiClient(SecretText apiKey, HttpClient? http = null, Login.LoginTrace? trace = null)
    {
        _apiKey = apiKey;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _limiter = new TokenBucket();
        _trace = trace ?? Login.LoginTrace.Null;

        // .NET sends no User-Agent by default. Riot's edge accepts that today — verified — but
        // identifying the client is basic manners for a third-party API consumer and costs nothing.
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "LeagueAccountManager/1.0");
    }

    /// <summary>
    /// Checks the key against a trivial endpoint and reports exactly what Riot said.
    ///
    /// Exists because "the key was rejected" on its own is a dead end: the key may be expired, or
    /// typed short, or the request may be at fault, and the three need completely different actions.
    /// </summary>
    public async Task<KeyTestResult> TestKeyAsync(string region, CancellationToken cancellationToken)
    {
        var platform = RiotRegions.PlatformFor(region) ?? "br1";
        var url = "https://" + platform + ".api.riotgames.com/lol/status/v4/platform-data";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Riot-Token", _apiKey.Value);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            var status = (int)response.StatusCode;

            return status switch
            {
                200 => new KeyTestResult(true, status, "Key works. Rank and level lookups are available."),
                401 => new KeyTestResult(false, status,
                    "Riot returned 401 Unauthorized: the key is not being accepted at all. Check it was " +
                    "pasted in full — it should start with RGAPI- and be " + ExpectedKeyLength + " characters."),
                403 => new KeyTestResult(false, status,
                    "Riot returned 403 Forbidden: the key is recognised but no longer valid. Development " +
                    "keys expire every 24 hours; regenerate it, or register a Personal key."),
                429 => new KeyTestResult(false, status, "Riot returned 429: rate limited. Wait a minute and retry."),
                _ => new KeyTestResult(false, status, "Riot returned HTTP " + status + "."),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new KeyTestResult(false, 0, "Could not reach Riot: " + ex.Message);
        }
    }

    /// <summary>Length of a well-formed key, used only to spot a truncated paste.</summary>
    private const int ExpectedKeyLength = 42;

    /// <summary>
    /// Refreshes everything we can learn for one account. Returns what changed, so the UI can say
    /// "renamed" rather than silently swapping a name.
    /// </summary>
    public async Task<RefreshOutcome> RefreshAsync(
        AccountEntry account, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var platform = RiotRegions.PlatformFor(account.Region);
        var regional = RiotRegions.RegionalFor(account.Region);

        if (platform is null || regional is null)
            return RefreshOutcome.Skipped("Region \"" + account.Region + "\" is not one I know how to route.");

        // Always resolve from the Riot ID rather than reusing a stored value.
        //
        // Riot encrypts the API PUUID per API key, so a cached one stops matching the moment a key is
        // regenerated. Worse, the durable account id the client gives us is a 36-character GUID,
        // which this endpoint answers with 400 — that mismatch is what made every rank read blank and
        // the card say "Unranked".
        if (string.IsNullOrWhiteSpace(account.Identity.GameName) ||
            string.IsNullOrWhiteSpace(account.Identity.TagLine))
        {
            return RefreshOutcome.Skipped(
                "Nothing to look up yet — sign in once and the client will fill in the Riot ID.");
        }

        var puuid = await ResolvePuuidAsync(
            regional, account.Identity.GameName!, account.Identity.TagLine!, cancellationToken);

        if (puuid is null)
            return RefreshOutcome.Failed(
                "Riot could not find " + account.Identity.GameName + "#" + account.Identity.TagLine + ".");

        // Kept only as a hint for the current key; never relied on for the next refresh.
        account.Identity.ApiPuuid = puuid;

        var renamed = false;

        var riotId = await GetRiotIdAsync(regional, puuid, cancellationToken);
        if (riotId is not null)
            renamed = account.Identity.RecordName(riotId.Value.GameName, riotId.Value.TagLine, nowUtc);

        var summoner = await GetSummonerAsync(platform, puuid, cancellationToken);
        if (summoner is not null)
        {
            account.Identity.SummonerLevel = summoner.Level;
            account.Identity.ProfileIconId = summoner.ProfileIconId;
            account.Identity.SummonerId = summoner.SummonerId ?? account.Identity.SummonerId;
            account.Identity.LastActivityUtc = summoner.RevisionDateUtc;
        }

        var ranks = await GetRanksAsync(platform, puuid, cancellationToken);
        account.Identity.SoloRank = ranks.Solo ?? account.Identity.SoloRank;
        account.Identity.FlexRank = ranks.Flex ?? account.Identity.FlexRank;

        account.Identity.Platform = platform;
        account.Identity.LastRefreshedUtc = nowUtc;

        return RefreshOutcome.Ok(renamed);
    }

    /// <summary>
    /// Estimates how old the account is by binary-searching match history for its oldest entry.
    ///
    /// Separate from <see cref="RefreshAsync"/> and not run automatically, because it costs a dozen
    /// or so requests per account and the answer barely moves. It is worth running once per account,
    /// for the recovery dossier.
    /// </summary>
    public async Task<AgeEstimate?> EstimateAgeAsync(
        AccountEntry account, CancellationToken cancellationToken)
    {
        var regional = RiotRegions.RegionalFor(account.Region);
        if (regional is null) return null;

        // Same rule as RefreshAsync: resolve for this key rather than trusting a stored value.
        if (string.IsNullOrWhiteSpace(account.Identity.GameName) ||
            string.IsNullOrWhiteSpace(account.Identity.TagLine))
        {
            return null;
        }

        var puuid = await ResolvePuuidAsync(
            regional, account.Identity.GameName!, account.Identity.TagLine!, cancellationToken);
        if (string.IsNullOrWhiteSpace(puuid)) return null;

        // Find the largest offset that still returns a match: that is the oldest one on record.
        var low = 0;
        var high = 1;

        while (await HasMatchAtAsync(regional, puuid, high, cancellationToken))
        {
            low = high;
            high *= 2;
            if (high > 100_000) break;   // a hard stop; nobody has this many games
        }

        while (low + 1 < high)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var middle = low + (high - low) / 2;
            if (await HasMatchAtAsync(regional, puuid, middle, cancellationToken)) low = middle;
            else high = middle;
        }

        var oldest = await GetMatchIdAtAsync(regional, puuid, low, cancellationToken);
        if (oldest is null) return null;

        var created = await GetMatchCreationAsync(regional, oldest, cancellationToken);
        if (created is null) return null;

        // If the oldest match we can see sits right at the edge of what the API serves, the account
        // is older than this and we must not imply otherwise.
        var atCoverageEdge = created.Value < MatchHistoryCoverageStart.AddMonths(3);

        return new AgeEstimate(oldest, created.Value, atCoverageEdge);
    }

    // ---- endpoints ---------------------------------------------------------

    private async Task<string?> ResolvePuuidAsync(
        string regional, string gameName, string tagLine, CancellationToken cancellationToken)
    {
        var path = "https://" + regional + ".api.riotgames.com/riot/account/v1/accounts/by-riot-id/"
                   + Uri.EscapeDataString(gameName) + "/" + Uri.EscapeDataString(tagLine);

        using var document = await GetAsync(path, cancellationToken);
        return document is null ? null : ReadString(document.RootElement, "puuid");
    }

    private async Task<(string? GameName, string? TagLine)?> GetRiotIdAsync(
        string regional, string puuid, CancellationToken cancellationToken)
    {
        var path = "https://" + regional + ".api.riotgames.com/riot/account/v1/accounts/by-puuid/" + puuid;
        using var document = await GetAsync(path, cancellationToken);
        if (document is null) return null;

        return (ReadString(document.RootElement, "gameName"), ReadString(document.RootElement, "tagLine"));
    }

    private sealed record SummonerReading(int? Level, int? ProfileIconId, string? SummonerId, DateTimeOffset? RevisionDateUtc);

    private async Task<SummonerReading?> GetSummonerAsync(
        string platform, string puuid, CancellationToken cancellationToken)
    {
        var path = "https://" + platform + ".api.riotgames.com/lol/summoner/v4/summoners/by-puuid/" + puuid;
        using var document = await GetAsync(path, cancellationToken);
        if (document is null) return null;

        var root = document.RootElement;
        return new SummonerReading(
            ReadInt(root, "summonerLevel"),
            ReadInt(root, "profileIconId"),
            ReadString(root, "id"),
            ReadEpochMilliseconds(root, "revisionDate"));
    }

    private async Task<(RankInfo? Solo, RankInfo? Flex)> GetRanksAsync(
        string platform, string puuid, CancellationToken cancellationToken)
    {
        // by-puuid only. The by-summoner fallback that used to live here could never work and was
        // actively harmful: it needs an *encrypted* summoner id, whereas the one we hold comes from
        // the League client and is a plain number (1395378). Riot answers that with 403, which this
        // class then reported as "your API key was rejected" — sending two rounds of debugging after
        // a key that was perfectly valid. summoner-v4 no longer returns an encrypted id at all, so
        // there is nothing to fall back to.
        var document = await GetAsync(
            "https://" + platform + ".api.riotgames.com/lol/league/v4/entries/by-puuid/" + puuid,
            cancellationToken);

        if (document is null) return (null, null);

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) return (null, null);

            RankInfo? solo = null, flex = null;
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var queue = ReadString(entry, "queueType");
                var rank = new RankInfo
                {
                    Tier = ReadString(entry, "tier") ?? "UNRANKED",
                    Division = ReadString(entry, "rank"),
                    LeaguePoints = ReadInt(entry, "leaguePoints") ?? 0,
                    Wins = ReadInt(entry, "wins") ?? 0,
                    Losses = ReadInt(entry, "losses") ?? 0,
                };

                if (queue == "RANKED_SOLO_5x5") solo = rank;
                else if (queue == "RANKED_FLEX_SR") flex = rank;
            }

            return (solo, flex);
        }
    }

    private async Task<bool> HasMatchAtAsync(
        string regional, string puuid, int start, CancellationToken cancellationToken)
        => await GetMatchIdAtAsync(regional, puuid, start, cancellationToken) is not null;

    private async Task<string?> GetMatchIdAtAsync(
        string regional, string puuid, int start, CancellationToken cancellationToken)
    {
        var path = "https://" + regional + ".api.riotgames.com/lol/match/v5/matches/by-puuid/"
                   + puuid + "/ids?start=" + start + "&count=1";

        using var document = await GetAsync(path, cancellationToken);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array) return null;

        var first = document.RootElement.EnumerateArray().FirstOrDefault();
        return first.ValueKind == JsonValueKind.String ? first.GetString() : null;
    }

    private async Task<DateTimeOffset?> GetMatchCreationAsync(
        string regional, string matchId, CancellationToken cancellationToken)
    {
        var path = "https://" + regional + ".api.riotgames.com/lol/match/v5/matches/" + matchId;
        using var document = await GetAsync(path, cancellationToken);
        if (document is null) return null;

        return document.RootElement.TryGetProperty("info", out var info)
            ? ReadEpochMilliseconds(info, "gameCreation")
            : null;
    }

    // ---- transport ---------------------------------------------------------

    /// <summary>
    /// Issues a rate-limited GET. A 404 is a normal answer here (unranked, no matches, unknown
    /// account), so it comes back as null rather than an exception.
    /// </summary>
    private async Task<JsonDocument?> GetAsync(string url, CancellationToken cancellationToken)
    {
        await _limiter.WaitAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Riot-Token", _apiKey.Value);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
                _limiter.PauseFor(retryAfter);
                throw new RiotApiRateLimitedException(retryAfter);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _trace.Write("Riot API " + (int)response.StatusCode + " on " + Describe(url));

                // A 403 means "not allowed" — which covers an expired key, but equally a well-formed
                // request carrying an identifier the endpoint will not accept. Once any call has
                // succeeded the key is demonstrably fine, so blaming it would be wrong; that is
                // exactly the mistake that hid a bad endpoint behind a key error.
                if (_keyHasWorked && response.StatusCode == HttpStatusCode.Forbidden)
                    return null;

                throw new RiotApiKeyRejectedException((int)response.StatusCode, Describe(url), _apiKey.Value);
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                // Riot rejects the identifier itself. Silently returning "no data" here is how a
                // wrong PUUID surfaced as a blank "Unranked" instead of an error worth acting on.
                _trace.Write("Riot API 400 (bad identifier) on " + Describe(url));
                throw new RiotApiBadRequestException(Describe(url));
            }

            if (!response.IsSuccessStatusCode)
            {
                _trace.Write("Riot API " + (int)response.StatusCode + " on " + Describe(url));
                return null;
            }

            _keyHasWorked = true;

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// The endpoint path, with the PUUID shortened. Enough to identify which call failed, without
    /// writing a full account identifier into a log file.
    /// </summary>
    private static string Describe(string url)
    {
        var uri = new Uri(url);
        var path = uri.AbsolutePath;

        var lastSlash = path.LastIndexOf('/');
        if (lastSlash > 0 && path.Length - lastSlash > 20)
            path = path[..(lastSlash + 9)] + "…";

        return uri.Host + path;
    }

    private static string? ReadString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static DateTimeOffset? ReadEpochMilliseconds(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        if (!value.TryGetInt64(out var epoch)) return null;

        // Riot has shipped this field in seconds and in milliseconds at different times. A value
        // small enough to be seconds is treated as such rather than landing in 1970.
        return epoch < 100_000_000_000L
            ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : DateTimeOffset.FromUnixTimeMilliseconds(epoch);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// A lower bound on the account's age, derived from the oldest match Riot will still serve.
/// </summary>
public sealed record AgeEstimate(string OldestMatchId, DateTimeOffset OldestMatchUtc, bool LowerBoundOnly)
{
    public string Describe() => LowerBoundOnly
        ? "At least as old as " + OldestMatchUtc.ToString("MMMM yyyy") +
          " (Riot's match history does not go back further, so the account may be much older)"
        : "First recorded game " + OldestMatchUtc.ToString("d MMMM yyyy");
}

public sealed record RefreshOutcome(bool Success, bool Renamed, string? Message)
{
    public static RefreshOutcome Ok(bool renamed) => new(true, renamed, null);
    public static RefreshOutcome Skipped(string why) => new(false, false, why);
    public static RefreshOutcome Failed(string why) => new(false, false, why);
}

public sealed class RiotApiKeyRejectedException : Exception
{
    public RiotApiKeyRejectedException(int status, string endpoint, string key)
        : base(Describe(status, endpoint, key))
    {
        Status = status;
        Endpoint = endpoint;
    }

    public int Status { get; }
    public string Endpoint { get; }

    private static string Describe(int status, string endpoint, string key)
    {
        // The key's shape is included because a truncated or half-pasted key is a common cause and
        // is otherwise invisible. Only the length and the last four characters — never the key.
        var shape = string.IsNullOrEmpty(key)
            ? "no key is stored"
            : "stored key is " + key.Length + " characters, ending \"" +
              key[Math.Max(0, key.Length - 4)..] + "\"" +
              (key.StartsWith("RGAPI-", StringComparison.Ordinal) ? "" : ", and does NOT start with RGAPI-");

        var cause = status == 401
            ? "401 Unauthorized — the key is not being accepted at all, which usually means it was " +
              "pasted incompletely."
            : "403 Forbidden — the key is recognised but no longer valid. Development keys expire " +
              "every 24 hours; register a Personal key at developer.riotgames.com to stop that.";

        return "Riot rejected the API key." + Environment.NewLine + Environment.NewLine
               + cause + Environment.NewLine + Environment.NewLine
               + "Call: " + endpoint + Environment.NewLine
               + shape + Environment.NewLine + Environment.NewLine
               + "Use \"Test key\" in Settings to check the key on its own.";
    }
}

public sealed class RiotApiRateLimitedException : Exception
{
    public RiotApiRateLimitedException(TimeSpan retryAfter)
        : base("Riot is rate limiting requests. Try again in " + (int)retryAfter.TotalSeconds + " seconds.")
        => RetryAfter = retryAfter;

    public TimeSpan RetryAfter { get; }
}

/// <summary>The outcome of checking an API key on its own, in words a person can act on.</summary>
public sealed record KeyTestResult(bool Ok, int Status, string Message);

/// <summary>Riot refused the identifier we sent, rather than the key.</summary>
public sealed class RiotApiBadRequestException : Exception
{
    public RiotApiBadRequestException(string endpoint)
        : base("Riot rejected the identifier sent to " + endpoint + ". This is a bug in the app, not " +
               "a problem with your key or your account — please report it with the sign-in log.")
        => Endpoint = endpoint;

    public string Endpoint { get; }
}
