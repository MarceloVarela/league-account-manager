using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LAM.App.Services;
using LAM.App.ViewModels;
using LAM.Core.Fleet;
using LAM.Core.Model;
using LAM.Core.Riot;

namespace LAM.App.Views;

/// <summary>
/// Everything known about one account: rank, wallet, collection and recovery details.
///
/// Opened by clicking a card. Signing in is a separate button here too — it closes the Riot Client
/// and League, so it should never be something that happens because a window was opened.
/// </summary>
public partial class AccountDetailWindow : Window
{
    private const int SkinsPerRow = 5;

    private readonly AccountEntry _account;
    private readonly AppServices _services;
    private readonly AccountTile _tile;

    private List<SkinRow> _allSkins = [];

    public AccountDetailWindow(AccountEntry account, AppServices services)
    {
        InitializeComponent();

        _account = account;
        _services = services;
        _tile = new AccountTile(account);
        _tile.LoadIcon(services.Icons);

        DataContext = _tile;
        Title = account.DisplayRiotId;

        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>Raised when the user presses Sign in; the main window owns the actual sign-in.</summary>
    public event Action? SignInRequested;

    private async Task LoadAsync()
    {
        RenderOverview();
        RenderRecovery();

        // The catalogue is six megabytes from the client and only changes on a patch, so it is
        // refreshed at most daily and otherwise read from disk.
        if (!_services.Catalogue.IsLoaded)
        {
            StatusText.Text = "Loading the skin catalogue…";
            await _services.Catalogue.RefreshAsync(_services.RiotPaths, TimeSpan.FromDays(1), CancellationToken.None);
        }

        RenderCollection();
        RenderLoot();
        StatusText.Text = _services.Catalogue.IsLoaded
            ? string.Empty
            : "No skin catalogue yet — sign in once with League running to capture it.";
    }

    // ---- overview ----------------------------------------------------------

    private void RenderOverview()
    {
        var identity = _account.Identity;
        var stats = identity.ClientStats;
        var text = new Readout(OverviewText);

        // Only the rows that prove ownership take the accent. Of this whole tab that is three: the
        // PUUID, whether Riot still has the account enabled, and which regions it has lived on.
        void Line(string label, string? value, bool key = false)
            => text.Pair(label, 22, value, key);

        Line("Riot ID", identity.GameName is null ? null : identity.GameName + "#" + identity.TagLine);
        Line("Region", RiotRegions.DisplayFor(_account.Region) + " (" + _account.Region + ")");
        Line("Level", identity.SummonerLevel?.ToString());
        Line("Riot account id", identity.RiotAccountId, key: true);

        text.Blank();
        Line("Solo queue", Describe(identity.SoloRank));
        Line("Flex queue", Describe(identity.FlexRank));
        Line("Peak tier", stats?.PeakTier);
        Line("Last season ended", stats?.PreviousSeasonPeak);

        text.Blank();
        Line("Riot Points", stats?.RiotPoints?.ToString("N0"));
        Line("Blue Essence", stats?.BlueEssence?.ToString("N0"));
        Line("Champions owned", stats?.ChampionsOwned?.ToString());
        Line("Skins owned", stats?.SkinsOwned?.ToString());
        Line("Honor level", stats?.HonorLevel?.ToString());

        if (stats?.Mastery is { } mastery)
        {
            Line("Mastery total", mastery.TotalPoints.ToString("N0") + " points across "
                                  + mastery.ChampionsPlayed + " champions");
            Line("Most played", string.Join(", ", mastery.Top.Take(3)
                .Select(m => _services.Catalogue.ChampionName(m.ChampionId)
                             + " (" + m.Points.ToString("N0") + ")")));
        }

        if (stats is { SeasonRewards.Count: > 0 })
        {
            Line("Season rewards", string.Join(", ", stats.SeasonRewards
                .Select(r => "S" + r.SeasonId + (r.Level > 0 ? " lvl " + r.Level : ""))));
        }

        if (identity.Observed is { } observed)
        {
            text.Blank();
            Line("Account state", observed.AccountState, key: true);
            Line("Regions seen", observed.Regions.Count == 0
                ? null
                : string.Join(", ", observed.Regions.Select(r => r.ToString())), key: true);
        }

        text.Blank();
        if (_account.Session is { } session)
        {
            Line("Saved session", session.IsProbablyUsable(DateTimeOffset.UtcNow)
                ? "usable — next sign-in is instant"
                : "expired — next sign-in types the password");
        }
        else
        {
            Line("Saved session", "none yet");
        }

        text.Done();

        // These come from the client, which can only be asked about the account it is signed into.
        // Saying when they were taken keeps the window honest about what it is showing.
        OverviewAsOf.Text = stats is null
            ? "Rank, wallet and collection are read from the League client when you sign in. Sign in once to fill this in."
            : "Read from the League client on " + stats.CapturedUtc.LocalDateTime.ToString("d MMM yyyy 'at' HH:mm")
              + ". These are a snapshot from that sign-in, not live figures.";
    }

    private static string? Describe(RankInfo? rank)
    {
        if (rank is null || !rank.IsRanked) return null;

        var text = rank.ToString();
        if (rank.WinRate is { } winRate)
            text += "   " + rank.Wins + "W/" + rank.Losses + "L  (" + winRate + "%)";

        return text;
    }

    private void RenderRecovery()
    {
        var recovery = _account.Recovery;
        var observed = _account.Identity.Observed;
        var text = new Readout(RecoveryText);

        void Row(string label, string? typed, string? seen, bool key = false)
            => text.Compare(label, 24, typed, 38, seen, key);

        Row("Registered email", recovery.Email, observed?.MaskedEmail, key: true);
        Row("Phone", recovery.PhoneNumber,
            observed?.MaskedPhone ?? (observed?.PhoneOnFile == true ? "on file" : null), key: true);
        Row("Two-factor", recovery.MfaEnabled ? "yes" : "no", observed?.MfaEnabled == true ? "enabled" : null);
        Row("Created", recovery.ApproximateCreated?.ToString("d MMM yyyy"),
            observed?.CreatedUtc?.UtcDateTime.ToString("d MMM yyyy"), key: true);
        Row("Country", null, observed?.Country);

        // Read straight from the client, and exactly what a Riot Support ticket asks for.
        text.Blank();
        text.Plain("From the client — the answers a recovery form wants:", "Ink");

        // This block IS the recovery form, so most of it is key material.
        void Fact(string label, string? value, bool key = true)
            => text.Pair("  " + label, 26, value, key);

        Fact("Account created", observed?.CreatedUtc?.UtcDateTime.ToString("d MMM yyyy"));
        Fact("First champion bought", DescribeFirstChampion());
        Fact("Password last changed", observed?.PasswordChangedUtc?.UtcDateTime.ToString("d MMM yyyy"));
        Fact("Legacy login username", observed?.LegacyUsername);
        Fact("Original region", observed?.OriginalPlatform);
        Fact("Regions seen", observed is null || observed.Regions.Count == 0
            ? null
            : string.Join(", ", observed.Regions.Select(r => r.ToString())), key: false);
        Fact("Legacy account id", observed?.LegacyAccountId);
        Fact("Riot account id", _account.Identity.RiotAccountId);
        Fact("Account state", observed?.AccountState);

        // Previous Riot IDs. Riot's own `summoner_name` field has been empty since Riot IDs replaced
        // summoner names, so what used to sit here was a permanently blank row; the names this app
        // has actually watched the account use are worth far more on a ticket.
        if (observed is { Aliases.Count: > 0 })
        {
            // Riot's own record, with the date each name was taken - strictly better than the names
            // this app happened to observe, which only start when it was first used here.
            text.Blank();
            text.Plain("Riot IDs, as Riot records them:", "Ink");
            foreach (var alias in observed.Aliases) text.Plain("  " + alias);
        }
        else
        {
            var previous = _account.Identity.NameHistory
                .Select(entry => entry.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Fact("Riot IDs seen", previous.Count == 0 ? null : string.Join(", ", previous));
        }

        RenderProvenance(text);

        if (observed?.MustResetPassword == true)
            text.Warn("  NOTE: Riot is requiring a password reset on this account.");

        if (observed?.EmailLooksConsistentWith(recovery.Email) == false)
        {
            text.Blank();
            text.Warn("WARNING: the email you saved does not match the one Riot has for this");
            text.Warn("account (" + observed.MaskedEmail + "). One of them belongs to a different account.");
        }

        text.Blank();
        text.Plain("Typed only — nothing local can supply these:", "Ink");
        text.Pair("  Earliest purchase ref", 26, recovery.FirstPurchaseReference, key: true);
        text.Pair("  2FA backup codes", 26, recovery.MfaBackupCodes.Count + " stored");

        text.Done();
    }

    /// <summary>
    /// What happened to this account and when, from the dates attached to what it owns.
    ///
    /// </summary>
    private void RenderProvenance(Readout text)
    {
        var identity = _account.Identity;

        text.Blank();
        text.Plain("Provenance:", "Ink");

        if (Provenance.FirstSkin(_account) is { } first)
        {
            var name = _services.Catalogue.Skin(first.SkinId)?.Name ?? ("skin " + first.SkinId);
            text.Plain("  First skin acquired     " + name
                            + "   (" + first.AcquiredUtc.UtcDateTime.ToString("d MMM yyyy") + ")");
        }
        else
        {
            text.Plain("  First skin acquired     - (sign in once to capture acquisition dates)");
        }

        var legacy = identity.OwnedSkinIds
            .Where(id => !SkinCatalogue.IsJadeSkin(id))
            .Count(id => _services.Catalogue.Skin(id)?.IsLegacy == true);

        if (legacy > 0)
        {
            text.Plain("  No longer obtainable    " + legacy
                            + " skins   (stronger age evidence than any claim)");
        }

        text.Blank();
        text.Plain("Can anyone else recover this account?", "Ink");
        foreach (var check in Provenance.RecoveryExposure(_account))
            // A failed check is the whole reason this section exists, so it reads as one.
            if (check.Passed) text.Plain("  [yes] " + check.Title);
            else text.Warn("  [ NO ] " + check.Title);
    }

    /// <summary>
    /// The first champion ever bought, named and dated.
    ///
    /// Read from the purchase dates the client attaches to every owned champion, so it needs no
    /// remembering — which matters, because it is the ownership question hardest to answer honestly
    /// about an account bought or made a decade ago.
    /// </summary>
    private string? DescribeFirstChampion()
    {
        var stats = _account.Identity.ClientStats;
        if (stats?.FirstChampionId is not { } championId) return null;

        var name = _services.Catalogue.ChampionName(championId);
        var when = stats.FirstChampionPurchasedUtc?.LocalDateTime.ToString("d MMM yyyy");

        return when is null ? name : name + "   (" + when + ")";
    }

    // ---- collection --------------------------------------------------------

    private void RenderCollection()
    {
        var catalogue = _services.Catalogue;
        var identity = _account.Identity;

        // Jade duplicates are excluded: 234 of them carry no name in the catalogue at all, so they
        // would render as bare id numbers padding out a collection that already lists the real skin.
        _allSkins = catalogue.Resolve(identity.OwnedSkinIds.Where(id => !SkinCatalogue.IsJadeSkin(id)))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => new SkinRow(s, catalogue))
            .ToList();

        ApplySkinFilter();

        // Distinct because the game data carries Jade_* duplicates that resolve to the same champion,
        // and because any id the catalogue cannot name collapses to one shared label — without this a
        // handful of unknowns would render as a wall of identical chips.
        var champions = identity.OwnedChampionIds
            .Select(catalogue.ChampionName)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        ChampionList.ItemsSource = champions;

        ChampionCountText.Text = champions.Count == 0
            ? "No champions recorded yet — sign in once with League running."
            : champions.Count + " champions owned";
    }

    // ---- loot --------------------------------------------------------------

    private void RenderLoot()
    {
        var loot = _account.Identity.Loot;

        if (loot is null || loot.IsEmpty)
        {
            LootList.ItemsSource = null;
            LootSummaryText.Text = "No loot recorded yet — sign in once with League running.";
            return;
        }

        var groups = loot.Grouped().Select(group => new LootGroupRow(group)).ToList();
        LootList.ItemsSource = groups;

        var expiring = loot.Items.Count(item => item.IsExpiring);

        LootSummaryText.Text =
            "Read from the League client on "
            + loot.CapturedUtc.LocalDateTime.ToString("d MMM yyyy 'at' HH:mm")
            + ". " + loot.Items.Count + " entries."
            + (expiring > 0 ? "  " + expiring + " of them expire." : string.Empty);

        _ = LoadLootTilesAsync(groups.SelectMany(g => g.Items).ToList());
    }

    private async Task LoadLootTilesAsync(IReadOnlyList<LootRow> rows)
    {
        foreach (var row in rows)
        {
            try { await row.LoadTileAsync(_services.SkinArt); }
            catch (Exception) { /* art is decoration; a failure must not break the list */ }
        }
    }

    private void OnSkinSearchChanged(object sender, TextChangedEventArgs e) => ApplySkinFilter();

    private void ApplySkinFilter()
    {
        var query = SkinSearch.Text?.Trim() ?? string.Empty;

        var matches = string.IsNullOrEmpty(query)
            ? _allSkins
            : _allSkins.Where(s =>
                s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || s.ChampionName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        SkinList.ItemsSource = SkinRowGroup.Chunk(matches, SkinsPerRow);

        SkinCountText.Text = _allSkins.Count == 0
            ? "No skins recorded yet — sign in once with League running."
            : matches.Count == _allSkins.Count
                ? _allSkins.Count + " skins"
                : matches.Count + " of " + _allSkins.Count + " skins";

        // Tiles for the first screenful, so the tab is not empty while scrolling starts. The rest
        // arrive as their rows are realised.
        _ = LoadTilesAsync(matches.Take(SkinsPerRow * 4).ToList());
    }

    private async Task LoadTilesAsync(IReadOnlyList<SkinRow> rows)
    {
        foreach (var row in rows)
        {
            try { await row.LoadTileAsync(_services.SkinArt); }
            catch (Exception) { /* art is decoration; a failure must not break the list */ }
        }
    }

    // ---- actions -----------------------------------------------------------

    private void OnSignIn(object sender, RoutedEventArgs e)
    {
        Close();
        SignInRequested?.Invoke();
    }

    /// <summary>
    /// Re-reads everything from the running client, without a sign-in.
    ///
    /// Worth having on its own merits — the client is often already signed into the account you are
    /// looking at — but it is also the only way an account picks up a field added after its last
    /// sign-in, or sheds a value an older build got wrong.
    /// </summary>
    private async void OnRefreshFromClient(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        StatusText.Text = "Reading from the client…";

        try
        {
            if (await ClientIsSignedIntoThisAccountAsync() is { } refusal)
            {
                StatusText.Text = refusal;
                return;
            }

            var now = DateTimeOffset.UtcNow;

            if (await _services.AccountProbe.TryReadAsync(CancellationToken.None) is { } facts)
                facts.ApplyTo(_account, now);

            if (await _services.StatsProbe.TryReadAsync(CancellationToken.None) is { } stats)
                stats.ApplyTo(_account);

            if (await _services.LootProbe.TryReadAsync(CancellationToken.None) is { } loot && !loot.IsEmpty)
                _account.Identity.Loot = loot;

            _tile.Refresh();
            RenderOverview();
            RenderRecovery();
            RenderCollection();
            RenderLoot();

            StatusText.Text = "Refreshed from the client at " + DateTime.Now.ToString("HH:mm") + ".";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not read from the client (" + ex.GetType().Name + ").";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Refuses unless the client is signed into <em>this</em> account, returning the reason if not.
    /// The decision itself lives on <see cref="AccountEntry.RefreshRefusalReason"/> so it can be
    /// tested without a window.
    /// </summary>
    private async Task<string?> ClientIsSignedIntoThisAccountAsync()
    {
        // Skip asking the client at all when the account could not be matched anyway.
        if (string.IsNullOrWhiteSpace(_account.Identity.RiotAccountId))
            return _account.RefreshRefusalReason(null, null);

        var reading = await _services.Identity.TryReadAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        return _account.RefreshRefusalReason(reading?.Puuid, reading?.GameName);
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        var editor = new AccountEditorWindow(_account, isNew: false) { Owner = this };
        if (editor.ShowDialog() != true) return;

        _tile.Refresh();
        RenderOverview();
        RenderRecovery();
    }
}
