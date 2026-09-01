namespace LAM.Core.Model;

/// <summary>
/// A captured Riot Client session — the contents of <c>RiotGamesPrivateSettings.yaml</c> taken
/// straight after a successful login with "Stay signed in" ticked.
///
/// This is the payload that makes a zero-typing login possible: written back into place before
/// launch, the client boots already authenticated. It is a bearer credential in its own right,
/// which is why it lives inside the encrypted vault and never on disk in the clear.
/// </summary>
public sealed class StoredSession
{
    /// <summary>The full YAML document, verbatim.</summary>
    public SecretText Yaml { get; set; } = new(string.Empty);

    public DateTimeOffset CapturedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Which account the session was captured for, when known. Guards against a mix-up writing one
    /// account's session while the UI believes it launched another.
    /// </summary>
    public string? Puuid { get; set; }

    /// <summary>
    /// Set once a swap attempt has been refused by the client. Sticky so we stop paying the cost of
    /// a doomed swap on every launch; cleared when a fresh session is captured.
    /// </summary>
    public bool KnownExpired { get; set; }

    /// <summary>
    /// Riot rotates these; a stale one just falls back to autofill, so the cutoff only decides when
    /// we stop *trying* first. Three weeks keeps the fast path useful without wasting a relaunch on
    /// sessions that are almost certainly dead.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(21);

    public bool IsProbablyUsable(DateTimeOffset nowUtc)
        => !KnownExpired
           && !Yaml.IsEmpty
           && nowUtc - CapturedUtc < StaleAfter;

    public TimeSpan Age(DateTimeOffset nowUtc) => nowUtc - CapturedUtc;
}
