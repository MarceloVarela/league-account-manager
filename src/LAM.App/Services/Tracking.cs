using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace LAM.App.Services;

/// <summary>
/// Letter-spacing for TextBlock, which WPF does not have.
///
/// The design puts tracking between .04em and .20em on condensed caps, and that tracking is most of
/// what makes them read as drafting labels rather than as compressed text.
///
/// Two hard rules came out of getting this wrong twice, and both are why this looks defensive:
///
///   1. NEVER touch a bound TextBlock. Writing Text or Inlines assigns a local value, which silently
///      destroys the binding — every nav tab count froze at 0 for exactly this reason, and with tiles
///      now reused across filters it would have left stale text on cards too. A bound TextBlock is
///      left alone: correct text without tracking beats tracked text that never updates.
///   2. NEVER apply it in a monospaced font. Monospace quantises EVERY glyph to one cell width, a
///      space included, so an inserted spacer becomes a full character gap and the label explodes
///      into "A C C - 0 1". The Mono styles no longer ask for tracking, and this checks anyway,
///      because the next person will not remember why.
///
/// The spacer is a Run whose font size is scaled so its advance lands near the requested em. That is
/// only possible because it is real content — which is precisely why it cannot coexist with a binding.
/// </summary>
public static class Tracking
{
    /// <summary>Roughly the width of a space relative to the em in Barlow and most UI faces.</summary>
    private const double SpaceRatio = 0.26;

    public static readonly DependencyProperty EmProperty = DependencyProperty.RegisterAttached(
        "Em", typeof(double), typeof(Tracking), new PropertyMetadata(0d, OnChanged));

    public static void SetEm(DependencyObject element, double value) => element.SetValue(EmProperty, value);
    public static double GetEm(DependencyObject element) => (double)element.GetValue(EmProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text || GetEm(text) <= 0) return;

        // Wait for the template's bindings and the inherited font to settle before deciding.
        text.Dispatcher.BeginInvoke(new Action(() => Apply(text)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void Apply(TextBlock text)
    {
        var em = GetEm(text);
        if (em <= 0) return;

        // Rule 1: the binding is worth more than the spacing.
        if (BindingOperations.GetBinding(text, TextBlock.TextProperty) is not null) return;

        // Rule 2: monospace cannot carry a sub-character spacer.
        if (IsMonospaced(text.FontFamily)) return;

        var value = text.Text;
        if (string.IsNullOrEmpty(value) || value.Length < 2) return;

        var size = Math.Max(0.1, text.FontSize * em / SpaceRatio);

        text.Inlines.Clear();

        for (var i = 0; i < value.Length; i++)
        {
            text.Inlines.Add(new Run(value[i].ToString()));

            if (i < value.Length - 1) text.Inlines.Add(new Run(" ") { FontSize = size });
        }
    }

    /// <summary>
    /// Whether the family is fixed-pitch.
    ///
    /// Matched by name rather than by measuring glyphs: the families in play are known, and the check
    /// has to survive a font falling back to something that was never asked for.
    /// </summary>
    private static bool IsMonospaced(FontFamily? family)
    {
        var name = family?.Source;
        if (string.IsNullOrEmpty(name)) return false;

        return name.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Mono", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Courier", StringComparison.OrdinalIgnoreCase);
    }
}
