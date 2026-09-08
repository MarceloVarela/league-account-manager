using System.Text.Json;

namespace LAM.Core.Riot;

/// <summary>
/// Reads the account's recent ranked results from the running League client.
///
/// This exists so the card's form chart can show real games. The ranked endpoint we already read
/// reports season TOTALS — 154W / 157L — which says nothing about how the last ten went, and drawing
/// a ten-bar chart from a season total would be inventing data.
///
/// It also produces the first honest "last played". Everything else in the app dates an account from
/// <c>LastUsedUtc</c>, which only records when this app last touched it — an account played daily
/// outside the manager still reads as untouched.
///
/// Served off the League lockfile: no Riot API key, no rate limit, and nothing leaves the machine.
/// Strictly read-only, like every other probe here.
/// </summary>
public sealed class ClientMatchProbe
{
    /// <summary>
    /// Twenty games, not ten.
    ///
    /// The chart wants the last ten RANKED games, and normals, ARAMs and bot games all come back in
    /// the same list — so the window has to be wider than the answer or a few ARAMs would leave the
    /// chart short.
    /// </summary>
    internal const string MatchesPath =
        "/lol-match-history/v1/products/lol/current-summoner/matches?begIndex=0&endIndex=19";

    /// <summary>Ranked solo/duo and flex. Everything else is noise for a form chart.</summary>
    private static readonly int[] RankedQueues = [420, 440];

    private readonly RiotPaths _paths;

    public ClientMatchProbe(RiotPaths paths) => _paths = paths;

    public async Task<MatchSnapshot?> TryReadAsync(CancellationToken cancellationToken)
    {
        var lockfile = Lockfile.Read(_paths.LeagueLockfile);
        if (lockfile is null) return null;

        using var client = new RiotLocalApiClient(lockfile);
        using var document = await client.GetJsonAsync(MatchesPath, cancellationToken);

        return Parse(document, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Turns the client's match list into a snapshot.
    ///
    /// Separate and internal so it can be tested against captured payloads without a running client.
    /// </summary>
    internal static MatchSnapshot? Parse(JsonDocument? document, DateTimeOffset nowUtc)
    {
        if (document is null) return null;

        var root = document.RootElement;

        // The list is nested at games.games, and has also been seen as a bare array.
        JsonElement list;

        if (root.ValueKind == JsonValueKind.Array)
        {
            list = root;
        }
        else if (root.ValueKind == JsonValueKind.Object
                 && root.TryGetProperty("games", out var outer)
                 && outer.ValueKind == JsonValueKind.Object
                 && outer.TryGetProperty("games", out var inner)
                 && inner.ValueKind == JsonValueKind.Array)
        {
            list = inner;
        }
        else
        {
            return null;
        }

        var results = new List<MatchResult>();
        DateTimeOffset? newest = null;

        foreach (var game in list.EnumerateArray())
        {
            if (game.ValueKind != JsonValueKind.Object) continue;

            var played = ReadPlayedAt(game);

            // Last played counts EVERY queue: an account grinding ARAM is not dormant.
            if (played is not null && (newest is null || played > newest)) newest = played;

            var queue = ReadInt(game, "queueId");
            if (queue is null || !RankedQueues.Contains(queue.Value)) continue;

            var won = ReadWin(game);
            if (won is null) continue;

            results.Add(new MatchResult
            {
                Won = won.Value,
                QueueId = queue.Value,
                PlayedUtc = played,
            });
        }

        if (results.Count == 0 && newest is null) return null;

        return new MatchSnapshot
        {
            CapturedUtc = nowUtc,
            LastPlayedUtc = newest,

            // Newest first from the client; the chart reads oldest to newest, left to right.
            Recent = [.. results.Take(10).Reverse()],
        };
    }

    /// <summary>
    /// Whether the local player won.
    ///
    /// The flag lives on the player's own participant entry, not on the game, because a game object
    /// describes both teams. participants[0] is the current summoner in this endpoint's shape.
    /// </summary>
    private static bool? ReadWin(JsonElement game)
    {
        if (!game.TryGetProperty("participants", out var participants)
            || participants.ValueKind != JsonValueKind.Array) return null;

        foreach (var participant in participants.EnumerateArray())
        {
            if (participant.ValueKind != JsonValueKind.Object) continue;
            if (!participant.TryGetProperty("stats", out var stats)) continue;

            if (stats.TryGetProperty("win", out var win))
            {
                return win.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => string.Equals(win.GetString(), "Win",
                        StringComparison.OrdinalIgnoreCase),
                    _ => null,
                };
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadPlayedAt(JsonElement game)
    {
        // gameCreation is epoch MILLISECONDS here — the same trap that made the collection dates
        // land in 1970 until they were read as seconds. The two endpoints genuinely differ.
        if (game.TryGetProperty("gameCreation", out var created)
            && created.ValueKind == JsonValueKind.Number
            && created.TryGetInt64(out var millis)
            && millis > 0)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(millis);
        }

        if (game.TryGetProperty("gameCreationDate", out var iso)
            && iso.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(iso.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static int? ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var number)
            ? number
            : null;
}

/// <summary>Recent ranked results, and when the account was genuinely last played.</summary>
public sealed class MatchSnapshot
{
    public DateTimeOffset CapturedUtc { get; set; }

    /// <summary>The newest game of ANY queue, which is what "last played" honestly means.</summary>
    public DateTimeOffset? LastPlayedUtc { get; set; }

    /// <summary>Up to ten ranked results, oldest first.</summary>
    public List<MatchResult> Recent { get; set; } = [];

    public bool IsEmpty => Recent.Count == 0;

    public int Wins => Recent.Count(r => r.Won);

    public int Losses => Recent.Count(r => !r.Won);
}

public sealed class MatchResult
{
    public bool Won { get; set; }

    public int QueueId { get; set; }

    public DateTimeOffset? PlayedUtc { get; set; }
}
