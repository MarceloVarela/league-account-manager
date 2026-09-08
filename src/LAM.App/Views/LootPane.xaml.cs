using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LAM.App.Services;
using LAM.Core.Model;
using LAM.Core.Riot;

namespace LAM.App.Views;

/// <summary>
/// Loot across every account, not just the one that happens to be signed in.
///
/// Deliberately read-only. The client's loot endpoints can disenchant, reroll and redeem, and none of
/// that is wired up here or anywhere else: a mis-selected row would destroy a skin shard permanently,
/// and no amount of confirmation dialog makes that a risk worth carrying for a convenience.
/// </summary>
public partial class LootPane : UserControl
{
    private readonly IReadOnlyList<AccountEntry> _accounts;
    private readonly AppServices _services;

    public LootPane(IReadOnlyList<AccountEntry> accounts, AppServices services)
    {
        InitializeComponent();

        _accounts = accounts;
        _services = services;

        Loaded += (_, _) => Render();
    }

    private void Render()
    {
        // Every loot item, tagged with the account it came from — the join that makes this view
        // possible at all, and that no single-account tool can perform.
        var rows = _accounts
            .Where(a => !a.IsDeleted && a.Identity.Loot is { IsEmpty: false })
            .SelectMany(a => a.Identity.Loot!.Items.Select(item => new LootRowView(item, a.DisplayRiotId)))
            .OrderByDescending(r => r.SortWeight)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Rows.ItemsSource = rows;

        var captured = _accounts.Count(a => !a.IsDeleted && a.Identity.Loot is { IsEmpty: false });

        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = captured == 0
            ? "NO LOOT — sign in to an account with League running and it is captured automatically."
            : string.Empty;

        Plates.ItemsSource = BuildPlates(captured);
    }

    /// <summary>
    /// The four figures across the top.
    ///
    /// Currencies are summed rather than counted: twelve accounts each holding blue essence is one
    /// number worth knowing, where "12 currency entries" is not.
    /// </summary>
    private IReadOnlyList<StatPlate> BuildPlates(int captured)
    {
        var items = _accounts
            .Where(a => !a.IsDeleted && a.Identity.Loot is { IsEmpty: false })
            .SelectMany(a => a.Identity.Loot!.Items)
            .ToList();

        var shards = items.Where(i => !i.IsCurrency).Sum(i => i.Count);
        var disenchant = items.Where(i => !i.IsCurrency).Sum(i => (long)(i.DisenchantValue ?? 0) * i.Count);
        var orange = items.Where(i => i.LootId == "CURRENCY_cosmetic").Sum(i => i.Count);
        var expiring = items.Count(i => i.IsExpiring);

        return
        [
            new StatPlate("SHARDS HELD", shards.ToString("N0"),
                captured + (captured == 1 ? " account read" : " accounts read")),

            new StatPlate("WOULD DISENCHANT FOR", disenchant.ToString("N0"),
                "essence, if you ever did — this app will not"),

            new StatPlate("ORANGE ESSENCE", orange.ToString("N0"), "across every account"),

            new StatPlate("EXPIRING", expiring.ToString("N0"),
                expiring == 0 ? "nothing on a timer" : "these disappear if left alone"),
        ];
    }
}

public sealed record StatPlate(string Label, string Figure, string Note);

/// <summary>One loot row, flattened across accounts.</summary>
public sealed class LootRowView
{
    public LootRowView(LootItem item, string account)
    {
        Name = item.Name;
        Account = account;
        Category = (item.Category ?? item.Type).ToUpperInvariant();
        Quantity = item.IsCurrency ? item.Count.ToString("N0") : "x" + item.Count;
        Rarity = item.Rarity is null or "DEFAULT" ? string.Empty : item.Rarity.ToUpperInvariant();

        Essence = item.DisenchantValue is > 0
            ? (item.DisenchantValue!.Value * item.Count).ToString("N0")
            : "—";

        // Expiring first, then the valuable, then everything else: the order in which they actually
        // demand attention.
        SortWeight = (item.IsExpiring ? 1_000_000 : 0) + (item.DisenchantValue ?? 0) * item.Count;
    }

    public string Name { get; }
    public string Account { get; }
    public string Category { get; }
    public string Quantity { get; }
    public string Rarity { get; }
    public string Essence { get; }
    public long SortWeight { get; }
}
