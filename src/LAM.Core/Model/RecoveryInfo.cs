namespace LAM.Core.Model;

/// <summary>
/// Everything you would need to prove ownership to Riot Support if an account were lost or stolen.
///
/// Riot's public API exposes none of this — no email, no creation date, no purchase history — so
/// every field here is entered by hand once and then kept safe. The fields mirror what a support
/// ticket actually asks for, which is why "first champion purchased" and a receipt reference are
/// in the list: they are the strongest ownership evidence available for an old account.
/// </summary>
public sealed class RecoveryInfo
{
    /// <summary>The email the account is registered to. The single most important recovery field.</summary>
    public string? Email { get; set; }

    /// <summary>Gmail / Outlook / a dead university address — matters because it tells you whether
    /// the email itself is still recoverable.</summary>
    public string? EmailProvider { get; set; }

    /// <summary>False for an account whose registered mailbox you no longer control. Surfaced as a
    /// warning on the card, because it changes what recovery is even possible.</summary>
    public bool EmailStillControlled { get; set; } = true;

    /// <summary>Optional. It is your vault, and a dead account is often gated behind a dead mailbox.</summary>
    public SecretText? EmailPassword { get; set; }

    public string? PhoneNumber { get; set; }

    /// <summary>When you believe the account was made. Riot asks for an approximate date.</summary>
    public DateOnly? ApproximateCreated { get; set; }


    /// <summary>A classic Riot Support ownership question.</summary>
    public string? FirstChampionPurchased { get; set; }

    /// <summary>Order id, last 4 digits, or the store used for the earliest RP purchase.</summary>
    public string? FirstPurchaseReference { get; set; }

    /// <summary>
    /// Whether you have said two-factor is on. Your answer only — the client's observation lives on
    /// <c>Identity.Observed</c> and is shown beside this rather than merged into it.
    ///
    /// Nothing writes here automatically any more. It used to be latched true from the client and
    /// could then never be cleared by a later observation, so removing 2FA from the Riot account left
    /// this permanently, silently wrong — the same trap that froze the creation date.
    /// </summary>
    public bool MfaEnabled { get; set; }

    /// <summary>Two-factor as best anyone knows: what you said, or what the client last reported.</summary>
    public bool MfaLooksEnabled(Riot.ClientAccountFacts? observed)
        => MfaEnabled || observed?.MfaEnabled == true;

    /// <summary>Two-factor backup codes. Redacted from every diagnostic path.</summary>
    public List<SecretText> MfaBackupCodes { get; set; } = [];

    /// <summary>Security question answers, if the account still has them.</summary>
    public SecretText? SecurityAnswers { get; set; }

    public string? Notes { get; set; }

    /// <summary>How complete the dossier is, 0..1 — drives the "recovery risk" hint on the card.</summary>
    public double Completeness()
    {
        var filled = 0;
        var total = 5;
        if (!string.IsNullOrWhiteSpace(Email)) filled++;
        if (!string.IsNullOrWhiteSpace(PhoneNumber)) filled++;
        if (ApproximateCreated is not null) filled++;
        if (!string.IsNullOrWhiteSpace(FirstChampionPurchased)) filled++;
        if (!string.IsNullOrWhiteSpace(FirstPurchaseReference)) filled++;
        return (double)filled / total;
    }
}
