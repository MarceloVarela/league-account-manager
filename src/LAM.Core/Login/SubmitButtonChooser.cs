using System.Text.RegularExpressions;

namespace LAM.Core.Login;

/// <summary>What we know about one clickable control on the login page.</summary>
/// <param name="Name">UI Automation name, if any. On this page most buttons have none.</param>
/// <param name="AutomationId">UI Automation id, if any.</param>
/// <param name="HelpText">Accessible description, if any.</param>
/// <param name="IsEnabled">Disabled buttons are never chosen.</param>
/// <param name="Top">Top edge in screen coordinates.</param>
/// <param name="Area">Bounding-box area. The primary discriminator — see the chooser.</param>
public sealed record ButtonCandidate(
    string? Name,
    string? AutomationId,
    string? HelpText,
    bool IsEnabled,
    double Top,
    double Area)
{
    public string Describe()
        => "name='" + (Name ?? "") + "' id='" + (AutomationId ?? "") + "' enabled=" + IsEnabled
           + " top=" + (int)Top + " area=" + (int)Area;
}

/// <summary>
/// Picks the button that submits the login form.
///
/// Pure and separated from UI Automation so the choice can be tested, because getting it wrong is
/// not a cosmetic failure — it has already opened the client's version dialog once, and the same
/// mistake pointed one row higher would have started an OAuth flow with an identity provider.
///
/// The rules come from the page as it actually is, measured from a live dump rather than guessed:
///
/// <code>
///   Button name=''          at 1008,684  52x33  = 1716   the five social logins,
///   Button name=''          at 1067,684  52x33  = 1716   all completely unnamed
///   Button name=''          at 1126,684  52x33  = 1716
///   Button name=''          at 1185,684  52x33  = 1716
///   Button name=''          at 1244,684  53x33  = 1749
///   Button name=''          at 1120,938  64x64  = 4096   the submit arrow
///   Button name='v137.0.3'  at 1248,1084 48x14  =  672   version label
///   Button name='Close window' at 2456,288 32x24 = 768   title-bar chrome
/// </code>
///
/// Two things that dump settles. The social buttons carry **no name and no id**, so no amount of
/// hint-matching can exclude them — only geometry can. And the submit control is more than twice the
/// area of anything else on the form, which makes size the reliable discriminator; "lowest on the
/// page" is not, because the footer sits below the arrow.
/// </summary>
public static class SubmitButtonChooser
{
    /// <summary>
    /// Anything smaller than this is a label, a link or a title-bar glyph rather than the form's
    /// primary action. Sized to sit above the 768 px² window chrome and the 672 px² version label,
    /// and well below the 1716 px² social buttons.
    /// </summary>
    private const double MinimumPrimaryActionArea = 1000;

    /// <summary>
    /// How much larger the winner must be than the runner-up. The arrow leads the social row 4096 to
    /// 1716 — a factor of 2.4 — so this is comfortably satisfied while still refusing to guess
    /// between similarly sized controls.
    /// </summary>
    private const double RequiredSizeLead = 1.5;

    /// <summary>Matches a version label such as <c>v137.0.3</c>, whose text changes every update.</summary>
    private static readonly Regex VersionLabel = new(@"^v?\d+(\.\d+)+$", RegexOptions.Compiled);

    /// <summary>
    /// Returns the index of the button to click, or null to fall back to Enter.
    /// </summary>
    /// <param name="candidates">Buttons found on the page, in tree order.</param>
    /// <param name="passwordFieldTop">Top edge of the password field, in the same coordinate space.</param>
    public static int? Choose(
        IReadOnlyList<ButtonCandidate> candidates,
        double passwordFieldTop,
        LoginUiProfile profile)
    {
        var eligible = new List<(int Index, ButtonCandidate Button)>();

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];

            // The arrow is disabled until both fields have content, so the caller must look *after*
            // typing. Scanning too early is what made this fall through to the version label.
            if (!candidate.IsEnabled) continue;

            if (IsWindowChrome(candidate, profile)) continue;
            if (IsVersionLabel(candidate)) continue;
            if (candidate.Area < MinimumPrimaryActionArea) continue;
            if (MatchesAny(candidate, profile.SocialProviderHints, profile)) continue;

            // Below the password field only. Besides finding the arrow, this rules out the "Sign-in"
            // tab at the top of the page, which matches the submit hints by name.
            if (candidate.Top <= passwordFieldTop) continue;

            eligible.Add((i, candidate));
        }

        if (eligible.Count == 0) return null;

        // A button that says what it does wins outright.
        var named = eligible
            .Where(e => MatchesAny(e.Button, profile.SubmitHints, profile))
            .ToList();

        if (named.Count > 0)
            return named.OrderByDescending(e => e.Button.Area).First().Index;

        // Nothing self-identifies — the real page names none of these — so size decides.
        var bySize = eligible.OrderByDescending(e => e.Button.Area).ToList();

        if (bySize.Count == 1) return bySize[0].Index;

        // Refuse to guess between controls of comparable size. That is the unnamed social row, and
        // picking one of those is the single worst thing this code could do.
        var winner = bySize[0].Button.Area;
        var runnerUp = bySize[1].Button.Area;

        return runnerUp > 0 && winner / runnerUp < RequiredSizeLead ? null : bySize[0].Index;
    }

    private static bool IsWindowChrome(ButtonCandidate candidate, LoginUiProfile profile)
        => MatchesAny(candidate, profile.WindowChromeHints, profile);

    /// <summary>
    /// Recognised by shape rather than by a hint list: the literal text changes with every client
    /// update, so any list of version strings would be stale within a patch.
    /// </summary>
    private static bool IsVersionLabel(ButtonCandidate candidate)
        => !string.IsNullOrWhiteSpace(candidate.Name) && VersionLabel.IsMatch(candidate.Name.Trim());

    private static bool MatchesAny(ButtonCandidate candidate, IEnumerable<string> hints, LoginUiProfile profile)
    {
        var haystacks = new[] { candidate.Name, candidate.AutomationId, candidate.HelpText };
        var hintList = hints.ToList();

        return haystacks.Any(text => profile.MatchesAny(text, hintList));
    }
}
