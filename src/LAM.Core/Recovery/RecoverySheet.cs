using System.Text;
using LAM.Core.Model;
using LAM.Core.Riot;

namespace LAM.Core.Recovery;

/// <summary>
/// Renders everything known about one account into a single page you could hand to Riot Support.
///
/// The ordering is deliberate: the identifiers Riot can verify come first (PUUID above all — it
/// survives every rename), then ownership evidence, then the things only you know. Secrets are
/// included only when explicitly asked for, because the common use of this is pasting it into a
/// support ticket and nobody should paste their password into one by accident.
/// </summary>
public static class RecoverySheet
{
    /// <summary>
    /// The first champion bought, named if the caller can name it.
    ///
    /// The sheet lives in Core and has no catalogue of its own, so naming is handed in. Without it
    /// the id still prints — with the date beside it that is verifiable evidence either way, and a
    /// support ticket saying "champion 86 on 9 November 2012" beats an empty line.
    /// </summary>
    private static string? FirstChampion(AccountEntry account, Func<int, string>? championName)
    {
        var stats = account.Identity.ClientStats;
        if (stats?.FirstChampionId is not { } id) return null;

        var name = championName?.Invoke(id);
        if (string.IsNullOrWhiteSpace(name) || name == "Unknown champion") name = "champion " + id;

        var when = stats.FirstChampionPurchasedUtc?.LocalDateTime.ToString("d MMMM yyyy");
        return when is null ? name : name + " on " + when;
    }

    public static string Render(
        AccountEntry account, bool includeSecrets = false, Func<int, string>? championName = null)
    {
        var text = new StringBuilder();
        var identity = account.Identity;
        var recovery = account.Recovery;

        void Heading(string title)
        {
            text.AppendLine();
            text.AppendLine(title.ToUpperInvariant());
            text.AppendLine(new string('-', title.Length));
        }

        void Line(string label, string? value)
            => text.AppendLine("  " + label.PadRight(26) + (string.IsNullOrWhiteSpace(value) ? "—" : value));

        text.AppendLine("RECOVERY SHEET — " + (string.IsNullOrWhiteSpace(account.Label)
            ? account.DisplayRiotId
            : account.Label));
        text.AppendLine("Generated " + DateTimeOffset.Now.ToString("d MMMM yyyy HH:mm"));

        Heading("Identifiers Riot can verify");
        Line("Riot account id", identity.RiotAccountId);
        Line("Riot ID", identity.GameName is null ? null : identity.GameName + "#" + identity.TagLine);
        Line("Region", RiotRegions.DisplayFor(account.Region) + " (" + account.Region + ")");
        Line("Platform", identity.Platform);
        Line("Summoner id", identity.SummonerId);
        Line("Account id", identity.AccountId);
        Line("Summoner level", identity.SummonerLevel?.ToString());

        if (identity.NameHistory.Count > 1)
        {
            text.AppendLine();
            text.AppendLine("  Previous Riot IDs seen by this app:");
            foreach (var entry in identity.NameHistory)
                text.AppendLine("    " + entry + "   (first seen " + entry.FirstSeenUtc.LocalDateTime.ToString("d MMM yyyy") + ")");
        }

        Heading("Ownership evidence");
        Line("Login username", account.LoginUsername);
        Line("Legacy login username", identity.Observed?.LegacyUsername);
        Line("Registered email", recovery.Email);
        Line("Email as Riot masks it", identity.Observed?.MaskedEmail);
        Line("Email provider", recovery.EmailProvider);
        Line("Still control that email", recovery.EmailStillControlled ? "yes" : "NO — flagged");
        Line("Phone on account", recovery.PhoneNumber);
        Line("How it was obtained", recovery.HowObtained);
        Line("First champion bought", recovery.FirstChampionPurchased);
        Line("Earliest purchase ref", recovery.FirstPurchaseReference);

        // Read from the client rather than remembered. These are the answers a support ticket turns
        // on, and the ones least likely to be recalled correctly about a decade-old account.
        Heading("Read from the client");
        Line("Account created", identity.Observed?.CreatedUtc?.UtcDateTime.ToString("d MMMM yyyy"));
        Line("Created (your estimate)", recovery.ApproximateCreated?.ToString("d MMMM yyyy"));
        Line("First champion, dated", FirstChampion(account, championName));
        Line("Password last changed", identity.Observed?.PasswordChangedUtc?.UtcDateTime.ToString("d MMMM yyyy"));
        Line("Original region", identity.Observed?.OriginalPlatform);
        Line("Regions seen", identity.Observed is null || identity.Observed.Regions.Count == 0
            ? null
            : string.Join(", ", identity.Observed.Regions.Select(r => r.ToString())));
        Line("Legacy account id", identity.Observed?.LegacyAccountId);
        Line("Country on account", identity.Observed?.Country);
        Line("Account state", identity.Observed?.AccountState);

        Heading("Account age, as far as Riot's API can show");
        if (identity.OldestKnownMatchUtc is { } oldest)
        {
            Line("Oldest match on record", oldest.LocalDateTime.ToString("d MMMM yyyy"));
            Line("Match id", identity.OldestKnownMatchId);
            text.AppendLine();
            text.AppendLine(identity.AgeIsLowerBoundOnly
                ? "  Riot's match history only reaches back to around mid-2021, and this account's\n" +
                  "  oldest visible game sits at that boundary. So the account is AT LEAST this old\n" +
                  "  and may be considerably older. Treat it as a floor, not a birthday."
                : "  This is the earliest game Riot still has on record for the account.");
        }
        else
        {
            Line("Oldest match on record", "not looked up yet");
        }

        Heading("Two-factor");
        Line("Enabled", recovery.MfaLooksEnabled(identity.Observed) ? "yes" : "no");
        Line("Backup codes stored", recovery.MfaBackupCodes.Count.ToString());

        if (includeSecrets)
        {
            Heading("Secrets — handle carefully");
            Line("Account password", account.Password?.Value);
            Line("Email password", recovery.EmailPassword?.Value);
            Line("Security answers", recovery.SecurityAnswers?.Value);

            if (recovery.MfaBackupCodes.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("  Backup codes:");
                foreach (var code in recovery.MfaBackupCodes) text.AppendLine("    " + code.Value);
            }
        }
        else
        {
            Heading("Secrets");
            text.AppendLine("  Omitted. Re-generate with \"include passwords\" if you actually need them —");
            text.AppendLine("  never paste them into a support ticket; Riot will never ask for your password.");
        }

        if (!string.IsNullOrWhiteSpace(recovery.Notes) || !string.IsNullOrWhiteSpace(account.Notes))
        {
            Heading("Notes");
            if (!string.IsNullOrWhiteSpace(recovery.Notes)) text.AppendLine("  " + recovery.Notes);
            if (!string.IsNullOrWhiteSpace(account.Notes)) text.AppendLine("  " + account.Notes);
        }

        var gaps = MissingFields(account).ToList();
        if (gaps.Count > 0)
        {
            Heading("Gaps worth filling in");
            foreach (var gap in gaps) text.AppendLine("  · " + gap);
        }

        return text.ToString();
    }

