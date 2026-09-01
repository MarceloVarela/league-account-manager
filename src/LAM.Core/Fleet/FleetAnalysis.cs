using LAM.Core.Model;
using LAM.Core.Riot;

namespace LAM.Core.Fleet;

/// <summary>
/// Everything that can only be answered by looking at <em>all</em> the accounts at once.
///
/// This is the one thing no rival tool can do. Every companion app attaches to whichever client is
/// running, so it can only ever see one account; a vault holding all of them can answer "who owns
/// this skin", "what have I spent", and "which of these is about to decay" — questions that are
/// meaningless for a single account.
///
/// Deliberately pure: it takes accounts and a catalogue and returns values. No I/O, no client, no
/// clock of its own — the caller passes the time in — so all of it is testable without a Riot install.
/// </summary>
public static class FleetAnalysis
{
    /// <summary>
    /// Which accounts own a given skin, and which do not.
    ///
    /// The point is the second list as much as the first: it answers "where should I buy this?".
    /// </summary>
    public static SkinOwnership WhoOwns(
        IReadOnlyList<AccountEntry> accounts, CatalogueSkin skin)
    {
        var owners = new List<AccountEntry>();
        var without = new List<AccountEntry>();

        foreach (var account in accounts)
        {
            if (account.Identity.OwnedSkinIds.Contains(skin.Id)) owners.Add(account);
            else without.Add(account);
        }

        return new SkinOwnership(skin, owners, without);
    }

    /// <summary>
    /// Finds skins matching a query and reports who owns each.
    ///
    /// Accounts with no captured collection are excluded from both columns rather than counted as
    /// "does not own": an account that has never been signed into knows nothing, and reporting that
    /// as an absence would send you to buy a skin you may already have.
    /// </summary>
    public static IReadOnlyList<SkinOwnership> Search(
        IReadOnlyList<AccountEntry> accounts,
        SkinCatalogue catalogue,
        string query,
        int limit = 200)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var known = accounts.Where(HasCollection).ToList();
        var needle = query.Trim();

        return catalogue.FindByName(needle, limit)
            .Select(skin => WhoOwns(known, skin))
            .ToList();
    }

    /// <summary>An account can only be said to own or not own something once it has been read.</summary>
    public static bool HasCollection(AccountEntry account)
        => account.Identity.OwnedSkinIds.Length > 0 || account.Identity.OwnedChampionIds.Length > 0;

    /// <summary>
    /// What one account's skins would have cost at their listed prices.
    ///
    /// This is deliberately <em>not</em> a valuation. It is the RP those skins are priced at today,
    /// which is a different thing from what was paid and a very different thing from what anyone
    /// would pay for the account — the last of which is a Riot ToS matter and not something this
    /// tool will express.
    ///
    /// Unpriced skins are counted and reported separately rather than guessed at. Roughly a third of
    /// a long-lived collection is legacy and has no listed price at all, so inventing a figure for
    /// them would turn a real number into a fictional one.
    /// </summary>
    public static CollectionValue ValueOf(AccountEntry account, SkinCatalogue catalogue)
    {
        var priced = 0;
        var unpriced = 0;
        var legacy = 0;
        long totalRp = 0;

        foreach (var id in account.Identity.OwnedSkinIds)
        {
            // Jade duplicates are the same skin a second time. Counting them would inflate every
            // total by about a fifth and disagree with what the collection tab shows.
            if (SkinCatalogue.IsJadeSkin(id)) continue;

            var skin = catalogue.Skin(id);
            if (skin is null)
            {
                unpriced++;
                continue;
            }

            if (skin.IsLegacy) legacy++;

            if (skin.RpCost is { } cost)
            {
                priced++;
                totalRp += cost;
            }
            else
            {
                unpriced++;
            }
        }

        return new CollectionValue(totalRp, priced, unpriced, legacy);
    }

    /// <summary>Totals across every account, for the portfolio view.</summary>
    public static Portfolio Summarise(
        IReadOnlyList<AccountEntry> accounts, SkinCatalogue catalogue, DateTimeOffset nowUtc)
    {
        var rows = accounts
            .Select(account => new PortfolioRow(
                account,
                ValueOf(account, catalogue),
                account.Identity.OwnedChampionIds.Count(id => id < SkinCatalogue.JadeOffset),
                DormancyOf(account, nowUtc)))
            .OrderByDescending(row => row.Value.KnownRp)
            .ToList();

        var distinctSkins = accounts
            .SelectMany(a => a.Identity.OwnedSkinIds)
            .Where(id => !SkinCatalogue.IsJadeSkin(id))
            .Distinct()
            .Count();

        return new Portfolio(rows, distinctSkins);
    }

    /// <summary>
    /// How long an account has gone unused, and whether that is worth saying out loud.
    ///
    /// Ranked decay is the real hazard of owning many accounts — it is silent, it only affects the
    /// accounts you are not looking at, and by the time you notice, the LP is gone. The thresholds
    /// are deliberately conservative: Riot's own decay timers vary by tier, so this flags "you have
    /// not touched this in a long time" rather than pretending to model the exact rule.
    /// </summary>
    public static Dormancy DormancyOf(AccountEntry account, DateTimeOffset nowUtc)
    {
        var lastSeen = account.LastUsedUtc ?? account.Identity.LastRefreshedUtc;
        if (lastSeen is null) return new Dormancy(null, DormancyLevel.Unknown);

        var days = (int)(nowUtc - lastSeen.Value).TotalDays;

        var level = days switch
        {
            >= 90 => DormancyLevel.Stale,
            >= 28 => DormancyLevel.Idle,
            _ => DormancyLevel.Active,
        };

        // Only a ranked account can decay, so an unranked one is merely unused.
        if (level is not DormancyLevel.Active && account.Identity.SoloRank?.IsRanked != true
            && account.Identity.FlexRank?.IsRanked != true)
        {
            level = DormancyLevel.Idle;
        }

        return new Dormancy(days, level);
    }

    /// <summary>
    /// Which accounts will need the password typed next time, and which are still instant.
    ///
    /// Worth surfacing before you need it: a stale session is not a failure, but it is the difference
    /// between walking away from a sign-in and standing over it.
    /// </summary>
    public static IReadOnlyList<SessionStanding> SessionStandings(
        IReadOnlyList<AccountEntry> accounts, DateTimeOffset nowUtc)
        => accounts
            .Select(account =>
            {
                var session = account.Session;
                if (session is null)
                    return new SessionStanding(account, null, SessionState.None);

                if (!session.IsProbablyUsable(nowUtc))
                    return new SessionStanding(account, session.CapturedUtc, SessionState.Expired);

                var expiresIn = session.CapturedUtc + StoredSession.StaleAfter - nowUtc;
                return new SessionStanding(
                    account,
                    session.CapturedUtc,
                    expiresIn <= TimeSpan.FromDays(3) ? SessionState.ExpiringSoon : SessionState.Usable);
            })
            .OrderBy(standing => standing.State)
            .ToList();
}

