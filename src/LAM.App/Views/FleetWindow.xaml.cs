using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using LAM.App.Services;
using LAM.Core.Fleet;
using LAM.Core.Model;
using LAM.Core.Riot;

namespace LAM.App.Views;

/// <summary>
/// The whole collection at once — what is owned where, what it adds up to, and what is rotting.
///
/// These are the questions a per-account view cannot answer and no competing tool can either: every
/// companion app attaches to whichever client is running, so it only ever sees one account.
/// </summary>
public partial class FleetWindow : Window
{
    private readonly IReadOnlyList<AccountEntry> _accounts;
    private readonly AppServices _services;

    public FleetWindow(IReadOnlyList<AccountEntry> accounts, AppServices services)
    {
        InitializeComponent();

        _accounts = accounts;
        _services = services;

        Loaded += (_, _) => Render();
    }

    private void Render()
    {
        var now = DateTimeOffset.UtcNow;
        var withCollections = _accounts.Count(FleetAnalysis.HasCollection);

        HeadlineText.Text = _accounts.Count + " accounts, " + withCollections + " with a captured collection.";

        if (!_services.Catalogue.IsLoaded)
        {
            StatusText.Text = "No skin catalogue yet — open an account and sign in once with League running.";
        }

        RenderPortfolio(now);
        RenderRepairState();
        RenderHealth(now);
    }

    // ---- find a skin -------------------------------------------------------

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var query = SkinSearch.Text?.Trim() ?? string.Empty;

        if (query.Length < 2)
        {
            ResultsList.ItemsSource = null;
            StatusText.Text = query.Length == 0 ? string.Empty : "Keep typing…";
            return;
        }

        var results = FleetAnalysis.Search(_accounts, _services.Catalogue, query, limit: 60);

        ResultsList.ItemsSource = results
            .Select(r => new OwnershipRow(r, _services.Catalogue))
            .ToList();

