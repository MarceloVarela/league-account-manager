using LAM.Core.Model;

namespace LAM.Core.Login;

/// <summary>The stages a login passes through, so the UI can narrate progress honestly.</summary>
public enum LoginStage
{
    Starting,
    CheckingGameNotRunning,
    ClosingClients,
    PreparingSession,
    ApplyingRegion,
    LaunchingClient,
    WaitingForClient,
    LocatingLoginForm,
    Typing,
    WaitingForVerificationCode,
    WaitingForSignIn,
    CapturingSession,
    ReadingIdentity,
    Done,
    Failed,
}

public sealed record LoginProgress(LoginStage Stage, string Message)
{
    public override string ToString() => Message;
}

public enum LoginOutcome
{
    /// <summary>The client is signed in.</summary>
    Success,

    /// <summary>The strategy could not apply; the orchestrator should try the next one.</summary>
    FallBack,

    /// <summary>Stop. Retrying will not help without the user doing something first.</summary>
    Failed,

    /// <summary>The user cancelled, or a safety check stopped us.</summary>
    Aborted,
}

public sealed record LoginResult(LoginOutcome Outcome, string Message, Exception? Error = null)
{
    public bool IsSuccess => Outcome == LoginOutcome.Success;

    public static LoginResult Success(string message = "Signed in.") => new(LoginOutcome.Success, message);
    public static LoginResult FallBack(string why) => new(LoginOutcome.FallBack, why);
    public static LoginResult Failed(string why, Exception? error = null) => new(LoginOutcome.Failed, why, error);
    public static LoginResult Aborted(string why) => new(LoginOutcome.Aborted, why);
}

/// <summary>
/// Everything a strategy needs for one login attempt, plus the callbacks it uses to talk back to
/// the UI while it runs.
/// </summary>
public sealed class LoginContext
{
    public required AccountEntry Account { get; init; }
    public required AppSettings Settings { get; init; }
    public required IProgress<LoginProgress> Progress { get; init; }

    /// <summary>
    /// Asks the user for a two-factor or emailed verification code. Returning null cancels.
    /// A login that needs a code can never be fully hands-free, so we ask rather than pretend.
    /// </summary>
    public Func<VerificationPrompt, CancellationToken, Task<string?>>? RequestVerificationCode { get; init; }

    /// <summary>
    /// Raised when a strategy captures a fresh session, so the caller can persist it to the vault.
    /// Kept as a callback because only the caller holds the unlocked vault.
    /// </summary>
    public Action<StoredSession>? SessionCaptured { get; init; }

    /// <summary>Raised when the local client tells us who signed in.</summary>
    public Action<Riot.IdentityReading>? IdentityObserved { get; init; }

    /// <summary>Raised with the recovery details read from the client after sign-in.</summary>
    public Action<Riot.ClientAccountFacts>? AccountFactsObserved { get; init; }

    /// <summary>Raised with rank, wallet and collection read from the client after sign-in.</summary>
    public Action<Riot.ClientStats>? ClientStatsObserved { get; init; }

    /// <summary>Raised with the loot inventory read from the client after sign-in.</summary>
    public Action<Riot.LootSnapshot>? LootObserved { get; init; }

    public void Report(LoginStage stage, string message) => Progress.Report(new LoginProgress(stage, message));
}

public sealed record VerificationPrompt(string Title, string Message);

/// <summary>One way of getting an account signed in.</summary>
public interface ILoginStrategy
{
    string Name { get; }

    /// <summary>
    /// Whether this strategy can be attempted for the account at all. A cheap check — the expensive
    /// verification happens inside <see cref="LoginAsync"/>.
    /// </summary>
    bool CanAttempt(LoginContext context);

    Task<LoginResult> LoginAsync(LoginContext context, CancellationToken cancellationToken);
}

/// <summary>Raised when a switch is refused because a match is running.</summary>
public sealed class GameInProgressException : Exception
{
    public GameInProgressException()
        : base("A League game is running. Switching accounts now would close it and earn you a leaver penalty. " +
               "Finish or quit the game first.")
    {
    }
}
