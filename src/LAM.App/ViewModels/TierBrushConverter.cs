using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace LAM.App.ViewModels;

/// <summary>
/// Turns a rank string into its tier colour.
///
/// The handoff makes rank the one place colour carries meaning: chrome stays blue, and each account's
/// tier drives three things at once — the rank text, the 3px bar across the top of its card, and the
/// fill of its LP rule. That is what makes a grid of nine accounts read as nine accounts instead of
/// one blue wall.
///
/// A converter rather than a per-card property, for two reasons the previous hard-coded table got
/// wrong. It resolves through <c>Application.Current.TryFindResource</c>, so the brushes come from
/// whichever palette is live and the colours re-resolve on a theme swap — the light values are deep
/// steps, not the dark ones dimmed, because dark Challenger cyan measures about 1.9:1 on a light
/// plate. And it matches on the tier word at the START of the string, so "Emerald II", "Emerald" and
/// the abbreviated "Emer I" that Fleet rows render all land on the same brush.
/// </summary>
public sealed class TierBrushConverter : IValueConverter
{
    /// <summary>
    /// Prefixes, longest first.
    ///
    /// Order matters: "Grandmaster" has to be tested before "Master", or every Grandmaster would
    /// match the shorter word sitting inside it and paint purple.
    /// </summary>
    private static readonly (string Prefix, string Key)[] Tiers =
    [
        ("GRANDMASTER", "TierGrandmaster"),
        ("CHALLENGER", "TierChallenger"),
        ("ASCENDANT", "TierAscendant"),
        ("PLATINUM", "TierPlatinum"),
        ("IMMORTAL", "TierImmortal"),
        ("DIAMOND", "TierDiamond"),
        ("EMERALD", "TierEmerald"),
        ("RADIANT", "TierRadiant"),
        ("MASTER", "TierMaster"),
        ("BRONZE", "TierBronze"),
        ("SILVER", "TierSilver"),
        ("IRON", "TierIron"),
        ("GOLD", "TierGold"),

        // The abbreviations the Fleet rows and some client payloads use.
        ("PLAT", "TierPlatinum"),
        ("EMER", "TierEmerald"),
        ("DIA", "TierDiamond"),
        ("GM", "TierGrandmaster"),
    ];

    public static string KeyFor(string? rank)
    {
        if (string.IsNullOrWhiteSpace(rank)) return "TierUnranked";

        var text = rank.TrimStart();

        foreach (var (prefix, key) in Tiers)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return key;
        }

        return "TierUnranked";
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var brush = Application.Current?.TryFindResource(KeyFor(value as string)) as Brush;

        // An unresolved key means the palette is mid-swap or a tier was added to the converter and not
        // to the dictionaries. Transparent would silently erase the bar; muted is visibly wrong instead.
        return brush
               ?? Application.Current?.TryFindResource("MutedText") as Brush
               ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("Tier colour is display-only.");
}
