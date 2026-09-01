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
    /// <summary>
    /// Rank colours, roughly matching Riot's own emblems.
    ///
    /// Used as an accent — the border and the rank text — rather than a full-card wash, so a grid of
    /// accounts still reads as a grid rather than a paint chart.
    /// </summary>
    private static readonly Dictionary<string, string> TierColours = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IRON"] = "#FF7C7368",
        ["BRONZE"] = "#FFA9714B",
        ["SILVER"] = "#FF9FB0C0",
        ["GOLD"] = "#FFE0B255",
        ["PLATINUM"] = "#FF4FC3B0",
        ["EMERALD"] = "#FF4FBF7B",
        ["DIAMOND"] = "#FF7CA6F5",
        ["MASTER"] = "#FFB667E0",
        ["GRANDMASTER"] = "#FFE05260",
        ["CHALLENGER"] = "#FFF0C674",
    };

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

    public string Title => string.IsNullOrWhiteSpace(Account.Label)
        ? Hide(Account.DisplayRiotId)
        : Account.Label;

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

    /// <summary>The tier's colour, or the muted text colour when unranked.</summary>
    public Brush RankBrush
    {
        get
        {
            var tier = Account.Identity.SoloRank?.Tier;
            if (tier is not null && TierColours.TryGetValue(tier, out var hex))
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);

            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF9AA1B1")!);
        }
    }

    public string LevelText => Account.Identity.SummonerLevel is { } level ? "Lv " + level : "Lv —";

    public string RegionText => RiotRegions.DisplayFor(Account.Region);

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
    public string? RecoveryWarning
    {
        get
        {
            var observed = Account.Identity.Observed;

            if (IsDuplicate) return "Same account is saved twice";
            if (IsDisabled) return "Riot reports this account as " + AccountStateText;

            if (observed?.EmailLooksConsistentWith(Account.Recovery.Email) == false)
                return "Saved email does not match this account (" + observed.MaskedEmail + ")";

            if (!Account.Recovery.EmailStillControlled) return "You no longer control the email";

            if (string.IsNullOrWhiteSpace(Account.Recovery.Email))
            {
                // Claiming nothing is saved is simply untrue once the client has told us the mask.
                // Riot only ever reveals a masked address, so the full one is still worth having —
                // but the prompt should acknowledge what is already known.
                return observed?.MaskedEmail is { } mask
                    ? "Email known (" + mask + ") — add the full address"
                    : "No recovery email saved";
            }
            if (Account.Recovery.MfaLooksEnabled(Account.Identity.Observed)
                && Account.Recovery.MfaBackupCodes.Count == 0)
                return "2FA on, no backup codes saved";
            if (Account.Recovery.Completeness() < 0.5) return "Recovery details incomplete";

            return null;
        }
    }

    public bool HasRecoveryWarning => RecoveryWarning is not null;

    public void Refresh()
    {
        foreach (var property in new[]
                 {
                     nameof(Title), nameof(Subtitle), nameof(Initials), nameof(RegionText), nameof(LevelText),
                     nameof(RankText), nameof(FlexRankText), nameof(HasFlexRank), nameof(IsRanked),
                     nameof(RankBrush), nameof(Tags), nameof(HasTags), nameof(LastUsedText),
                     nameof(SignInModeText), nameof(IsInstant), nameof(CanSignIn),
                     nameof(RecoveryWarning), nameof(HasRecoveryWarning),
                     nameof(StatsLine), nameof(HasStats), nameof(StatsAsOfText),
                     nameof(IsDisabled), nameof(AccountStateText),
                 })
        {
            OnPropertyChanged(property);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
