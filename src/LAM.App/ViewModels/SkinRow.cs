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

/// <summary>One skin in the collection view.</summary>
public sealed class SkinRow : INotifyPropertyChanged
{
    private ImageBrush? _tile;

    public SkinRow(CatalogueSkin skin, SkinCatalogue catalogue)
    {
        Skin = skin;
        ChampionName = catalogue.ChampionName(skin.ChampionId);
    }

    public CatalogueSkin Skin { get; }

    public string Name => Skin.Name;

    public string ChampionName { get; }

    public string RarityText => string.IsNullOrWhiteSpace(Skin.Rarity) ? string.Empty : Skin.Rarity!;

    public bool HasRarity => !string.IsNullOrEmpty(RarityText);

    public string ReleasedText => Skin.Released?.ToString("MMM yyyy") ?? string.Empty;

    public ImageBrush? Tile
    {
        get => _tile;
        private set { _tile = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasTile)); }
    }

    public bool HasTile => _tile is not null;

    /// <summary>
    /// Loads the tile from disk if it is cached, and fetches it otherwise.
    ///
    /// Called as rows scroll into view rather than up front: an account here owns over fifteen
    /// hundred skins, and downloading all of them for a screen that may never be scrolled would be
    /// indefensible.
    /// </summary>
    public async System.Threading.Tasks.Task LoadTileAsync(SkinArtCache art)
    {
        if (_tile is not null) return;

        var path = art.CachedPath(Skin.Id)
                   ?? await art.GetTileAsync(Skin, System.Threading.CancellationToken.None);

        if (path is null) return;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            // OnLoad so the file is not held open, and a modest decode size because these are
            // thumbnails — decoding full splash art fifteen hundred times would exhaust memory.
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

/// <summary>
/// A row of skins, so a wrapping grid can still virtualize.
///
/// WPF's <c>WrapPanel</c> does not virtualize, and rendering fifteen hundred tiles eagerly hangs the
/// window. Grouping items into fixed-width rows lets the built-in <c>VirtualizingStackPanel</c> do
/// its job over the rows, which gives the grid appearance while only realising what is on screen.
/// </summary>
public sealed class SkinRowGroup
{
    public SkinRowGroup(IReadOnlyList<SkinRow> items) => Items = items;

    public IReadOnlyList<SkinRow> Items { get; }

    /// <summary>Splits a sequence into rows of <paramref name="perRow"/>, keeping order.</summary>
    public static IReadOnlyList<SkinRowGroup> Chunk(IReadOnlyList<SkinRow> skins, int perRow)
        => LAM.Core.Model.Chunking.IntoRows(skins, perRow)
            .Select(row => new SkinRowGroup(row))
            .ToList();
}
