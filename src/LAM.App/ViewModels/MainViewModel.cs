using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using LAM.Core.Model;
using LAM.Core.Riot;
using LAM.Core.Vault;

namespace LAM.App.ViewModels;

/// <summary>How the grid is ordered. Rank matters most with a pile of smurfs.</summary>
public enum AccountSort
{
    LastUsed,
    Rank,
    Level,
    Name,
    Region,
}

/// <summary>State for the main window: locked or not, the visible accounts, and what is happening.</summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    /// <summary>Tier order, worst to best, for sorting. Unranked sinks below Iron.</summary>
    private static readonly string[] TierOrder =
    [
        "UNRANKED", "IRON", "BRONZE", "SILVER", "GOLD", "PLATINUM",
        "EMERALD", "DIAMOND", "MASTER", "GRANDMASTER", "CHALLENGER",
    ];

    private static readonly string[] DivisionOrder = ["IV", "III", "II", "I"];

    private UnlockedVault? _vault;
    private string _searchText = string.Empty;
    private string _status = string.Empty;
    private bool _isBusy;
    private string _busyMessage = string.Empty;
    private string? _busyDetail;
    private bool _busyIsTyping;
    private AccountSort _sort = AccountSort.LastUsed;
    private string _regionFilter = AllRegions;
    private string? _signedInAccountId;

    public const string AllRegions = "All regions";

    public ObservableCollection<AccountTile> VisibleAccounts { get; } = [];

    public IReadOnlyList<AccountSort> SortOptions { get; } = Enum.GetValues<AccountSort>();

    public AccountSort Sort
    {
        get => _sort;
        set { if (_sort == value) return; _sort = value; OnPropertyChanged(); ApplyFilter(); }
    }

    public string RegionFilter
    {
        get => _regionFilter;
        set { if (_regionFilter == value) return; _regionFilter = value; OnPropertyChanged(); ApplyFilter(); }
    }

    /// <summary>Regions actually in use, so the filter never offers an empty choice.</summary>
    public IReadOnlyList<string> RegionOptions
    {
        get
        {
            if (_vault is null || _vault.IsLocked) return [AllRegions];

            var regions = _vault.Document.Live
                .Select(a => a.Region)
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase);

            return new[] { AllRegions }.Concat(regions).ToList();
        }
    }

    /// <summary>
    /// The account the client is signed into right now, by durable id.
    ///
    /// Worth showing prominently: with a dozen smurfs, "which one am I on?" is the first question
    /// every single time, and the client is the only thing that actually knows.
    /// </summary>
    public void SetSignedInAccount(string? riotAccountId)
    {
        if (_signedInAccountId == riotAccountId) return;
        _signedInAccountId = riotAccountId;
        MarkFlags();
    }

    public bool IsLocked => _vault is null || _vault.IsLocked;

    public bool IsUnlocked => !IsLocked;

    public UnlockedVault? Vault => _vault;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotBusy)); }
    }

    public bool IsNotBusy => !_isBusy;

    public string BusyMessage
    {
        get => _busyMessage;
        private set { _busyMessage = value; OnPropertyChanged(); }
    }

    public string? BusyDetail
    {
        get => _busyDetail;
        private set { _busyDetail = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasBusyDetail)); }
    }

    public bool HasBusyDetail => !string.IsNullOrWhiteSpace(_busyDetail);

    /// <summary>
    /// True while keystrokes are being sent. The overlay uses it to show the "don't touch anything"
    /// warning only when it is actually true — a warning shown during an instant login would train
    /// you to ignore it.
    /// </summary>
    public bool BusyIsTyping
    {
        get => _busyIsTyping;
        private set { _busyIsTyping = value; OnPropertyChanged(); }
    }

    public string AccountCountText
    {
        get
        {
            if (_vault is null || _vault.IsLocked) return string.Empty;
            var total = _vault.Document.Live.Count();
            var shown = VisibleAccounts.Count;
            return shown == total
                ? total + (total == 1 ? " account" : " accounts")
                : shown + " of " + total + " accounts";
        }
    }

    public void SetVault(UnlockedVault? vault)
    {
        _vault = vault;
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(IsUnlocked));
        ApplyFilter();
    }

    public void BeginBusy(string message, bool isTyping = false)
    {
        BusyMessage = message;
        BusyDetail = null;
        BusyIsTyping = isTyping;
        IsBusy = true;
    }

    public void UpdateBusy(string detail, bool isTyping)
    {
        BusyDetail = detail;
        BusyIsTyping = isTyping;
    }

    public void EndBusy(string? status = null)
    {
        IsBusy = false;
        BusyIsTyping = false;
        BusyDetail = null;
        if (status is not null) Status = status;
    }

    /// <summary>
    /// Rebuilds the visible list from the search box.
    ///
    /// Sorted most-recently-used first, with never-used accounts after them, because with a large
    /// collection the one you want next is nearly always one you used recently.
    /// </summary>
    public void ApplyFilter()
    {
        VisibleAccounts.Clear();

        if (_vault is null || _vault.IsLocked)
        {
            OnPropertyChanged(nameof(AccountCountText));
            return;
        }

        // Decided once per refresh: every card must agree, and asking the OS for a process list
        // per card would be wasteful for an answer that cannot differ between them.
        AccountTile.RedactNames = _vault.Settings.RedactWhileStreaming
                                  && LAM.Core.Model.StreamerMode.CaptureSoftwareRunning();

        var matches = _vault.Document.Search(_searchText);

        if (!string.Equals(_regionFilter, AllRegions, StringComparison.Ordinal))
            matches = matches.Where(a => string.Equals(a.Region, _regionFilter, StringComparison.OrdinalIgnoreCase));

        // Favourites first, then whatever sort is chosen. Pinning has to survive the sort or it is
        // not pinning — with dozens of accounts the two or three you actually play should never be
        // somewhere down a scroll.
        var ordered = Order(matches)
            .OrderByDescending(a => a.IsFavourite)
            .ToList();

        foreach (var account in ordered) VisibleAccounts.Add(new AccountTile(account));

        MarkFlags();
        OnPropertyChanged(nameof(AccountCountText));
        OnPropertyChanged(nameof(RegionOptions));
    }

    private IEnumerable<AccountEntry> Order(IEnumerable<AccountEntry> accounts) => _sort switch
    {
        // Highest rank first — the useful direction when picking an account to play.
        AccountSort.Rank => accounts
            .OrderByDescending(a => RankWeight(a.Identity.SoloRank))
            .ThenByDescending(a => a.Identity.SoloRank?.LeaguePoints ?? 0)
            .ThenBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase),

        AccountSort.Level => accounts
            .OrderByDescending(a => a.Identity.SummonerLevel ?? 0)
            .ThenBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase),

        AccountSort.Name => accounts
            .OrderBy(a => a.DisplayRiotId, StringComparer.CurrentCultureIgnoreCase),

        AccountSort.Region => accounts
            .OrderBy(a => a.Region, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase),

        // Most recently used first, with never-used last: with a large collection the one you want
        // next is nearly always one you used recently.
        _ => accounts
            .OrderByDescending(a => a.LastUsedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase),
    };

    /// <summary>Ranks as a single comparable number: tier first, then division within it.</summary>
    private static int RankWeight(RankInfo? rank)
    {
        if (rank is null || !rank.IsRanked) return 0;

        var tier = Array.FindIndex(TierOrder, t => string.Equals(t, rank.Tier, StringComparison.OrdinalIgnoreCase));
        if (tier < 0) tier = 0;

        var division = rank.Division is null
            ? 0
            : Math.Max(0, Array.FindIndex(DivisionOrder, d => string.Equals(d, rank.Division, StringComparison.OrdinalIgnoreCase)));

        return tier * 10 + division;
    }

    /// <summary>
    /// Marks which card is signed in, and which entries are the same account saved twice.
    ///
    /// Duplicates are matched on the durable account id rather than the label — two entries called
    /// "smurf" and "smurf2" pointing at one account is exactly the case that is invisible otherwise.
    /// </summary>
    private void MarkFlags()
    {
        var duplicates = VisibleAccounts
            .Select(t => t.Account.Identity.RiotAccountId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .GroupBy(id => id!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var tile in VisibleAccounts)
        {
            var id = tile.Account.Identity.RiotAccountId;

            tile.SetDuplicate(id is not null && duplicates.Contains(id));
            tile.SetSignedIn(id is not null
                             && _signedInAccountId is not null
                             && string.Equals(id, _signedInAccountId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void RefreshTile(AccountEntry account)
    {
        var tile = VisibleAccounts.FirstOrDefault(t => t.Account.Id == account.Id);
        tile?.Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