        StatusText.Text = results.Count switch
        {
            0 when !_services.Catalogue.IsLoaded => "No catalogue loaded yet, so there is nothing to search.",
            0 => "No skin matches \"" + query + "\".",
            _ => results.Count + " matching skins.",
        };
    }

    // ---- portfolio ---------------------------------------------------------

    private void RenderPortfolio(DateTimeOffset now)
    {
        var portfolio = FleetAnalysis.Summarise(_accounts, _services.Catalogue, now);
        var text = new StringBuilder();

        text.AppendLine("ACROSS ALL ACCOUNTS");
        text.AppendLine("  Accounts with a collection   " + portfolio.AccountsWithCollections + " of " + _accounts.Count);
        text.AppendLine("  Skins owned (with repeats)   " + portfolio.TotalSkins.ToString("N0"));
        text.AppendLine("  Distinct skins owned         " + portfolio.DistinctSkinsOwned.ToString("N0")
                        + "   (how much of the game you have access to)");
        text.AppendLine("  Of those, no longer sold     " + portfolio.TotalLegacy.ToString("N0"));
        text.AppendLine("  Priced at, in total          " + portfolio.TotalKnownRp.ToString("N0") + " RP");
        text.AppendLine();

        text.AppendLine("PER ACCOUNT");
        text.AppendLine("  " + "Account".PadRight(24) + "Skins".PadLeft(7) + "Champs".PadLeft(8)
                        + "RP at listed prices".PadLeft(22) + "   Last used");

        foreach (var row in portfolio.Rows)
        {
            text.AppendLine("  "
                            + Trim(row.Account.DisplayRiotId, 24).PadRight(24)
                            + row.Value.TotalSkins.ToString("N0").PadLeft(7)
                            + row.Champions.ToString("N0").PadLeft(8)
                            + row.Value.KnownRp.ToString("N0").PadLeft(22)
                            + "   " + row.Dormancy.Describe());
        }

        var unpriced = portfolio.Rows.Sum(r => r.Value.UnpricedSkins);
        if (unpriced > 0)
        {
            text.AppendLine();
            text.AppendLine(unpriced.ToString("N0") + " skins have no listed price and are left out of the RP");
            text.AppendLine("totals — almost all of them are skins Riot no longer sells. They are counted");
            text.AppendLine("as owned, just not priced; inventing a number for them would make the total");
            text.AppendLine("look precise while being fiction.");
        }

        PortfolioText.Text = text.ToString();
    }

    // ---- health ------------------------------------------------------------

    private void RenderHealth(DateTimeOffset now)
    {
        var text = new StringBuilder();

        text.AppendLine("NEXT SIGN-IN");
        foreach (var standing in FleetAnalysis.SessionStandings(_accounts, now))
        {
            text.AppendLine("  " + Trim(standing.Account.DisplayRiotId, 26).PadRight(26) + standing.Describe());
        }

        var stale = _accounts
            .Select(a => (Account: a, Dormancy: FleetAnalysis.DormancyOf(a, now)))
            .Where(x => x.Dormancy.Level is DormancyLevel.Stale)
            .OrderByDescending(x => x.Dormancy.DaysSinceUsed)
            .ToList();

        text.AppendLine();
        text.AppendLine("NOT USED IN A LONG TIME");

        if (stale.Count == 0)
        {
            text.AppendLine("  Nothing has been left alone long enough to worry about.");
        }
        else
        {
            // Decay is the failure mode of owning many accounts: it is silent, it only touches the
            // ones you are not looking at, and by the time it shows the LP has already gone.
            foreach (var (account, dormancy) in stale)
                text.AppendLine("  " + Trim(account.DisplayRiotId, 26).PadRight(26) + dormancy.Describe());
        }

        var noCollection = _accounts.Where(a => !FleetAnalysis.HasCollection(a)).ToList();
        if (noCollection.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("NOTHING CAPTURED YET");
            text.AppendLine("  These are excluded from every figure above, because an account that has");
            text.AppendLine("  never been signed into cannot say what it owns:");
            foreach (var account in noCollection)
                text.AppendLine("    " + account.DisplayRiotId);
        }

        HealthText.Text = text.ToString();
    }

    // ---- tools -------------------------------------------------------------

    private void RenderRepairState()
    {
        var stale = _services.Repair.StaleLockfiles();
        var running = _services.RiotPaths is null ? [] : _services.Processes.DescribeRunningClients();

        var lines = new List<string>
        {
            "Clients running     " + (running.Count == 0 ? "none" : string.Join(", ", running)),
            "Stale lockfiles     " + (stale.Count == 0 ? "none" : stale.Count + " found"),
        };

        if (stale.Count > 0)
        {
            // Worth spelling out, because this is the failure that makes the app itself look broken:
            // every probe reads the lockfile for the client's port, so a leftover one points every
            // request at a port nothing is listening on.
            lines.Add(string.Empty);
            lines.Add("A stale lockfile names a port nothing is listening on, so reads fail in a");
            lines.Add("way that looks like the client refusing to answer.");
        }

        RepairStateText.Text = string.Join(Environment.NewLine, lines);
        FolderList.ItemsSource = _services.Repair.UsefulFolders()
            .Select(f => new { f.Name, f.Path })
            .ToList();
    }

    private async void OnCloseClients(object sender, RoutedEventArgs e)
        => Report(await _services.Repair.StopClientsAsync(CancellationToken.None));

    private void OnClearLockfiles(object sender, RoutedEventArgs e)
        => Report(_services.Repair.ClearStaleLockfiles());

    private void OnClearCache(object sender, RoutedEventArgs e)
        => Report(_services.Repair.ClearBrowserCache());

    private void Report(RepairResult result)
    {
        StatusText.Text = (result.Succeeded ? "" : "Could not: ") + result.Action + " - " + result.Detail;
        RenderRepairState();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path }) return;

        try
        {
            if (!Directory.Exists(path))
            {
                StatusText.Text = "Not there: " + path;
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception
                                       or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not open " + path;
        }
    }

    /// <summary>
    /// Writes the fleet to CSV. Never includes a password, a session or a recovery secret.
    /// </summary>
    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "league-accounts.csv",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            Title = "Export fleet",
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var csv = FleetExport.ToCsv(_accounts, _services.Catalogue, DateTimeOffset.UtcNow);
            File.WriteAllText(dialog.FileName, csv, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            StatusText.Text = "Exported " + _accounts.Count(a => !a.IsDeleted) + " accounts to " + dialog.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not write that file (" + ex.GetType().Name + ").";
        }
    }

    private static string Trim(string value, int width)
        => value.Length <= width ? value : value[..(width - 1)] + "…";
}

/// <summary>One search result: a skin, who has it, and who does not.</summary>
public sealed class OwnershipRow
{
    public OwnershipRow(SkinOwnership ownership, SkinCatalogue catalogue)
    {
        Name = ownership.Skin.Name;
        IsLegacy = ownership.Skin.IsLegacy;

        var champion = catalogue.ChampionName(ownership.Skin.ChampionId);
        ChampionAndPrice = ownership.Skin.RpCost is { } rp
            ? champion + " · " + rp.ToString("N0") + " RP"
            : champion + " · no longer sold";

        OwnersText = ownership.Owners.Count == 0
            ? "Owned by: nobody"
            : "Owned by: " + string.Join(", ", ownership.Owners.Select(a => a.DisplayRiotId));

        WithoutText = ownership.Without.Count == 0
            ? "Every account has it."
            : "Missing from: " + string.Join(", ", ownership.Without.Select(a => a.DisplayRiotId));
    }

    public string Name { get; }
    public string ChampionAndPrice { get; }
    public bool IsLegacy { get; }
    public string OwnersText { get; }
    public string WithoutText { get; }
}
