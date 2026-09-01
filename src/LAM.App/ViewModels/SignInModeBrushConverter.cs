using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LAM.App.ViewModels;

/// <summary>
/// Colours the sign-in mode badge: green when the account will sign in from a saved session, muted
/// when it will type the password.
///
/// Worth a visual distinction rather than just wording. "Instant" and "Types password" mean very
/// different things to the person about to click — one of them is a two-second window where touching
/// the keyboard aborts the login — and that should be readable at a glance across a grid.
/// </summary>
public sealed class SignInModeBrushConverter : IValueConverter
{
    private static readonly Brush InstantBackground = Frozen("#332ECC71");
    private static readonly Brush InstantForeground = Frozen("#FF4FBF7B");
    private static readonly Brush TypingBackground = Frozen("#FF2C313C");
    private static readonly Brush TypingForeground = Frozen("#FF9AA1B1");

    /// <summary>True for the badge's fill, false for its text.</summary>
    public bool Background { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var instant = value is true;

        return Background
            ? (instant ? InstantBackground : TypingBackground)
            : (instant ? InstantForeground : TypingForeground);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();   // shared across every card, so it must be immutable
        return brush;
    }
}
