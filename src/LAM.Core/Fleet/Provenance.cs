using LAM.Core.Model;
using LAM.Core.Riot;

namespace LAM.Core.Fleet;

/// <summary>
/// What happened to an account, and when — reconstructed from the dates the client attaches to
/// everything it owns.
///
/// Two jobs. The dates are the strongest evidence of an account's age that exists outside Riot's own
/// records, which is what a support ticket asks for. And the recovery-path checks say whether anyone
/// other than you could still take the account back.
/// </summary>
public static class Provenance
{
    /// <summary>
    /// The earliest thing on the account, by acquisition date.
    ///
    /// Together with the creation date this is the strongest age evidence a collection can give:
    /// an unavailable skin acquired in 2013 cannot be faked by a new account.
    /// </summary>
    public static (int SkinId, DateTimeOffset AcquiredUtc)? FirstSkin(AccountEntry account)
    {
        (int SkinId, DateTimeOffset AcquiredUtc)? earliest = null;

        foreach (var (id, epoch) in account.Identity.SkinAcquiredEpochSeconds)
        {
            if (epoch <= 0 || SkinCatalogue.IsJadeSkin(id)) continue;

            var when = DateTimeOffset.FromUnixTimeSeconds(epoch);
            if (earliest is null || when < earliest.Value.AcquiredUtc) earliest = (id, when);
        }

        return earliest;
    }

    /// <summary>
    /// Whether anyone other than you could still recover this account.
    ///
    /// Every check is about control of the RECOVERY PATH rather than about use: whoever holds the
    /// registered mailbox can start a recovery at any time, and no amount of playing on an account
    /// changes that. Signing in every day proves nothing; holding the mailbox proves everything.
    /// </summary>
    public static IReadOnlyList<ExposureCheck> RecoveryExposure(AccountEntry account)
    {
        var recovery = account.Recovery;
        var observed = account.Identity.Observed;
        var checks = new List<ExposureCheck>();

        checks.Add(new ExposureCheck(
            "Registered email is one you control",
            recovery.EmailStillControlled && !string.IsNullOrWhiteSpace(recovery.Email),
            "Whoever holds the registered mailbox can start a recovery. This is the single check "
            + "that decides whether an account is really yours."));

        // The last password change is reported by the client, but there is nothing honest to compare
        // it against: the app has no date for when the account became yours, and inventing one would
        // produce a confident, meaningless answer. So the checks below are all about the recovery
        // path, which is what actually decides who can take an account back.
        checks.Add(new ExposureCheck(
            "Two-factor is on",
            recovery.MfaLooksEnabled(observed),
            "Two-factor stops a recovery attempt that only has the password."));

        checks.Add(new ExposureCheck(
            "A phone number is on file",
            observed?.PhoneOnFile == true,
            "A phone number is a recovery route, so it has to be one you hold."));

        checks.Add(new ExposureCheck(
            "Recovery details are recorded here",
            !string.IsNullOrWhiteSpace(recovery.Email) && recovery.ApproximateCreated is not null,
            "If you ever have to prove ownership, these are the answers you will be asked for."));

        return checks;
    }
}

public sealed record ExposureCheck(string Title, bool Passed, string Why);