    /// <summary>
    /// The fields that are missing and would matter in a recovery. Surfaced while they can still be
    /// remembered, rather than on the day the account is gone.
    /// </summary>
    public static IEnumerable<string> MissingFields(AccountEntry account)
    {
        var recovery = account.Recovery;
        var identity = account.Identity;

        if (string.IsNullOrWhiteSpace(recovery.Email))
            yield return "Registered email — the single most important field.";

        if (string.IsNullOrWhiteSpace(identity.RiotAccountId))
            yield return "Riot account id — sign in once through this app and it is captured automatically.";

        // Both of these are asked for only when nothing local has already answered them. Nagging for
        // a creation date the client has reported, or for a first champion read straight off the
        // purchase dates, would be asking the user to retype what is already on the sheet.
        if (recovery.ApproximateCreated is null && identity.Observed?.CreatedUtc is null)
            yield return "Roughly when the account was created — Riot Support asks for this.";

        if (string.IsNullOrWhiteSpace(recovery.FirstChampionPurchased)
            && identity.ClientStats?.FirstChampionId is null)
        {
            yield return "First champion purchased — a classic ownership question.";
        }

        if (string.IsNullOrWhiteSpace(recovery.FirstPurchaseReference))
            yield return "Earliest purchase reference — a receipt or order number is strong evidence.";

        if (identity.OldestKnownMatchUtc is null)
            yield return "Account age estimate — run \"Estimate account age\" from the card menu.";

        if (recovery.MfaLooksEnabled(identity.Observed) && recovery.MfaBackupCodes.Count == 0)
            yield return "Two-factor is on but no backup codes are stored — lose the authenticator and you lose the account.";

        if (!recovery.EmailStillControlled)
            yield return "You no longer control the registered email. Recover the mailbox first, or contact Riot Support before anything else changes.";
    }
}
