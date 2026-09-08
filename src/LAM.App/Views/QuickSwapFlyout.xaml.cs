using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LAM.App.ViewModels;

namespace LAM.App.Views;

/// <summary>
/// The quick-swap flyout: pick an account, sign in, done.
///
/// One instance is kept and re-shown rather than recreated, so the filter box keeps focus behaviour
/// and there is no window-creation delay on a hotkey press — the flyout has to feel instant or it is
/// slower than alt-tabbing to the manager, which defeats it.
/// </summary>
public partial class QuickSwapFlyout : Window
{
    private readonly List<AccountTile> _all = [];

    public event Action<AccountTile>? SignInRequested;
    public event Action? OpenManagerRequested;
    public event Action? LockRequested;

    public QuickSwapFlyout()
    {
        InitializeComponent();

        FilterBox.TextChanged += (_, _) =>
            FilterHint.Visibility = string.IsNullOrEmpty(FilterBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Fills the list and shows the flyout at the mouse, clamped to the work area.</summary>
    public void ShowNear(IEnumerable<AccountTile> accounts, bool vaultIsLocked)
    {
        _all.Clear();
        _all.AddRange(accounts);

        FilterBox.Text = string.Empty;
        Apply();

        EmptyNote.Text = vaultIsLocked
            ? "VAULT LOCKED — OPEN THE MANAGER TO UNLOCK"
            : _all.Count == 0 ? "VAULT EMPTY — ADD AN ACCOUNT" : string.Empty;

        Position();
        Show();
        Activate();

        FilterBox.Focus();
    }

    /// <summary>
    /// Bottom-right, above the notification area, and never off-screen.
    ///
    /// Measured against the WORK area rather than the screen bounds, so the flyout clears the taskbar
    /// wherever the user keeps it — a bottom-right constant would sit under a top or left taskbar.
    /// </summary>
    private void Position()
    {
        UpdateLayout();

        var work = SystemParameters.WorkArea;

        Left = Math.Max(work.Left, work.Right - Width - 12);
        Top = Math.Max(work.Top, work.Bottom - ActualHeight - 12);
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => Apply();

    private void Apply()
    {
        var query = FilterBox.Text?.Trim() ?? string.Empty;

        var shown = string.IsNullOrEmpty(query)
            ? _all
            : _all.Where(t => t.Account.MatchesSearch(query)).ToList();

        Rows.ItemsSource = shown;

        EmptyNote.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (shown.Count == 0 && !string.IsNullOrEmpty(query)) EmptyNote.Text = "NOTHING MATCHES";
    }

    private void OnRowClicked(object sender, MouseButtonEventArgs e) => Pick(sender);

    private void OnRowKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        Pick(sender);
    }

    private void Pick(object sender)
    {
        if (sender is not FrameworkElement { DataContext: AccountTile tile }) return;

        Hide();
        SignInRequested?.Invoke(tile);
    }

    private void OnOpenManager(object sender, RoutedEventArgs e)
    {
        Hide();
        OpenManagerRequested?.Invoke();
    }

    private void OnLockVault(object sender, RoutedEventArgs e)
    {
        Hide();
        LockRequested?.Invoke();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Hide();
            return;
        }

        // Down from the filter box walks into the list, so the whole flyout is keyboard-drivable
        // without reaching for the mouse — which is the point of a hotkey-raised panel.
        if (e.Key == Key.Down && FilterBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
    }

    /// <summary>Losing focus dismisses it. A flyout that lingers is just a second window.</summary>
    private void OnDeactivated(object sender, EventArgs e) => Hide();

    /// <summary>Closing the app must not be intercepted; hide on the user's X instead of disposing.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!IsShuttingDown)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>Set by the shell so the real shutdown can actually close this window.</summary>
    public bool IsShuttingDown { get; set; }
}