/// <summary>Who has a skin and who does not.</summary>
public sealed record SkinOwnership(
    CatalogueSkin Skin,
    IReadOnlyList<AccountEntry> Owners,
    IReadOnlyList<AccountEntry> Without)
{
    public bool OwnedAnywhere => Owners.Count > 0;
}

/// <summary>
/// A collection expressed in RP at listed prices — explicitly not a market value.
/// </summary>
public sealed record CollectionValue(long KnownRp, int PricedSkins, int UnpricedSkins, int LegacySkins)
{
    public int TotalSkins => PricedSkins + UnpricedSkins;

    /// <summary>
    /// Wording that cannot be mistaken for a sale price, and that admits what it does not know.
    /// </summary>
    public string Describe()
    {
        if (TotalSkins == 0) return "No collection captured yet.";

        var text = KnownRp.ToString("N0") + " RP at listed prices, across " + PricedSkins + " skins";

        if (UnpricedSkins > 0)
            text += "; " + UnpricedSkins + " more have no listed price";

        return text + ".";
    }
}

public enum DormancyLevel { Active, Idle, Stale, Unknown }

public sealed record Dormancy(int? DaysSinceUsed, DormancyLevel Level)
{
    public string Describe() => Level switch
    {
        DormancyLevel.Unknown => "never used from here",
        DormancyLevel.Active => DaysSinceUsed + "d ago",
        DormancyLevel.Idle => DaysSinceUsed + "d ago",
        DormancyLevel.Stale => DaysSinceUsed + "d ago — ranked may have decayed",
        _ => "",
    };
}

/// <summary>Ordered so the accounts needing attention sort first.</summary>
public enum SessionState { Expired, ExpiringSoon, None, Usable }

public sealed record SessionStanding(AccountEntry Account, DateTimeOffset? CapturedUtc, SessionState State)
{
    public string Describe() => State switch
    {
        SessionState.Usable => "instant sign-in",
        SessionState.ExpiringSoon => "instant, but expiring within 3 days",
        SessionState.Expired => "expired — will type the password",
        SessionState.None => "no saved session — will type the password",
        _ => "",
    };
}

public sealed record PortfolioRow(
    AccountEntry Account, CollectionValue Value, int Champions, Dormancy Dormancy);

public sealed record Portfolio(IReadOnlyList<PortfolioRow> Rows, int DistinctSkinsOwned)
{
    public long TotalKnownRp => Rows.Sum(r => r.Value.KnownRp);

    public int TotalSkins => Rows.Sum(r => r.Value.TotalSkins);

    public int TotalLegacy => Rows.Sum(r => r.Value.LegacySkins);

    public int AccountsWithCollections => Rows.Count(r => r.Value.TotalSkins > 0);
}
