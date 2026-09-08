using System.Windows;
using System.Windows.Input;

namespace LAM.App.Views;

/// <summary>
/// The themed stand-in for <c>MessageBox.Show</c>.
///
/// Call <see cref="Say"/> to tell the user something and <see cref="Confirm"/> to ask. Both take an
/// owner so the dialog centres on the window rather than the screen, and both fall back to a plain
/// message box if no owner is available — during startup, say, before any window exists, where
/// showing nothing at all would be worse than showing the wrong chrome.
/// </summary>
public partial class Dialog : Window
{
    private Dialog() => InitializeComponent();

    /// <summary>Tells the user something. One button.</summary>
    public static void Say(Window? owner, string title, string message)
        => Show(owner, title, message, confirmLabel: "OK", cancel: false);

    /// <summary>
    /// Asks the user to confirm. Two buttons, and the affirmative one is labelled with the ACTION —
    /// "MOVE TO TRASH", not "YES" — so the button says what it will do rather than making the title
    /// carry that alone.
    /// </summary>
    public static bool Confirm(Window? owner, string title, string message, string confirmLabel)
        => Show(owner, title, message, confirmLabel, cancel: true);

    private static bool Show(Window? owner, string title, string message, string confirmLabel, bool cancel)
    {
        owner ??= Application.Current?.MainWindow;

        if (owner is null || !owner.IsLoaded)
        {
            // No window to own or centre on. The OS box is the wrong look, but it is reachable.
            var fallback = MessageBox.Show(
                message, title,
                cancel ? MessageBoxButton.OKCancel : MessageBoxButton.OK,
                MessageBoxImage.None);

            return fallback == MessageBoxResult.OK;
        }

        var dialog = new Dialog { Owner = owner };

        dialog.TitleText.Text = title.ToUpperInvariant();
        dialog.BodyText.Text = message;
        dialog.ConfirmButton.Content = confirmLabel.ToUpperInvariant();

        if (cancel)
        {
            dialog.CancelButton.Visibility = Visibility.Visible;
        }

        return dialog.ShowDialog() == true;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnDragBar(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        // Escape closes a one-button dialog too: with nothing to cancel it is just "dismiss", and a
        // dialog that ignores Escape feels stuck.
        if (e.Key != Key.Escape) return;

        e.Handled = true;
        DialogResult = false;
    }
}
