using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LAM.Core.Riot;

namespace LAM.App.ViewModels;

/// <summary>One loot entry — a shard, a chest, or a pile of essence.</summary>
public sealed class LootRow : INotifyPropertyChanged
{
    private ImageBrush? _tile;

    public LootRow(LootItem item) => Item = item;

    public LootItem Item { get; }

    public string Name => Item.Name;

    /// <summary>Shown as "×3"; a single copy needs no multiplier, but essence is all about the number.</summary>
    public string CountText => Item.IsCurrency
        ? Item.Count.ToString("N0")
        : Item.Count > 1 ? "×" + Item.Count : string.Empty;

    public bool HasCount => CountText.Length > 0;

    public string RarityText => string.IsNullOrWhiteSpace(Item.Rarity) || Item.Rarity == "DEFAULT"
        ? string.Empty
        : char.ToUpperInvariant(Item.Rarity![0]) + Item.Rarity[1..].ToLowerInvariant();

    public bool HasRarity => RarityText.Length > 0;

    /// <summary>What disenchanting would return, which is the number that decides whether to keep it.</summary>
    public string DisenchantText => Item.DisenchantValue is > 0
        ? "Disenchants for " + Item.DisenchantValue!.Value.ToString("N0")
        : string.Empty;

    public bool HasDisenchant => DisenchantText.Length > 0;

    public string ExpiryText => Item.ExpiresUtc is { } expires
        ? "Expires " + expires.LocalDateTime.ToString("d MMM yyyy")
        : string.Empty;

    public bool IsExpiring => Item.ExpiresUtc is not null;

    public ImageBrush? Tile
    {
        get => _tile;
        private set { _tile = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasTile)); }
    }

    public bool HasTile => _tile is not null;

    /// <summary>Currencies have no meaningful art, so they render as a plain count instead.</summary>
    public bool WantsTile => !Item.IsCurrency && !string.IsNullOrWhiteSpace(Item.TilePath);

    public async System.Threading.Tasks.Task LoadTileAsync(SkinArtCache art)
    {
        if (_tile is not null || !WantsTile) return;

        var path = art.CachedPath(Item.LootId)
                   ?? await art.GetAssetAsync(Item.LootId, Item.TilePath, System.Threading.CancellationToken.None);

        if (path is null) return;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.EndInit();
            image.Freeze();

            var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            brush.Freeze();

            Tile = brush;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            // A corrupt or half-written file leaves the placeholder in place.
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A named section of the loot view — "Skin shards", "Essence", and so on.</summary>
public sealed class LootGroupRow
{
    public LootGroupRow(LootGroup group)
    {
        Title = group.Title;
        Items = [.. group.Items.Select(item => new LootRow(item))];
    }

    public string Title { get; }

    public IReadOnlyList<LootRow> Items { get; }

    public string Summary => Items.Count == 1 ? "1 entry" : Items.Count + " entries";
}
