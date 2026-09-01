using LAM.Core.Model;
using LAM.Core.Riot;

namespace LAM.Core.Login;

/// <summary>
/// Signs in by restoring a previously captured session — no typing, no synthetic input at all.
///
/// This is the fast path and the one that should run almost every time. The client's own session
/// file is written back into place before launch, so the client boots already authenticated. It
/// touches no process memory and injects no keystrokes, which also makes it the least intrusive
/// thing this app can do while Vanguard is loaded.
///
/// It cannot bootstrap itself: something has to log in the first time for there to be a session to
/// capture. That is <see cref="AutofillStrategy"/>'s job, and the orchestrator chains the two.
/// </summary>
public sealed class SessionSwapStrategy : ILoginStrategy
{
    private readonly RiotPaths _paths;
    private readonly RiotYamlService _yaml;
    private readonly RiotProcessManager _processes;
    private readonly RiotLauncher _launcher;
    private readonly PostLoginCapture _capture;
    private readonly TimeProvider _clock;
    private readonly LoginTrace _trace;

    public SessionSwapStrategy(
        RiotPaths paths,
        RiotYamlService yaml,
        RiotProcessManager processes,
        RiotLauncher launcher,
        PostLoginCapture capture,
        TimeProvider? clock = null,
        LoginTrace? trace = null)
    {
        _paths = paths;
        _yaml = yaml;
        _processes = processes;
        _launcher = launcher;
        _capture = capture;
        _clock = clock ?? TimeProvider.System;
        _trace = trace ?? LoginTrace.Null;
    }

    public string Name => "session swap";

    public bool CanAttempt(LoginContext context)
    {
        if (context.Settings.Strategy == StrategyPreference.AlwaysAutofill) return false;
        return context.Account.Session?.IsProbablyUsable(_clock.GetUtcNow()) == true;
    }

    public async Task<LoginResult> LoginAsync(LoginContext context, CancellationToken cancellationToken)
    {
        var account = context.Account;
        var session = account.Session;

        if (session is null || !session.IsProbablyUsable(_clock.GetUtcNow()))
        {
            _trace.Write("no usable saved session" + (session is null
                ? ""
                : " (age " + (int)session.Age(_clock.GetUtcNow()).TotalDays + "d, expired=" + session.KnownExpired + ")"));
            return LoginResult.FallBack("No usable saved session for this account.");
        }

        _trace.Write("using a saved session captured " + (int)session.Age(_clock.GetUtcNow()).TotalHours + "h ago");

        if (!RiotYamlService.ContainsSession(session.Yaml.Value))
        {
            // Defensive: a stored blob that carries only a device cookie would launch an
            // unauthenticated client and look like a mysterious failure. Discard it instead.
            session.KnownExpired = true;
            _trace.Write("stored session holds no credentials; discarding it");
            return LoginResult.FallBack("The saved session turned out to hold no sign-in cookies.");
        }

        context.Report(LoginStage.CheckingGameNotRunning, "Checking no game is running…");
        if (_processes.IsGameInProgress()) throw new GameInProgressException();

        context.Report(LoginStage.ClosingClients, "Closing the Riot Client…");
        await _processes.CloseClientsAsync(cancellationToken);

        if (RiotYamlService.IsDeviceBound(session.Yaml.Value))
        {
            // A DPoP-bound token is tied to a key held by the client that obtained it, so restoring
            // it would launch an unauthenticated client and look like a mysterious failure.
            session.KnownExpired = true;
            _trace.Write("stored session is DPoP device-bound and cannot be restored elsewhere");
            return LoginResult.FallBack("The saved session is bound to a device key and cannot be restored.");
        }

        context.Report(LoginStage.PreparingSession, "Restoring the saved session…");

        // Merges the authorisation block in and keeps this machine's device cookie, rather than
        // overwriting the whole file — see RiotYamlService.RestoreSession.
        _yaml.RestoreSession(session.Yaml.Value);
        _trace.Write("session restored into the client settings (device cookie preserved)");

        ApplyRegion(context);

        context.Report(LoginStage.LaunchingClient, "Starting League…");
        _launcher.Launch();

        context.Report(LoginStage.WaitingForClient, "Waiting for the client…");
        var lockfile = await Lockfile.WaitForAsync(
            _paths.RiotClientLockfile,
            TimeSpan.FromSeconds(context.Settings.LoginWindowTimeoutSeconds),
            cancellationToken);

        if (lockfile is null)
        {
            _trace.Write("the Riot Client did not come up in time");
            return LoginResult.FallBack("The Riot Client did not come up in time.");
        }

        using var client = new RiotLocalApiClient(lockfile);

        context.Report(LoginStage.WaitingForSignIn, "Checking the session was accepted…");
        var authorised = await client.WaitForAuthorizedAsync(TimeSpan.FromSeconds(30), cancellationToken);

        if (!authorised)
        {
            // Riot rotated or revoked the cookies. Remember that so we stop paying for a doomed
            // relaunch on every future click, and let the orchestrator type instead.
            session.KnownExpired = true;
            _trace.Write("client REJECTED the restored session; marking it expired and falling back to typing");
            return LoginResult.FallBack("The saved session was rejected — it has expired.");
        }

        _trace.Write("client accepted the restored session — signed in without typing");
        await _capture.RunAsync(context, cancellationToken);
        return LoginResult.Success("Signed in from the saved session.");
    }

    private void ApplyRegion(LoginContext context)
    {
        var account = context.Account;
        if (string.IsNullOrWhiteSpace(account.Region)) return;

        context.Report(LoginStage.ApplyingRegion, "Setting region to " + account.Region + "…");
        if (!_yaml.TrySetRegionAndLocale(account.Region, account.Locale, out var error) && error is not null)
        {
            // Not fatal: the client will simply open on whatever region it last used.
            context.Report(LoginStage.ApplyingRegion, error);
        }
    }

}
