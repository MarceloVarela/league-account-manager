using System.Windows;
using System.Windows.Input;
using System.Windows.Shell;

namespace LAM.App.Services;

/// <summary>
/// Makes a Window frameless and gives its templated caption buttons something to do.
///
/// The six secondary windows all used stock Windows chrome, so a rounded, system-coloured title bar
/// appeared on top of a frameless radius-0 app — and it ignored the theme, so a dark session raised
/// light dialogs. Restyling each one by hand would have meant six near-identical edits; this is a
/// single attached property they opt into, with the chrome living in one template in Theme.xaml.
///
/// The caption buttons are driven by <see cref="SystemCommands"/> rather than by click handlers,
/// because a ControlTemplate in a resource dictionary has no code-behind to handle them in — and the
/// command bindings have to be registered per window, which is what this does.
/// </summary>
public static class Frameless
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(Frameless), new PropertyMetadata(false, OnChanged));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || !(bool)e.NewValue) return;

        // GlassFrameThickness 0 removes the DWM frame, so nothing of the system chrome is drawn.
        //
        // The template's 38px band IS the caption, which is why CaptionHeight matches it: Windows
        // then handles dragging, double-click maximise and the system menu natively, and the caption
        // buttons opt out with IsHitTestVisibleInChrome or the chrome swallows their clicks.
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 38,
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            ResizeBorderThickness = new Thickness(window.ResizeMode == ResizeMode.NoResize ? 0 : 5),
        });

        window.CommandBindings.Add(new CommandBinding(
            SystemCommands.CloseWindowCommand, (_, _) => window.Close()));

        window.CommandBindings.Add(new CommandBinding(
            SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(window)));

        window.CommandBindings.Add(new CommandBinding(
            SystemCommands.MaximizeWindowCommand, (_, _) => SystemCommands.MaximizeWindow(window)));

        window.CommandBindings.Add(new CommandBinding(
            SystemCommands.RestoreWindowCommand, (_, _) => SystemCommands.RestoreWindow(window)));
    }
}
