using LAM.Core.Model;
using LAM.Core.Riot;

namespace LAM.Core.Fleet;

/// <summary>
/// What happened to an account, and when — reconstructed from the dates the client attaches to
/// everything it owns.
///
/// This matters most for an account you did not make. The dates say what the previous owner did and
/// what you did, which is the difference between a collection you can account for in a support
/// ticket and one you can only guess at.
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
    /// Splits the collection at the date the account changed hands.
    ///
    /// Returns null when there is nothing to split by — an account you have always owned has no
    /// "before", and inventing a pivot would produce a confident and meaningless answer.
    /// </summary>
    public static OwnershipSplit? SplitByOwnership(AccountEntry account, SkinCatalogue catalogue)
    {
        if (account.Recovery.OwnedSince is not { } ownedSince) return null;

        var pivot = new DateTimeOffset(ownedSince.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var before = new List<CatalogueSkin>();
        var after = new List<CatalogueSkin>();
        var undated = 0;

        foreach (var id in account.Identity.OwnedSkinIds)
        {
            if (SkinCatalogue.IsJadeSkin(id)) continue;

            var skin = catalogue.Skin(id) ?? new CatalogueSkin(id, "Skin " + id, id / 1000, null, null, null);
            var acquired = account.Identity.SkinAcquiredUtc(id);

            if (acquired is null) undated++;
            else if (acquired < pivot) before.Add(skin);
            else after.Add(skin);
        }

        return new OwnershipSplit(pivot, Sort(before), Sort(after), undated);

        static IReadOnlyList<CatalogueSkin> Sort(List<CatalogueSkin> skins)
            => [.. skins.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// How exposed the account is to being taken back by whoever had it before.
    ///
    /// This is the part buyer guides are actually about. A seller who still holds the registered
    /// mailbox can start a recovery at any time, and no amount of playing on the account changes
    /// that — so the checks are about control of the recovery path, not about use.
    /// </summary>
    public static IReadOnlyList<ExposureCheck> RecallExposure(AccountEntry account)
    {
        var recovery = account.Recovery;
        var observed = account.Identity.Observed;
        var checks = new List<ExposureCheck>();

        checks.Add(new ExposureCheck(
            "Registered email is one you control",
            recovery.EmailStillControlled && !string.IsNullOrWhiteSpace(recovery.Email),
            "Whoever holds the registered mailbox can start a recovery. This is the single check "
            + "that decides whether an account is really yours."));

        var changedAfter = observed?.PasswordChangedUtc is { } changed
                           && recovery.OwnedSince is { } since
                           && changed > new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        checks.Add(new ExposureCheck(
            "Password changed since you got it",
            recovery.OwnedSince is null || changedAfter,
            recovery.OwnedSince is null
                ? "Set \"owned since\" on the account to check this."
                : "The client reports the last password change. If it predates your ownership, the "
                  + "previous owner may still know the password."));

        checks.Add(new ExposureCheck(
            "Two-factor is on",
            recovery.MfaLooksEnabled(observed),
            "Two-factor stops a recovery attempt that only has the password."));

        checks.Add(new ExposureCheck(
            "A phone number is on file",
            observed?.PhoneOnFile == true,
            "A phone number is a recovery route. If it is not yours, it is someone else's."));

        checks.Add(new ExposureCheck(
            "Recovery details are recorded here",
            !string.IsNullOrWhiteSpace(recovery.Email) && recovery.ApproximateCreated is not null,
            "If you ever have to prove ownership, these are the answers you will be asked for."));

        return checks;
    }
}

public sealed record ExposureCheck(string Title, bool Passed, string Why);

/// <summary>A collection divided into what came before you and what came after.</summary>
public sealed record OwnershipSplit(
    DateTimeOffset OwnedSinceUtc,
    IReadOnlyList<CatalogueSkin> BeforeYou,
    IReadOnlyList<CatalogueSkin> SinceYou,
    int Undated)
{
    public string Describe()
    {
        var text = BeforeYou.Count + " skins predate your ownership, " + SinceYou.Count + " came after.";
        if (Undated > 0) text += " " + Undated + " have no recorded date.";
        return text;
    }
}
