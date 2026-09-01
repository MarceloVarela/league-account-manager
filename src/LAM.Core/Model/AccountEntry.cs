namespace LAM.Core.Model;

/// <summary>One Riot account: how to log in as it, what we know about it, and how to get it back.</summary>
public sealed class AccountEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Your name for it — "main", "smurf BR", "the one I bought". Shown on the card.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The Riot login name. Often an email, but not always, and not necessarily the Riot ID.</summary>
    public string LoginUsername { get; set; } = string.Empty;

    public SecretText? Password { get; set; }

    /// <summary>Riot region code as the client writes it, e.g. "BR", "NA", "EUW".</summary>
    public string Region { get; set; } = "BR";

    /// <summary>Client locale, e.g. "en_US". Written alongside the region before launch.</summary>
    public string Locale { get; set; } = "en_US";

    public List<string> Tags { get; set; } = [];

    public string Notes { get; set; } = string.Empty;

    public RecoveryInfo Recovery { get; set; } = new();

    public IdentitySnapshot Identity { get; set; } = new();

    /// <summary>
    /// The captured Riot session, if we have one. Its presence is what lets a login skip typing
    /// entirely; its absence (or expiry) sends the orchestrator down the autofill path.
    /// </summary>
    public StoredSession? Session { get; set; }

    /// <summary>
    /// When this account was moved to the trash, or null if it is live.
    ///
    /// Deletion used to be immediate and irreversible, and it took the password, the saved session
    /// and the whole recovery dossier with it — the one thing in this app that cannot be rebuilt by
    /// signing in again. A trashed entry is hidden everywhere but still on disk until it is purged
    /// on purpose.
    /// </summary>
    public DateTimeOffset? DeletedUtc { get; set; }

    public bool IsDeleted => DeletedUtc is not null;

    /// <summary>
    /// Previous passwords, newest first, with the date each was replaced.
    ///
    /// Kept because "the password I set last week does not work" is a real situation and a rotation
    /// typed with a typo is otherwise unrecoverable — the account is then locked out by this app's
    /// own record of it.
    /// </summary>
    public List<PasswordHistoryEntry> PasswordHistory { get; set; } = [];

    /// <summary>Marked as a favourite, which pins it to the front of the grid.</summary>
    public bool IsFavourite { get; set; }

    /// <summary>
    /// Replaces the password, keeping the old one in history.
    ///
    /// A no-op when the password has not actually changed, so merely opening and saving the editor
    /// does not fill the history with duplicates.
    /// </summary>
    public void SetPassword(SecretText? password, DateTimeOffset nowUtc, int keep = 10)
    {
        var old = Password;
        if (old?.Value == password?.Value) return;

        if (old is { IsEmpty: false })
        {
            PasswordHistory.Insert(0, new PasswordHistoryEntry { Password = old, ReplacedUtc = nowUtc });
            if (PasswordHistory.Count > keep) PasswordHistory.RemoveRange(keep, PasswordHistory.Count - keep);
        }

        Password = password;
    }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastUsedUtc { get; set; }

    public int LaunchCount { get; set; }

    /// <summary>Display name for the account, preferring the auto-captured Riot ID over the label.</summary>
    public string DisplayRiotId =>
        Identity is { GameName: not null and not "", TagLine: not null and not "" }
            ? $"{Identity.GameName}#{Identity.TagLine}"
            : string.IsNullOrWhiteSpace(Label) ? LoginUsername : Label;

    /// <summary>
    /// Whether there is anything to sign in with: a stored password, or a session that can be
    /// restored without one.
    /// </summary>
    public bool CanSignIn(DateTimeOffset nowUtc)
        => Password is { IsEmpty: false } || Session?.IsProbablyUsable(nowUtc) == true;

    /// <summary>
    /// What the sign-in will actually do, or why it cannot happen.
    ///
    /// Surfaced on the button itself, because a disabled control that explains itself is far better
    /// than a click that fails afterwards — and because "instant" versus "types your password" is the
    /// difference between walking away and not touching the keyboard for two seconds.
    /// </summary>
    public string SignInExplanation(DateTimeOffset nowUtc)
    {
        if (Session?.IsProbablyUsable(nowUtc) == true)
            return "Sign in from the saved session — no typing";

        if (Password is { IsEmpty: false })
            return "Types the saved password";

        return "No password saved and no usable session. Edit the account to add one.";
    }

    /// <summary>
    /// Why this account cannot be refreshed from the client right now, or null if it can.
    ///
    /// The client only ever answers for whoever is signed in, so refreshing against a different
    /// account would copy that account's rank, collection and recovery details onto this one —
    /// silently, permanently, and looking entirely plausible afterwards. An account that has never
    /// been signed into has nothing to match on, so it is refused rather than trusted.
    /// </summary>
    public string? RefreshRefusalReason(string? signedInRiotAccountId, string? signedInName)
    {
        if (string.IsNullOrWhiteSpace(Identity.RiotAccountId))
        {
            return "This account has never been signed into through the app, so there is nothing to "
                   + "match against the client. Sign in once first.";
        }

        if (string.IsNullOrWhiteSpace(signedInRiotAccountId))
            return "League is not running, or has not finished starting.";

        if (!string.Equals(signedInRiotAccountId, Identity.RiotAccountId, StringComparison.OrdinalIgnoreCase))
        {
            return "The client is signed in as " + (signedInName ?? "another account")
                   + ", not this one — nothing was changed.";
        }

        return null;
    }

    public bool MatchesSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.Trim();

        static bool Has(string? haystack, string needle)
            => haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

        return Has(Label, q)
            || Has(LoginUsername, q)
            || Has(Notes, q)
            || Has(Region, q)
            || Has(Identity.GameName, q)
            || Has(DisplayRiotId, q)
            || Has(Recovery.Email, q)
            || Tags.Any(t => Has(t, q));
    }
}

/// <summary>One superseded password, kept so a mistyped rotation is not a lockout.</summary>
public sealed class PasswordHistoryEntry
{
    public SecretText? Password { get; set; }
    public DateTimeOffset ReplacedUtc { get; set; }
}
