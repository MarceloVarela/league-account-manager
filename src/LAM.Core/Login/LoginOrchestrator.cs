using LAM.Core.Model;
using LAM.Core.Riot;

namespace LAM.Core.Login;

/// <summary>
/// Runs a login: picks a strategy, falls back when one bows out, and folds what was learned back
/// into the account.
///
/// The chaining is the whole point of the design. A brand-new account has no session, so autofill
/// types the password once and captures the session it produced. From then on the swap strategy
/// takes over and no keystroke is ever injected again — until Riot expires the session, at which
/// point the swap reports a fallback, autofill types once more, and the fast path resumes.
/// </summary>
public sealed class LoginOrchestrator
{
    private readonly IReadOnlyList<ILoginStrategy> _strategies;
    private readonly RiotProcessManager _processes;
    private readonly TimeProvider _clock;
    private readonly LoginTrace _trace;

    public LoginOrchestrator(
        IReadOnlyList<ILoginStrategy> strategies,
        RiotProcessManager processes,
        TimeProvider? clock = null,
        LoginTrace? trace = null)
    {
        _strategies = strategies;
        _processes = processes;
        _clock = clock ?? TimeProvider.System;
        _trace = trace ?? LoginTrace.Null;
    }

    /// <summary>
    /// Builds the standard chain: try the saved session first, type only when that is not an option.
    /// </summary>
    public static LoginOrchestrator CreateDefault(
        RiotPaths paths,
        RiotYamlService yaml,
        RiotProcessManager processes,
        RiotLauncher launcher,
        PostLoginCapture capture,
        LoginUiProfile profile,
        TimeProvider? clock = null,
        LoginTrace? trace = null)
    {
        var strategies = new List<ILoginStrategy>
        {
            new SessionSwapStrategy(paths, yaml, processes, launcher, capture, clock, trace),
            new AutofillStrategy(paths, yaml, processes, launcher, capture, profile, trace),
        };

        return new LoginOrchestrator(strategies, processes, clock, trace);
    }

    /// <summary>
    /// Signs the account in, mutating it with anything learned along the way (session, identity,
    /// last-used time). The caller is responsible for saving the vault afterwards.
    /// </summary>
    public async Task<LoginResult> LoginAsync(
        AccountEntry account,
        AppSettings settings,
        IProgress<LoginProgress> progress,
        Func<VerificationPrompt, CancellationToken, Task<string?>>? requestVerificationCode,
        CancellationToken cancellationToken)
    {
        progress.Report(new LoginProgress(LoginStage.Starting, "Preparing to sign in…"));
        _trace.BeginAttempt(account.Label);

        // Checked here as well as inside each strategy so the refusal is immediate and identical
        // regardless of which path would have run.
        if (_processes.IsGameInProgress())
        {
            _trace.Write("REFUSED: a League game is running");
            return LoginResult.Failed(new GameInProgressException().Message);
        }

        var context = new LoginContext
        {
            Account = account,
            Settings = settings,
            Progress = progress,
            RequestVerificationCode = requestVerificationCode,
            SessionCaptured = session => account.Session = session,
            IdentityObserved = reading => reading.ApplyTo(account.Identity, _clock.GetUtcNow()),
            AccountFactsObserved = facts => facts.ApplyTo(account, _clock.GetUtcNow()),
            ClientStatsObserved = stats => stats.ApplyTo(account),
            LootObserved = loot => account.Identity.Loot = loot,
        };

        var attempted = new List<string>();
        var reasons = new List<string>();

        foreach (var strategy in _strategies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!strategy.CanAttempt(context))
                continue;

            attempted.Add(strategy.Name);
            _trace.Write("trying strategy: " + strategy.Name);

            LoginResult result;
            try
            {
                result = await strategy.LoginAsync(context, cancellationToken);
            }
            catch (GameInProgressException ex)
            {
                _trace.Write("REFUSED mid-run: a League game started");
                return LoginResult.Failed(ex.Message, ex);
            }
            catch (OperationCanceledException)
            {
                _trace.Write("CANCELLED during " + strategy.Name);
                throw;
            }
            catch (RiotClientNotFoundException ex)
            {
                _trace.Write("Riot Client not found: " + ex.Message);
                return LoginResult.Failed(ex.Message, ex);
            }
            catch (RiotClientLaunchException ex)
            {
                // Windows refused to start the client. Every strategy launches it the same way, so
                // trying the next one would fail identically — stop and say why.
                _trace.Write("could not launch the Riot Client: " + ex.Message);
                return LoginResult.Failed(ex.Message, ex);
            }
            catch (Exception ex)
            {
                // One strategy blowing up should not strand the login if another could still work.
                _trace.Write("EXCEPTION in " + strategy.Name + ": " + ex.GetType().Name + ": " + ex.Message);
                reasons.Add(strategy.Name + ": " + ex.Message);
                continue;
            }

            switch (result.Outcome)
            {
                case LoginOutcome.Success:
                    _trace.Write("SUCCESS via " + strategy.Name + ": " + result.Message);
                    account.LastUsedUtc = _clock.GetUtcNow();
                    account.LaunchCount++;
                    progress.Report(new LoginProgress(LoginStage.Done, result.Message));
                    return result;

                case LoginOutcome.FallBack:
                    _trace.Write("fallback from " + strategy.Name + ": " + result.Message);
                    reasons.Add(strategy.Name + ": " + result.Message);
                    progress.Report(new LoginProgress(
                        LoginStage.Starting, result.Message + " Trying the next method…"));
                    continue;

                case LoginOutcome.Aborted:
                case LoginOutcome.Failed:
                default:
                    _trace.Write(result.Outcome + " via " + strategy.Name + ": " + result.Message);
                    progress.Report(new LoginProgress(LoginStage.Failed, result.Message));
                    return result;
            }
        }

        var exhausted = DescribeExhaustion(account, attempted, reasons);
        _trace.Write("no strategy signed in: " + exhausted);
        return LoginResult.Failed(exhausted);
    }

    /// <summary>
    /// Explains why nothing ran, in terms of what to do about it — the difference between "add a
    /// password" and "your session expired" matters and is knowable here.
    /// </summary>
    private static string DescribeExhaustion(
        AccountEntry account, IReadOnlyList<string> attempted, IReadOnlyList<string> reasons)
    {
        if (attempted.Count == 0)
        {
            if (account.Password is null || account.Password.IsEmpty)
            {
                return "No sign-in method is available for this account: there is no saved password, " +
                       "and no usable saved session. Edit the account and add its password.";
            }

            return "No sign-in method is available for this account. Check the strategy setting — " +
                   "\"session swap only\" refuses to type, and this account has no usable session.";
        }

        return "Could not sign in. " + string.Join("  ", reasons);
    }
}
