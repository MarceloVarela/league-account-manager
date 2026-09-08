using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LAM.Core.Model;
using LAM.Core.Riot;

namespace LAM.App.ViewModels;

/// <summary>One account card. A display wrapper over <see cref="AccountEntry"/>.</summary>
public sealed class AccountTile : INotifyPropertyChanged
{
    private BitmapImage? _icon;
    private ImageBrush? _iconBrush;

    public AccountTile(AccountEntry account) => Account = account;

    public AccountEntry Account { get; }

    // ---- identity ----------------------------------------------------------

    /// <summary>
    /// Set once per refresh so every card agrees, and so the process list is not walked per card.
    ///
    /// Your own label is left alone - "smurf BR" identifies nothing. It is the Riot ID and the login
    /// name that are searchable, and those are what get hidden.
    /// </summary>
    public static bool RedactNames { get; set; }

    /// <summary>
    /// The name on its own.
    ///
    /// Deliberately without the tagline: the card draws <c>#TAG</c> as a separate, lighter run
    /// beside it, and returning the full Riot ID here printed it twice.
    /// </summary>
    public string Title
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Account.Label)) return Account.Label;

            var name = Account.Identity.GameName;
            if (!string.IsNullOrWhiteSpace(name)) return Hide(name);

            return Hide(Account.DisplayRiotId);
        }
    }

    /// <summary>The Riot ID once known, otherwise the login name so the card is never blank.</summary>
    public string Subtitle
    {
        get
        {
            var riotId = Account.DisplayRiotId;
            if (!string.IsNullOrWhiteSpace(riotId) && riotId != Title) return Hide(riotId);
            return Hide(Account.LoginUsername);
        }
    }

    private static string Hide(string value)
        => RedactNames ? LAM.Core.Model.StreamerMode.Redact(value) : value;

    /// <summary>Two letters for the fallback tile shown until an icon has been cached.</summary>
    public string Initials
    {
        get
        {
            var source = Account.Identity.GameName ?? Account.Label ?? Account.LoginUsername ?? "?";
            var trimmed = source.Trim();
            return trimmed.Length == 0 ? "?" : trimmed[..Math.Min(2, trimmed.Length)].ToUpperInvariant();
        }
    }

    public BitmapImage? Icon
    {
        get => _icon;
        private set
        {
            _icon = value;

            // An Ellipse fill rather than a clipped Border: WPF's ClipToBounds ignores CornerRadius,
            // which left the square corners of the icon showing behind the circular ring.
            if (value is not null)
            {
                var brush = new ImageBrush(value) { Stretch = Stretch.UniformToFill };
                brush.Freeze();
                _iconBrush = brush;
            }
            else
            {
                _iconBrush = null;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(IconBrush));
            OnPropertyChanged(nameof(HasIcon));
            OnPropertyChanged(nameof(ShowInitials));
        }
    }

    public ImageBrush? IconBrush => _iconBrush;

    public bool HasIcon => _icon is not null;

    public bool ShowInitials => _icon is null;

    /// <summary>
    /// Loads the cached icon, if one has been downloaded.
    ///
    /// Deliberately synchronous and cache-only: the grid must never block or flicker on the network.
    /// The download happens elsewhere, and the card picks it up on the next refresh.
    /// </summary>
    public void LoadIcon(ProfileIconCache cache)
    {
        var iconId = Account.Identity.ProfileIconId;
        if (iconId is not { } id) return;

        var path = cache.CachedPath(id);
        if (path is null) return;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            // Load into memory so the file is not held open — otherwise the cache could never
            // replace an icon, and the file would be locked for the app's lifetime.
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 96;
            image.EndInit();
            image.Freeze();

            Icon = image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            // A corrupt or half-written file simply falls back to the initials tile.
        }
    }

    // ---- rank --------------------------------------------------------------

    public string RankText => Account.Identity.SoloRank?.ToString() ?? "Unranked";

    public string FlexRankText =>
        Account.Identity.FlexRank is { IsRanked: true } flex ? "Flex " + flex : string.Empty;

    public bool HasFlexRank => !string.IsNullOrEmpty(FlexRankText);

    public bool IsRanked => Account.Identity.SoloRank?.IsRanked == true;

    /// <summary>
    /// The rank string the tier colour is derived from.
    ///
    /// The colour itself is resolved by <see cref="TierBrushConverter"/> against the live palette
    /// rather than being built here: a brush constructed in the view-model captures whichever theme
    /// happened to be loaded and never updates, which is the same defect as a StaticResource.
    /// </summary>
    public string TierKey => RankText;

    public string LevelText => Account.Identity.SummonerLevel is { } level ? "Lv " + level : "Lv —";

    public string RegionText => RiotRegions.DisplayFor(Account.Region);

    // ---- the spec plate -----------------------------------------------------
    //
    // The card is drawn as an engineering part: a part-code rail, a hatched rank plate, an identity
    // block, a stats grid, an optional hazard strip and a footer. These are the values that layout
    // needs which the older card did not have.

    /// <summary>Part code on the rail, e.g. <c>acc-04</c>. Assigned by the list, not stored.</summary>
    public string PartCode { get; set; } = "acc-00";

    /// <summary>Right of the rail: <c>EUW1 · LEAGUE</c>.</summary>
    public string RailRight => (PlatformCode + " · LEAGUE").ToUpperInvariant();

    private string PlatformCode => string.IsNullOrWhiteSpace(Account.Region) ? "??" : Account.Region.ToUpperInvariant();

    /// <summary>Rank without the LP suffix — the plate shows the number separately.</summary>
    public string RankName
    {
        get
        {
            var rank = Account.Identity.SoloRank;
            if (rank is null || !rank.IsRanked) return "UNRANKED";

            var tier = char.ToUpperInvariant(rank.Tier[0]) + rank.Tier[1..].ToLowerInvariant();
            return (string.IsNullOrEmpty(rank.Division) ? tier : tier + " " + rank.Division).ToUpperInvariant();
        }
    }

    public string LpValue => Account.Identity.SoloRank is { IsRanked: true } rank
        ? rank.LeaguePoints.ToString()
        : "—";

    public bool HasLp => Account.Identity.SoloRank?.IsRanked == true;

    /// <summary>
    /// The ten LP cells, filled to <c>round(lp / 10)</c>.
    ///
    /// A list of booleans rather than a width, because the design draws ten discrete cells with gaps
    /// — a proportional bar would be a different object entirely.
    /// </summary>
    public IReadOnlyList<bool> LpCells
    {
        get
        {
            if (!HasLp) return [];

            var lp = Account.Identity.SoloRank!.LeaguePoints;

            // Apex tiers have no 0-100 ladder, so a proportional read would be a lie. The rule still
            // draws — full, in the tier colour — because it is the widest piece of tier colour on the
            // card and blanking it would strip the spectrum from exactly the cards where it reads
            // loudest. HasLpBar still governs whether the figure is presented as a fraction.
            var filled = HasLpBar ? (int)Math.Round(Math.Clamp(lp, 0, 100) / 10.0) : 10;

            return [.. Enumerable.Range(0, 10).Select(i => i < filled)];
        }
    }

    /// <summary>
    /// Whether a ten-cell bar can honestly represent this rank.
    ///
    /// Below Master, LP runs 0-100 and the bar means something. At and above it LP is unbounded and a
    /// promotion is decided by the ladder cutoff, not by reaching 100 — so clamping produces a full
    /// bar for a 214 LP Master that is indistinguishable from a 100 LP Diamond, and from Challenger.
    /// The apex tiers show the figure alone; a bar that lies is worse than no bar.
    /// </summary>
    public bool HasLpBar
    {
        get
        {
            if (Account.Identity.SoloRank is not { IsRanked: true } rank) return false;

            return !ApexTiers.Contains(rank.Tier, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static readonly string[] ApexTiers = ["MASTER", "GRANDMASTER", "CHALLENGER"];

    /// <summary>
    /// The last ten ranked results, oldest first.
    ///
    /// Real games, read from the client's match history. The ranked endpoint reports season TOTALS,
    /// so for a long time this card showed "154W - 157L" under a bar it could not draw: a ten-bar
    /// chart built from a season total would be invented data. Empty until an account has been
    /// captured with the match probe.
    /// </summary>
    public IReadOnlyList<bool> FormBars => Account.Identity.Matches?.Recent
        .Select(match => match.Won)
        .ToList() ?? [];

    public bool HasFormBars => FormBars.Count > 0;

    /// <summary>
    /// The tally beside the chart.
    ///
    /// Counts the same ten games the bars show, so the number and the picture cannot disagree. It
    /// falls back to the season totals — clearly labelled — when no match history has been captured,
    /// because the totals are still true, just about a different span.
    /// </summary>
    public string FormText
    {
        get
        {
            if (Account.Identity.Matches is { IsEmpty: false } matches)
                return matches.Wins + "W \u00b7 " + matches.Losses + "L";

            return Account.Identity.SoloRank is { IsRanked: true } rank && rank.Games > 0
                ? rank.Wins + "W \u00b7 " + rank.Losses + "L \u00b7 SEASON"
                : string.Empty;
        }
    }

    public bool HasForm => FormText.Length > 0;

    /// <summary>Identity line under the name: <c>EUW1 · LVL 214 · 3D AGO</c>.</summary>
    public string IdentityMeta
    {
        get
        {
            var level = Account.Identity.SummonerLevel is { } lvl ? "LVL " + lvl : "LVL ?";
            return (PlatformCode + " · " + level + " · " + LastUsedText).ToUpperInvariant();
        }
    }

    /// <summary>The tagline shown beside the name, e.g. <c>#00000</c>.</summary>
    public string TagLineText => string.IsNullOrWhiteSpace(Account.Identity.TagLine)
        ? string.Empty
        : "#" + Account.Identity.TagLine;

    public bool HasTagLine => TagLineText.Length > 0;

    // The 2x2 stats grid. Values stay as strings so an unknown reads "—" rather than 0, which would
    // claim the account owns nothing.
    public string ChampsSkinsValue => Pair(Stats?.ChampionsOwned, Stats?.SkinsOwned);
    public string EssenceValue => Number(Stats?.BlueEssence);
    public string RiotPointsValue => Number(Stats?.RiotPoints);
    /// <summary>
    /// The essence the loot is worth, not how many rows of it there are.
    ///
    /// This printed an item count, so a rich account read "24" while the Loot tab's own headline for
    /// the same data read "24 600" — two numbers a reader had no way to reconcile. Currency entries
    /// are excluded because they are already essence, and counting them would double them.
    /// </summary>
    public string LootValue => Account.Identity.Loot is { } loot && !loot.IsEmpty
        ? Number((int)loot.Items
            .Where(item => !item.IsCurrency)
            .Sum(item => (long)(item.DisenchantValue ?? 0) * item.Count))
        : "\u2014";

    /// <summary>
    /// The design's figure format: thousands grouped with a space, never a comma.
    ///
    /// Culture-invariant on purpose. "N0" emits 12,400 on en-US and 12.400 on pt-BR, so the mono stat
    /// columns stopped aligning depending on who was running it — and a comma reads as a decimal
    /// point to half the world. A narrow no-break space keeps the group from wrapping mid-number.
    /// </summary>
    private static string Number(int? value)
        => value?.ToString("#,0", NumberFormat) ?? "\u2014";

    private static readonly System.Globalization.NumberFormatInfo NumberFormat = new()
    {
        NumberGroupSeparator = "\u202f",
        NumberGroupSizes = [3],
    };

    private static string Pair(int? left, int? right)
        => left is null && right is null ? "—" : Number(left) + " / " + Number(right);

    public string HonorValue => Stats?.HonorLevel?.ToString() ?? "\u2014";

    /// <summary>When the client data was last read. The card is a snapshot, and says so.</summary>
    public string SyncedValue => Stats is { } stats
        ? stats.CapturedUtc.LocalDateTime.ToString("d MMM").ToLowerInvariant()
        : "never";

    /// <summary>
    /// The email row.
    ///
    /// Shows Riot's own mask rather than what was typed: the mask is what the client actually
    /// reports, so it cannot be stale, and it is already redacted. An account with nothing recorded
    /// says so and invites the fix, which is more useful than an empty row.
    /// </summary>
    public string EmailLine
    {
        get
        {
            var masked = Account.Identity.Observed?.MaskedEmail;
            if (!string.IsNullOrWhiteSpace(masked)) return ("EMAIL \u00b7 " + masked).ToUpperInvariant();

            return string.IsNullOrWhiteSpace(Account.Recovery.Email)
                ? "EMAIL \u00b7 NOT STORED \u00b7 ADD IT"
                : "EMAIL \u00b7 STORED";
        }
    }

    /// <summary>Footer verb: the live account is not something you log into again.</summary>
    public string FooterAction => IsSignedIn ? "ACTIVE SESSION" : "LOG IN";

    /// <summary>
    /// The flex line under the rank headline.
    ///
    /// "1 PLACEMENT LEFT" is worth its own wording: PlacementsRemaining is a client-only field the
    /// public API does not expose, and it is the difference between unranked and nearly ranked.
    /// </summary>
    public string FlexLine
    {
        get
        {
            var flex = Account.Identity.FlexRank;
            if (flex is null || !flex.IsRanked) return string.Empty;

            if (flex.PlacementsRemaining > 0)
            {
                return ("FLEX — " + flex.PlacementsRemaining + " PLACEMENT"
                        + (flex.PlacementsRemaining == 1 ? "" : "S") + " LEFT").ToUpperInvariant();
            }

            var tier = char.ToUpperInvariant(flex.Tier[0]) + flex.Tier[1..].ToLowerInvariant();
            return ("FLEX " + (string.IsNullOrEmpty(flex.Division) ? tier : tier + " " + flex.Division))
                .ToUpperInvariant();
        }
    }

    public bool HasFlexLine => FlexLine.Length > 0;

    /// <summary>The hazard strip, when there is something worth flagging.</summary>
    /// <summary>
    /// The warning, as written.
    ///
    /// Not uppercased: the readability rules put warnings in 12.5px Barlow body text, and shouting a
    /// full sentence in caps is both harder to read and wider — which is what clipped it.
    /// </summary>
    public string HazardText => RecoveryWarning;

    public bool HasHazard => HasRecoveryWarning;

    // ---- state -------------------------------------------------------------

    public IReadOnlyList<string> Tags => Account.Tags;

    public bool HasTags => Account.Tags.Count > 0;

    public string LastUsedText
    {
        get
        {
            if (Account.LastUsedUtc is not { } used) return "never";

            var ago = DateTimeOffset.UtcNow - used;
            if (ago < TimeSpan.FromMinutes(2)) return "just now";
            if (ago < TimeSpan.FromHours(1)) return (int)ago.TotalMinutes + "m ago";
            if (ago < TimeSpan.FromDays(1)) return (int)ago.TotalHours + "h ago";
            if (ago < TimeSpan.FromDays(30)) return (int)ago.TotalDays + "d ago";
            return used.LocalDateTime.ToString("d MMM yyyy");
        }
    }

    public bool IsInstant => Account.Session?.IsProbablyUsable(DateTimeOffset.UtcNow) == true;

    /// <summary>
    /// Tells you before you click whether this will be instant or will type. Worth surfacing: it is
    /// the difference between "go make coffee" and "do not touch anything for two seconds".
    /// </summary>
    public string SignInModeText =>
        IsInstant ? "Instant" :
        Account.Password is { IsEmpty: false } ? "Types password" : "No password saved";

    public bool CanSignIn => Account.CanSignIn(DateTimeOffset.UtcNow);

    /// <summary>Says why the button is unavailable, rather than letting the click fail.</summary>
    public string SignInTooltip => Account.SignInExplanation(DateTimeOffset.UtcNow);

    // ---- what the client last told us --------------------------------------

    private LAM.Core.Riot.ClientStats? Stats => Account.Identity.ClientStats;

    /// <summary>Wallet and collection on one line, omitting anything not known.</summary>
    public string StatsLine
    {
        get
        {
            if (Stats is null) return string.Empty;

            var parts = new List<string>();
            if (Stats.RiotPoints is { } rp) parts.Add(rp.ToString("N0") + " RP");
            if (Stats.BlueEssence is { } be) parts.Add(Compact(be) + " BE");
            if (Stats.ChampionsOwned is { } champs) parts.Add(champs + " champs");
            if (Stats.SkinsOwned is { } skins) parts.Add(skins + " skins");
            if (Stats.HonorLevel is { } honor) parts.Add("Honor " + honor);

            return string.Join("  ·  ", parts);
        }
    }

    public bool HasStats => !string.IsNullOrEmpty(StatsLine);

    /// <summary>272382 reads as 272k. Exact figures belong in the editor, not on a tile.</summary>
    private static string Compact(int value)
        => value >= 1_000_000 ? (value / 1_000_000.0).ToString("0.#") + "M"
         : value >= 1_000 ? (value / 1_000.0).ToString("0.#") + "k"
         : value.ToString();

    /// <summary>
    /// When the client data was captured.
    ///
    /// Shown because these are a snapshot from the last sign-in, not a live reading — the client can
    /// only be asked about the account it is signed into. Presenting them as current would be a lie
    /// the moment the account sits unused for a week.
    /// </summary>
    public string StatsAsOfText
    {
        get
        {
            if (Stats is null) return string.Empty;

            var ago = DateTimeOffset.UtcNow - Stats.CapturedUtc;
            if (ago < TimeSpan.FromHours(1)) return "as of just now";
            if (ago < TimeSpan.FromDays(1)) return "as of " + (int)ago.TotalHours + "h ago";
            return "as of " + Stats.CapturedUtc.LocalDateTime.ToString("d MMM");
        }
    }

    // ---- flags -------------------------------------------------------------

    /// <summary>True when this is the account the client is signed into right now.</summary>
    public bool IsSignedIn { get; private set; }

    public void SetSignedIn(bool value)
    {
        if (IsSignedIn == value) return;
        IsSignedIn = value;
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(FooterAction));
    }

    /// <summary>Set when another entry shares this account's durable id.</summary>
    public bool IsDuplicate { get; private set; }

    public void SetDuplicate(bool value)
    {
        if (IsDuplicate == value) return;
        IsDuplicate = value;
        OnPropertyChanged(nameof(IsDuplicate));
        OnPropertyChanged(nameof(RecoveryWarning));
        OnPropertyChanged(nameof(HasRecoveryWarning));
        OnPropertyChanged(nameof(HasDismissedWarnings));
    }

    /// <summary>Banned or suspended, as reported by the client.</summary>
    public bool IsDisabled =>
        Account.Identity.Observed?.AccountState is { } state
        && !string.Equals(state, "ENABLED", StringComparison.OrdinalIgnoreCase);

    public string AccountStateText => Account.Identity.Observed?.AccountState ?? string.Empty;

    // ---- recovery ----------------------------------------------------------

    /// <summary>
    /// Flags an account that would be hard to get back, while the gap can still be closed.
    ///
    /// A typed email that disagrees with the one the client reports comes first: that is not an
    /// incomplete record but a wrong one, and it stays invisible until the day it matters.
    /// </summary>
    /// <summary>
    /// The warning this account should show, as a stable id plus its text.
    ///
    /// The id is what dismissal is keyed on. Three of these interpolate a mask or a reported state,
    /// so the sentence changes while the underlying problem does not — keying on the text would make
    /// a dismissal evaporate the next time Riot phrased something differently.
    ///
    /// Only the first match is shown. A card with four problems that lists all four is a card nobody
    /// reads; the most serious one is the one worth acting on.
    /// </summary>
    public (string Id, string Text)? Warning
    {
        get
        {
            var observed = Account.Identity.Observed;

            if (IsDuplicate) return ("duplicate", "Same account is saved twice");
            if (IsDisabled) return ("disabled", "Riot reports this account as " + AccountStateText);

            if (observed?.EmailLooksConsistentWith(Account.Recovery.Email) == false)
                return ("email-mismatch",
                    "Saved email does not match this account (" + observed.MaskedEmail + ")");

            if (!Account.Recovery.EmailStillControlled)
                return ("email-lost", "You no longer control the email");

            if (string.IsNullOrWhiteSpace(Account.Recovery.Email))
            {
                // Claiming nothing is saved is simply untrue once the client has told us the mask.
                // Riot only ever reveals a masked address, so the full one is still worth having —
                // but the prompt should acknowledge what is already known.
                return observed?.MaskedEmail is { } mask
                    ? ("email-partial", "Email known (" + mask + ") \u2014 add the full address")
                    : ("email-missing", "No recovery email saved");
            }

            if (Account.Recovery.MfaLooksEnabled(Account.Identity.Observed)
                && Account.Recovery.MfaBackupCodes.Count == 0)
                return ("mfa-no-codes", "2FA on, no backup codes saved");

            if (Account.Recovery.Completeness() < 0.5)
                return ("incomplete", "Recovery details incomplete");

            return null;
        }
    }

    /// <summary>The warning text, or null once it has been dismissed on this account.</summary>
    public string? RecoveryWarning
        => Warning is { } warning && !Account.DismissedWarnings.Contains(warning.Id, StringComparer.Ordinal)
            ? warning.Text
            : null;

    public bool HasRecoveryWarning => RecoveryWarning is not null;

    /// <summary>Whether anything is currently hidden on this account, for the menu's label.</summary>
    public bool HasDismissedWarnings => Account.DismissedWarnings.Count > 0;

    /// <summary>Stops the current warning showing on this card. Returns false if there was none.</summary>
    public bool DismissWarning()
    {
        if (Warning is not { } warning) return false;
        if (Account.DismissedWarnings.Contains(warning.Id, StringComparer.Ordinal)) return false;

        Account.DismissedWarnings.Add(warning.Id);
        return true;
    }

    public void RestoreWarnings() => Account.DismissedWarnings.Clear();

    /// <summary>
    /// Re-reads every displayed value from the account.
    ///
    /// The list must match what the card actually binds. It previously named eighteen properties from
    /// a card design that no longer exists and none of the ones on screen — which went unnoticed only
    /// because ApplyFilter throws every tile away and rebuilds. That masking ends the moment anything
    /// updates a card in place, so the list is kept complete deliberately.
    /// </summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Initials));
        OnPropertyChanged(nameof(PartCode));
        OnPropertyChanged(nameof(RailRight));
        OnPropertyChanged(nameof(RankName));
        OnPropertyChanged(nameof(LpValue));
        OnPropertyChanged(nameof(HasLp));
        OnPropertyChanged(nameof(HasLpBar));
        OnPropertyChanged(nameof(LpCells));
        OnPropertyChanged(nameof(HasTagLine));
        OnPropertyChanged(nameof(FormText));
        OnPropertyChanged(nameof(FormBars));
        OnPropertyChanged(nameof(HasFormBars));
        OnPropertyChanged(nameof(HasForm));
        OnPropertyChanged(nameof(IdentityMeta));
        OnPropertyChanged(nameof(TagLineText));
        OnPropertyChanged(nameof(ChampsSkinsValue));
        OnPropertyChanged(nameof(EssenceValue));
        OnPropertyChanged(nameof(RiotPointsValue));
        OnPropertyChanged(nameof(LootValue));
        OnPropertyChanged(nameof(HonorValue));
        OnPropertyChanged(nameof(SyncedValue));
        OnPropertyChanged(nameof(EmailLine));
        OnPropertyChanged(nameof(HazardText));
        OnPropertyChanged(nameof(HasHazard));
        OnPropertyChanged(nameof(FooterAction));
        OnPropertyChanged(nameof(FlexLine));
        OnPropertyChanged(nameof(HasFlexLine));
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(CanSignIn));
        OnPropertyChanged(nameof(SignInTooltip));
        OnPropertyChanged(nameof(IsInstant));
        OnPropertyChanged(nameof(SignInModeText));
        OnPropertyChanged(nameof(RankText));
        OnPropertyChanged(nameof(FlexRankText));
        OnPropertyChanged(nameof(HasFlexRank));
        OnPropertyChanged(nameof(TierKey));
        OnPropertyChanged(nameof(LevelText));
        OnPropertyChanged(nameof(RegionText));
        OnPropertyChanged(nameof(LastUsedText));
        OnPropertyChanged(nameof(HasStats));
        OnPropertyChanged(nameof(StatsLine));
        OnPropertyChanged(nameof(StatsAsOfText));
        OnPropertyChanged(nameof(RecoveryWarning));
        OnPropertyChanged(nameof(HasRecoveryWarning));

    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
