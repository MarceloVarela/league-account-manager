using LAM.Core.Login;
using Xunit;

namespace LAM.Tests;

/// <summary>
/// What "the game is starting" means, and why it is not the same as "the game is running".
///
/// The distinction caused a real failure. The Play button was clicked correctly, League took longer
/// than the sixty seconds allowed to appear, and the app reported the click as failed. That verdict
/// then fed the identity lookup, which used its short timeout, gave up, and left the account with no
/// Riot ID or level — all from a launch that had actually worked.
/// </summary>
public sealed class GameStartOutcomeTests
{
    [Fact]
    public void A_delivered_click_counts_as_the_game_coming()
    {
        // The whole point: League may still be minutes from ready, and that is fine. Waiting for a
        // first-boot patch is the identity probe's problem, not a reason to call the click a failure.
        Assert.True(GameStartOutcome.Clicked.GameIsComing());
    }

    [Fact]
    public void An_already_running_client_counts_too()
        => Assert.True(GameStartOutcome.AlreadyRunning.GameIsComing());

    [Theory]
    [InlineData(GameStartOutcome.Disabled)]
    [InlineData(GameStartOutcome.NotStarted)]
    public void Nothing_else_does(GameStartOutcome outcome)
    {
        // When League genuinely is not coming, the lockfile can never appear and a long wait for it
        // is pure dead time on every sign-in.
        Assert.False(outcome.GameIsComing());
    }

    [Fact]
    public void Every_outcome_is_classified()
    {
        // Guards against a new outcome being added and silently defaulting to "not coming", which
        // would quietly reintroduce the short-timeout bug.
        foreach (var outcome in Enum.GetValues<GameStartOutcome>())
        {
            var expected = outcome is GameStartOutcome.Clicked or GameStartOutcome.AlreadyRunning;
            Assert.Equal(expected, outcome.GameIsComing());
        }
    }
}
