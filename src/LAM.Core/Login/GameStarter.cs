using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using LAM.Core.Riot;

namespace LAM.Core.Login;

/// <summary>What happened when we tried to start the game.</summary>
public enum GameStartOutcome
{
    /// <summary>Turned off in settings, so nothing was attempted.</summary>
    Disabled,

    /// <summary>League was already running and was left alone.</summary>
    AlreadyRunning,

    /// <summary>The Play button was clicked and the click was seen to take effect.</summary>
    Clicked,

    /// <summary>The Play button was never found, or clicking it changed nothing.</summary>
    NotStarted,
}

public static class GameStartOutcomeExtensions
{
    /// <summary>
    /// Whether League should be expected to appear, and therefore whether it is worth waiting for
    /// its client API.
    ///
    /// <see cref="GameStartOutcome.Clicked"/> counts even though the game may still be minutes from
    /// being ready — treating "clicked" as failure is what previously cut the identity lookup short
    /// on a launch that was working perfectly well.
    /// </summary>
    public static bool GameIsComing(this GameStartOutcome outcome)
        => outcome is GameStartOutcome.Clicked or GameStartOutcome.AlreadyRunning;
}

/// <summary>
/// Presses Play on the Riot Client home page once an account is signed in.
///
/// Beyond the convenience, this is what makes the rest of the sign-in work: the League client only
/// exists after Play is pressed, and <see cref="LcuIdentityProbe"/> reads the PUUID, Riot ID and
/// level from it.
///
/// Confirming the click is harder than it sounds, and both obvious signals have been wrong once:
///
///  * Waiting for the <c>LeagueClient</c> process reported failure on a click that had worked, because
///    a first boot can patch for longer than any sensible timeout.
///  * Watching for the Play button to disappear reported success 0.8 seconds after a click that did
///    nothing at all, because a repainting page reads as "button not found".
///
/// So the process is the signal, with a generous budget spread across retries — and "the button is
/// genuinely gone" only counts as success *after* a click has been sent, never before.
/// </summary>
public sealed class GameStarter
{
    private readonly RiotProcessManager _processes;
    private readonly LoginUiProfile _profile;
    private readonly LoginTrace _trace;

    public GameStarter(RiotProcessManager processes, LoginUiProfile profile, LoginTrace? trace = null)
    {
        _processes = processes;
        _profile = profile;
        _trace = trace ?? LoginTrace.Null;
    }

    public async Task<GameStartOutcome> TryStartGameAsync(
        LoginContext context, CancellationToken cancellationToken)
    {
        if (!context.Settings.LaunchGameAfterSignIn)
        {
            _trace.Write("not starting the game (turned off in settings)");
            return GameStartOutcome.Disabled;
        }

        if (_processes.IsGameInProgress() || IsLeagueClientRunning())
        {
            _trace.Write("League is already running; leaving it alone");
            return GameStartOutcome.AlreadyRunning;
        }

        context.Report(LoginStage.WaitingForSignIn, "Starting League…");

        using var inspector = new RiotWindowInspector(_profile);

        // Generous, because a first sign-in often patches before the home page settles and the Play
        // button does not exist until it does.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(150);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var window = await inspector.WaitForClientWindowAsync(TimeSpan.FromSeconds(5), cancellationToken);
            if (window is not null && await TryPressPlayAsync(window, cancellationToken))
                return GameStartOutcome.Clicked;

            await Task.Delay(1500, cancellationToken);
        }

        // Not a failure of the sign-in: the account is signed in regardless.
        _trace.Write("could not find the Play button; signed in but the game was not started");
        return GameStartOutcome.NotStarted;
    }

    /// <summary>
    /// Clicks Play and confirms the click had an effect, retrying with a real mouse click if the
    /// invoke pattern does nothing.
    ///
    /// That fallback is not speculative. The "Stay signed in" checkbox on the login page behaves the
    /// same way: the automation pattern reports success and leaves the control untouched, and only a
    /// physical click works.
    /// </summary>
    private async Task<bool> TryPressPlayAsync(Window window, CancellationToken cancellationToken)
    {
        // A real mouse click first. The invoke pattern reports success and does nothing on this
        // client — the "Stay signed in" checkbox behaved identically — and an invoke that silently
        // no-ops is indistinguishable from one that worked until League fails to appear.
        var attempts = new[]
        {
            (UseInvoke: false, Wait: TimeSpan.FromSeconds(15)),
            (UseInvoke: true, Wait: TimeSpan.FromSeconds(20)),
            (UseInvoke: false, Wait: TimeSpan.FromSeconds(30)),
        };

        for (var attempt = 0; attempt < attempts.Length; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lookup = FindPlayButton(window);
            if (lookup.Button is null)
            {
                // Unreadable is not the same as absent. A page mid-repaint is worth waiting for.
                if (!lookup.Readable)
                {
                    await Task.Delay(1000, cancellationToken);
                    continue;
                }

                // Genuinely gone. Before any click that means there is nothing to press; after one it
                // means the page moved on, which is the client switching to its launching state — and
                // calling that a failure is what previously cut the identity lookup short on a launch
                // that had worked.
                if (attempt == 0) return false;

                _trace.Write("Play is gone from the page after clicking; taking that as launched");
                return true;
            }

            if (attempt == 0)
                _trace.Write("pressing Play: " + RiotWindowInspector.DescribeButton(lookup.Button).Describe());

            if (!TryActivate(lookup.Button, attempts[attempt].UseInvoke)) return false;

            if (await LeagueStartedAsync(attempts[attempt].Wait, cancellationToken))
            {
                _trace.Write("Play worked (attempt " + (attempt + 1) + "); the League client is up");
                return true;
            }

            _trace.Write("Play click produced no League client after "
                         + (int)attempts[attempt].Wait.TotalSeconds + "s (attempt " + (attempt + 1) + ")");
        }

        return false;
    }

    /// <summary>
    /// Looks for the Play button, distinguishing "not there" from "could not read the page".
    ///
    /// The distinction is the whole point. An earlier version returned null for both, and the caller
    /// read null as "the button vanished, so the click worked" — so a page that was merely repainting
    /// counted as success, 0.8 seconds after the click, and the retry with a real click never ran
    /// while League never started.
    /// </summary>
    private (AutomationElement? Button, bool Readable) FindPlayButton(Window window)
    {
        try
        {
            var buttons = window
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .ToList();

            // No buttons at all means the tree is not ready, not that the page has none.
            if (buttons.Count == 0) return (null, false);

            var index = PlayButtonChooser.Choose(buttons.Select(RiotWindowInspector.DescribeButton).ToList());
            return (index is null ? null : buttons[index.Value], true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, false);
        }
    }

    private bool TryActivate(AutomationElement element, bool useInvoke)
    {
        try
        {
            if (useInvoke && element.Patterns.Invoke.IsSupported)
                element.Patterns.Invoke.Pattern.Invoke();
            else
                element.Click();

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _trace.Write("could not activate Play: " + ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Waits for the League client process to appear.
    ///
    /// The process is the only signal that actually means the click worked. Watching the button
    /// instead was the mistake: it cannot tell a launching state apart from an unreadable one, and it
    /// reported success on a click that did nothing at all.
    /// </summary>
    private static async Task<bool> LeagueStartedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsLeagueClientRunning()) return true;

            await Task.Delay(1000, cancellationToken);
        }

        return false;
    }

    private static bool IsLeagueClientRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("LeagueClient").Length > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
