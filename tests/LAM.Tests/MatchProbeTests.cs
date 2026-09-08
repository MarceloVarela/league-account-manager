using System;
using System.Linq;
using System.Text.Json;
using LAM.Core.Riot;
using Xunit;

namespace LAM.Tests;

/// <summary>
/// Guards the match-history parse.
///
/// Every field here has a specific way of going wrong, and two of them have already gone wrong
/// elsewhere in this codebase: the epoch unit, and a list nested one level deeper than expected.
/// </summary>
public class MatchProbeTests
{
    private static JsonDocument Games(string inner) => JsonDocument.Parse(
        "{\"games\":{\"games\":[" + inner + "]}}");

    private static string Game(int queue, bool win, long created = 1_700_000_000_000)
        => "{\"queueId\":" + queue + ",\"gameCreation\":" + created
           + ",\"participants\":[{\"stats\":{\"win\":" + (win ? "true" : "false") + "}}]}";

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reads_ranked_results_oldest_first()
    {
        // The client returns newest first; the chart reads left to right ending on the newest game,
        // so the order has to be reversed or every card shows its form backwards.
        // Deliberately NOT a palindrome. The first version of this test expected [true, false, true],
        // which reads the same in both directions — so it passed with the reversal removed and proved
        // nothing at all.
        using var document = Games(
            Game(420, true, 4_000_000_000_000) + "," +    // newest
            Game(420, true, 3_000_000_000_000) + "," +
            Game(420, false, 2_000_000_000_000) + "," +
            Game(420, false, 1_000_000_000_000));         // oldest

        var snapshot = ClientMatchProbe.Parse(document, Now);

        Assert.NotNull(snapshot);
        Assert.Equal([false, false, true, true], snapshot!.Recent.Select(r => r.Won));
        Assert.Equal(2, snapshot.Wins);
        Assert.Equal(2, snapshot.Losses);
    }

    [Fact]
    public void Ignores_every_queue_that_is_not_ranked()
    {
        // ARAM, normals and bot games come back in the same list. A form chart that counted them
        // would disagree with the rank it sits under.
        using var document = Games(
            Game(450, true) + "," +      // ARAM
            Game(420, false) + "," +     // solo/duo
            Game(400, true) + "," +      // draft normal
            Game(440, true));            // flex

        var snapshot = ClientMatchProbe.Parse(document, Now);

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.Recent.Count);
        Assert.Equal([420, 440], snapshot.Recent.Select(r => r.QueueId).Order());
    }

    [Fact]
    public void Keeps_only_the_last_ten()
    {
        var games = string.Join(",", Enumerable.Range(0, 25)
            .Select(i => Game(420, i % 2 == 0, 1_000_000_000_000 + i)));

        using var document = Games(games);

        var snapshot = ClientMatchProbe.Parse(document, Now);

        Assert.NotNull(snapshot);
        Assert.Equal(10, snapshot!.Recent.Count);
    }

    [Fact]
    public void Reads_game_creation_as_milliseconds()
    {
        // The collection endpoint reports SECONDS and this one reports MILLISECONDS. Reading this as
        // seconds put every date in 1970 once already; reading that one as millis put them in 2262.
        using var document = Games(Game(420, true, 1_700_000_000_000));

        var snapshot = ClientMatchProbe.Parse(document, Now);

        Assert.NotNull(snapshot);
        Assert.Equal(2023, snapshot!.LastPlayedUtc!.Value.Year);
    }

    [Fact]
    public void Last_played_counts_unranked_games_too()
    {
        // Dormancy is about whether the account is being PLAYED, not whether it is climbing. An
        // account grinding ARAM daily is not dormant.
        using var document = Games(
            Game(450, true, 3_000_000_000_000) + "," +
            Game(420, true, 1_000_000_000_000));

        var snapshot = ClientMatchProbe.Parse(document, Now);

        Assert.NotNull(snapshot);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(3_000_000_000_000), snapshot!.LastPlayedUtc);
        Assert.Single(snapshot.Recent);
    }

    [Fact]
    public void Accepts_a_bare_array_as_well_as_the_nested_shape()
    {
        using var document = JsonDocument.Parse("[" + Game(420, true) + "]");

        var snapshot = ClientMatchProbe.Parse(document, Now);

        Assert.NotNull(snapshot);
        Assert.Single(snapshot!.Recent);
    }

    [Fact]
    public void Reads_a_string_win_flag()
    {
        // Some client versions report "Win"/"Fail" rather than a boolean.
        using var document = Games(
            "{\"queueId\":420,\"gameCreation\":1700000000000,"
            + "\"participants\":[{\"stats\":{\"win\":\"Win\"}}]}");

        var snapshot = ClientMatchProbe.Parse(document, Now);

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.Recent.Single().Won);
    }

    [Fact]
    public void Returns_null_rather_than_an_empty_shell_when_there_is_nothing()
    {
        using var document = JsonDocument.Parse("{\"games\":{\"games\":[]}}");

        Assert.Null(ClientMatchProbe.Parse(document, Now));
        Assert.Null(ClientMatchProbe.Parse(null, Now));
    }

    [Fact]
    public void Survives_a_game_with_no_participants_or_no_win_flag()
    {
        // A remade or errored game can arrive without the stats block. It must be skipped, not throw
        // and lose the nine good games around it.
        using var document = Games(
            "{\"queueId\":420,\"gameCreation\":1700000000000}" + "," +
            "{\"queueId\":420,\"gameCreation\":1700000000001,\"participants\":[]}" + "," +
            Game(420, true));

        var snapshot = ClientMatchProbe.Parse(document, Now);

        Assert.NotNull(snapshot);
        Assert.Single(snapshot!.Recent);
    }
}
