using System;
using System.Linq;
using System.Windows;

namespace LAM.App.Services;

public enum AppTheme { Light, Dark }

/// <summary>
/// Swaps the colour palette at runtime.
///
/// Only the palette dictionary is replaced — Theme.xaml and ShellChrome.xaml stay put, because they
/// hold structure rather than colour and look every token up with DynamicResource. That is what makes
/// the swap take effect on a live window instead of needing a restart.
/// </summary>
public static class ThemeSwitcher
{
    private const string LightSource = "Themes/Light.xaml";
    private const string DarkSource = "Themes/Dark.xaml";

    /// <summary>Dark is the design's home state, so it is also the starting assumption.</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static void Apply(AppTheme theme)
    {
        var dictionaries = Application.Current?.Resources.MergedDictionaries;
        if (dictionaries is null) return;

        var wanted = theme == AppTheme.Dark ? DarkSource : LightSource;

        var existing = dictionaries.FirstOrDefault(d =>
            d.Source is not null &&
            (d.Source.OriginalString.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
             d.Source.OriginalString.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase)));

        if (existing?.Source?.OriginalString.EndsWith(
                theme == AppTheme.Dark ? "Dark.xaml" : "Light.xaml",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            Current = theme;
            return;
        }

        var replacement = new ResourceDictionary
        {
            Source = new Uri(wanted, UriKind.Relative),
        };

        // Insert at the old index rather than appending: the palette must stay ahead of Theme.xaml,
        // whose own keyed styles would otherwise be shadowed by a later dictionary.
        if (existing is not null)
        {
            var index = dictionaries.IndexOf(existing);
            dictionaries.RemoveAt(index);
            dictionaries.Insert(index, replacement);
        }
        else
        {
            dictionaries.Insert(0, replacement);
        }

        Current = theme;
    }

    public static void Toggle() => Apply(Current == AppTheme.Light ? AppTheme.Dark : AppTheme.Light);
}
