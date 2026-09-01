namespace LAM.Core.Login;

/// <summary>
/// Picks the Play button on the Riot Client home page.
///
/// Separated and pure for the same reason as <see cref="SubmitButtonChooser"/>: this clicks something
/// on a busy page, and the page is busier than the login form — navigation tabs, "Watch Now",
/// "Gifts", a game-mode chevron beside Play, and a **friends list full of user-chosen names**. A
/// live capture from this machine contained a friend called "siege player", which a naive
/// "contains play" match would happily click.
///
/// So the match is exact rather than substring, with the obvious near-misses excluded outright, and
/// ambiguity resolves to clicking nothing — the account is signed in either way, and not launching
/// the game is a far better outcome than clicking an unknown control.
/// </summary>
public static class PlayButtonChooser
{
    /// <summary>
    /// Exact button labels that start the game. Localised because the app already writes a
    /// per-account client language, so a BR account really does show "Jogar".
    /// </summary>
    public static readonly string[] PlayLabels =
    [
        "play", "jogar", "jugar", "jouer", "spielen", "gioca", "graj", "играть", "플레이", "プレイ",
    ];

    /// <summary>
    /// Labels that contain a play word but are emphatically not the Play button. "player" is the
    /// one that matters — friend names flow straight into the automation tree.
    /// </summary>
    public static readonly string[] NeverPlay =
    [
        "player", "players", "replay", "replays", "playlist", "playstation", "playtime", "playing",
    ];

    /// <summary>
    /// The Play button is a large primary action; the chevron beside it that opens the game-mode
    /// menu is small. Sized to keep the button and drop the chevron.
    /// </summary>
    private const double MinimumPlayArea = 2000;

    /// <summary>Returns the index of the button to click, or null to leave the client alone.</summary>
    public static int? Choose(IReadOnlyList<ButtonCandidate> candidates)
    {
        var matches = new List<(int Index, ButtonCandidate Button)>();

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (!candidate.IsEnabled) continue;
            if (candidate.Area < MinimumPlayArea) continue;

            if (IsPlay(candidate.Name) || IsPlay(candidate.AutomationId))
                matches.Add((i, candidate));
        }

        if (matches.Count == 0) return null;

        // Exactly one is the expected case. More than one means something on the page is named the
        // same way and we cannot tell them apart, so nothing is clicked.
        return matches.Count == 1 ? matches[0].Index : null;
    }

    /// <summary>
    /// Exact match after trimming, never a substring.
    ///
    /// Substring matching is what would turn a friend named "siege player" into a click target, and
    /// the exclusion list is a second line of defence rather than the only one.
    /// </summary>
    private static bool IsPlay(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;

        var text = label.Trim();

        if (NeverPlay.Any(bad => text.Contains(bad, StringComparison.OrdinalIgnoreCase)))
            return false;

        return PlayLabels.Any(play => string.Equals(text, play, StringComparison.OrdinalIgnoreCase));
    }
}
