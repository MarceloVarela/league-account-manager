using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LAM.App.Services;
using LAM.App.ViewModels;
using LAM.Core.Login;
using LAM.Core.Model;
using LAM.Core.Riot;
using LAM.Core.Vault;
using Microsoft.Win32;

namespace LAM.App.Views;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly MainViewModel _model = new();
    private CancellationTokenSource? _loginCancellation;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();

        _services = new AppServices();
        DataContext = _model;

        // Before anything paints. The lock screen is drawn long before the vault is open, so reading
        // the palette from the encrypted settings would mean opening light and flipping at unlock.
        ThemeSwitcher.Apply(ThemePreference.Read(_services.Paths.Root));

        Loaded += OnLoaded;
        Closing += OnClosing;

        // The hint text depends on the filtered list, so keep it in step with the list itself
        // rather than remembering to update it at every call site that re-filters.
        // Status messages become toasts. The status bar keeps the standing facts (account count,
        // client state); transient news belongs in the corner where the design puts it.
        _model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Status)) ShowToast(_model.Status);
        };

        _model.VisibleAccounts.CollectionChanged += (_, _) =>
        {
            UpdateEmptyHint();
            LoadIcons();
            NumberTheCards();
            UpdateChrome();
        };

        // Any interaction counts as activity, so the idle lock only fires when the app really has
        // been sitting untouched.
        PreviewMouseDown += (_, _) => _model.Vault?.Touch();
        PreviewKeyDown += (_, _) => _model.Vault?.Touch();

        SystemEvents.SessionSwitch += OnSessionSwitch;
        StateChanged += OnStateChanged;

        SetUpTray();

        // "/" focuses search, the way it does everywhere else.
        PreviewKeyDown += OnGlobalKey;
    }

    // ---- lifecycle ---------------------------------------------------------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var exists = _services.Paths.VaultExists;

        RenderLockPanel(exists, null);

        ConfirmPanel.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;

        if (exists && _services.Hello.IsEnrolled && await HelloUnlock.IsAvailableAsync())
        {
            HelloButton.Visibility = Visibility.Visible;
            HelloDivider.Visibility = Visibility.Visible;
            // Offer Hello straight away — it is the intended everyday path.
            await TryHelloAsync(silentOnCancel: true);
        }

        MasterPasswordBox.Focus();
    }

    /// <summary>
    /// Asks the client which account is signed in, so the matching card can be marked.
    ///
    /// Polled rather than read once: the answer changes whenever an account is switched, including
    /// switches made outside this app.
    /// </summary>
    private async Task WatchSignedInAccountAsync()
    {
        while (!_closing)
        {
            try
            {
                var reading = await _services.Identity.TryReadAsync(
                    TimeSpan.FromSeconds(3), CancellationToken.None);

                _model.SetSignedInAccount(reading?.Puuid);
            }
            catch (Exception)
            {
                _model.SetSignedInAccount(null);
            }

            await Task.Delay(TimeSpan.FromSeconds(15));
        }
    }

    // ---- window chrome -----------------------------------------------------
    //
    // The window is frameless via WindowChrome, so the caption buttons are ordinary Buttons and have
    // to do the work the OS would otherwise do.

    private void OnMinimise(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximise(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximiseGlyph();
    }

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Keeps the maximise glyph honest.
    ///
    /// The state changes without the button being pressed — a double-click on the caption, a drag to
    /// the top of the screen, Win+Up — so the glyph is driven from the state rather than toggled.
    /// </summary>
    private void UpdateMaximiseGlyph()
    {
        if (MaximiseButton is null) return;

        MaximiseButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        MaximiseButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximise";
    }

    // ---- navigation --------------------------------------------------------

    /// <summary>
    /// Switches pane. The toolbar belongs to Accounts alone, so it comes and goes with it.
    /// </summary>
    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent, before the rest of the tree exists.
        if (AccountsPane is null || PaneHost is null) return;

        var accounts = TabAccounts.IsChecked == true;

        AccountsPane.Visibility = accounts ? Visibility.Visible : Visibility.Collapsed;
        AccountsToolbar.Visibility = accounts ? Visibility.Visible : Visibility.Collapsed;
        PaneHost.Visibility = accounts ? Visibility.Collapsed : Visibility.Visible;

        if (accounts)
        {
            PaneHost.Content = null;
            UpdateEmptyHint();
            return;
        }

        var vault = _model.Vault;
        if (vault is null) return;

        PaneHost.Content = true switch
        {
            _ when TabFleet.IsChecked == true => new FleetPane(vault.Document.Accounts, _services),
            _ when TabLoot.IsChecked == true => new LootPane(vault.Document.Accounts, _services),
            _ => new SettingsPane(vault, _services),
        };
    }

    private void OnAddAccountTile(object sender, MouseButtonEventArgs e) => OnAddAccount(sender, e);

    private void OnCycleSort(object sender, RoutedEventArgs e) => _model.CycleSort();

    private void OnGameFilter(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string game }) _model.GameFilter = game;
    }

    /// <summary>The icon frame swallows its click, so filling an icon is never a sign-in.</summary>
    private void OnIconClicked(object sender, MouseButtonEventArgs e) => e.Handled = true;

    /// <summary>
    /// Gives each visible card its part code.
    ///
    /// Assigned by position rather than stored: the rail is a drawing convention, not a property of
    /// the account, and a stored code would go stale the moment the list was filtered or reordered.
    /// </summary>
    private void NumberTheCards()
    {
        for (var i = 0; i < _model.VisibleAccounts.Count; i++)
            _model.VisibleAccounts[i].PartCode = "ACC-" + (i + 1).ToString("00");
    }

    private System.Windows.Threading.DispatcherTimer? _toastTimer;

    /// <summary>The design's one rise: 8px up and fade in, 180ms ease-out.</summary>
    private static System.Windows.Media.Animation.Storyboard BuildRise()
    {
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(
            fade, new PropertyPath(OpacityProperty));

        var slide = new System.Windows.Media.Animation.DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
            },
        };
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(
            slide, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));

        var board = new System.Windows.Media.Animation.Storyboard();
        board.Children.Add(fade);
        board.Children.Add(slide);
        return board;
    }

    /// <summary>
    /// Shows a transient message.
    ///
    /// The previous timer is always cancelled first, so a burst of messages replaces rather than
    /// stacks — two toasts fading independently is how a tidy corner becomes a mess.
    /// </summary>
    private void ShowToast(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        ToastText.Text = message.ToUpperInvariant();
        Toast.Visibility = Visibility.Visible;

        // Its own storyboard, deliberately. Storyboard.SetTarget mutates the resource in place, so
        // retargeting the shared RamRise would make any two surfaces animating at once fight over one
        // object — and it throws outright if the resource is ever frozen.
        Toast.BeginStoryboard(BuildRise());

        _toastTimer?.Stop();
        _toastTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(2600),
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };
        _toastTimer.Start();
    }

    /// <summary>Fills the chrome bands that are not data-bound.</summary>
    private void UpdateChrome()
    {
        var vault = _model.Vault;
        var accounts = vault?.Document.Live.ToList() ?? [];

        VersionText.Text = "v" + (System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(3) ?? "1.0.0");

        var regions = accounts.Select(a => a.Region)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        StatusLeft.Text = accounts.Count == 0
            ? "EMPTY VAULT · NOTHING STORED YET · LOCAL ONLY"
            : accounts.Count + " ACCOUNTS · " + regions + " REGIONS · VAULT ENCRYPTED (AES-GCM) · LOCAL ONLY";

        var lockfile = _services.RiotPaths.LeagueLockfile;
        var clientUp = lockfile is not null && System.IO.File.Exists(lockfile);

        ClientDot.Visibility = clientUp ? Visibility.Visible : Visibility.Collapsed;
        StatusRight.Text = clientUp ? "LEAGUE CLIENT DETECTED · LCU ONLY" : "LEAGUE CLIENT NOT RUNNING";

        var newest = accounts
            .Select(a => a.Identity.ClientStats?.CapturedUtc)
            .Where(when => when is not null)
            .DefaultIfEmpty(null)
            .Max();

        SyncedText.Text = newest is { } stamp
            ? "SYNCED " + stamp.LocalDateTime.ToString("d MMM").ToUpperInvariant()
            : "NEVER SYNCED";
    }

    private void OnGlobalKey(object sender, KeyEventArgs e)
    {
        if (_model.IsLocked || _model.IsBusy) return;

        // Not while typing, or "/" could never be entered into a search or a note.
        if (e.OriginalSource is TextBox or PasswordBox) return;

        if (e.Key == Key.Oem2 || e.Key == Key.Divide)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _loginCancellation?.Cancel();
        _model.Vault?.Dispose();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        var vault = _model.Vault;
        if (vault is null || vault.IsLocked) return;
        if (!vault.Settings.LockOnWorkstationLock) return;

        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff
            or SessionSwitchReason.RemoteDisconnect)
        {
            Dispatcher.Invoke(() => LockVault(VaultLockReason.WorkstationLocked));
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        UpdateMaximiseGlyph();

        var vault = _model.Vault;
        if (WindowState != WindowState.Minimized) return;

        if (vault is { IsLocked: false } && vault.Settings.LockOnMinimize)
            LockVault(VaultLockReason.Requested);

        // Hiding is what takes it off the taskbar; minimising alone only shrinks it. Order matters:
        // lock first, so a vault set to lock on minimise is already locked before it disappears.
        if (vault?.Settings.MinimizeToTray == true) Hide();
    }

    // ---- tray and quick swap -----------------------------------------------

    /// <summary>
    /// The live stealth session, if a sign-in started one.
    ///
    /// Held here rather than inside the login task because the client keeps its chat connection open
    /// for as long as it runs — tearing the proxy down when the sign-in finished would kill chat a
    /// few seconds after the user got in.
    /// </summary>
    private LAM.Core.Stealth.StealthSession? _stealth;

    private TrayIcon? _tray;
    private GlobalHotkey? _hotkey;
    private QuickSwapFlyout? _flyout;

    private void SetUpTray()
    {
        _tray = new TrayIcon();

        _tray.Activated += ShowQuickSwap;

        // Only offer the submenu when stealth is actually running; ticking a mode that cannot take
        // effect would be a lie.
        _tray.StealthMode = () => _model.Vault?.Settings is { StealthLogin: true } settings
            ? settings.StealthMode
            : null;

        _tray.StealthModeRequested += mode => _ = ApplyStealthModeAsync(mode);
        _tray.OpenRequested += RestoreFromTray;
        _tray.LockRequested += () => LockVault(VaultLockReason.Requested);
        _tray.ExitRequested += () =>
        {
            if (_flyout is not null) _flyout.IsShuttingDown = true;
            Application.Current.Shutdown();
        };

        Loaded += (_, _) =>
        {
            _hotkey = new GlobalHotkey();
            _hotkey.Pressed += ShowQuickSwap;
            _hotkey.Attach(this);

            // A hotkey another app already owns is a lost convenience, not a fault: say so once in
            // the status line rather than raising a dialog at startup.
            if (!_hotkey.IsRegistered)
                _model.Status = "alt + \\ is taken by another app - quick swap is on the tray icon";
        };

        _services.Orchestrator.StealthStarted += session =>
        {
            // One at a time: a second sign-in replaces the first, and the old client is already gone.
            _stealth?.Dispose();
            _stealth = session;

            // The fake friend can ask for a mode too, and it must land in the same place as the tray
            // and the settings pane or the three would disagree.
            session.ModeRequested += mode =>
                Dispatcher.Invoke(() => _ = ApplyStealthModeAsync(mode));
        };

        Closed += (_, _) =>
        {
            _stealth?.Dispose();
            _hotkey?.Dispose();
            _tray?.Dispose();

            if (_flyout is not null)
            {
                _flyout.IsShuttingDown = true;
                _flyout.Close();
            }
        };
    }

    /// <summary>
    /// Changes how the account appears, and makes it take effect now.
    ///
    /// Persisted first, then pushed: the setting is what a later sign-in reads, and the live push is
    /// what the current session sees. Without the push nothing happens until the client next decides
    /// to send presence, which can be minutes.
    /// </summary>
    private async Task ApplyStealthModeAsync(LAM.Core.Stealth.StealthMode mode)
    {
        var vault = _model.Vault;
        if (vault is null || vault.IsLocked) return;

        vault.Settings.StealthMode = mode;

        try { vault.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _model.Status = "Could not save the stealth mode.";
        }

        if (_stealth is not null) await _stealth.RefreshPresenceAsync();

        _model.Status = "Appearing " + mode.ToString().ToLowerInvariant() + ".";
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ShowQuickSwap()
    {
        var vault = _model.Vault;

        // Kept and re-shown, not recreated: a hotkey-raised panel has to feel instant or it is slower
        // than alt-tabbing to the manager, which is the whole reason it exists.
        if (_flyout is null)
        {
            _flyout = new QuickSwapFlyout();
            _flyout.SignInRequested += async tile =>
            {
                RestoreFromTray();
                await SignInAsync(tile);
            };
            _flyout.OpenManagerRequested += RestoreFromTray;
            _flyout.LockRequested += () => LockVault(VaultLockReason.Requested);
        }

        if (_flyout.IsVisible)
        {
            _flyout.Hide();
            return;
        }

        _flyout.ShowNear(
            vault is { IsLocked: false } ? _model.VisibleAccounts : [],
            vault is null || vault.IsLocked);
    }

    // ---- unlocking ---------------------------------------------------------

    private void OnMasterPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnUnlock(sender, e);
    }

    private void OnUnlock(object sender, RoutedEventArgs e)
    {
        ShowLockError(null);

        var creating = !_services.Paths.VaultExists;
        var entered = MasterPasswordBox.Password;

        if (string.IsNullOrEmpty(entered))
        {
            ShowLockError("Enter your master password.");
            return;
        }

        if (creating)
        {
            if (entered.Length < 8)
            {
                ShowLockError("Use at least 8 characters. This is the only thing protecting every account you store.");
                return;
            }

            if (entered != ConfirmPasswordBox.Password)
            {
                ShowLockError("The two passwords do not match.");
                return;
            }
        }

        using var password = SecretBuffer.FromString(entered);

        try
        {
            var vault = creating
                ? _services.Repository.Create(password, bindToMachine: true)
                : _services.Repository.Unlock(password);

            OpenVault(vault);
        }
        catch (VaultAuthenticationException ex)
        {
            ShowLockError(ex.Message);
        }
        catch (VaultProfileMismatchException ex)
        {
            ShowLockError(ex.Message);
        }
        catch (VaultFormatException ex)
        {
            ShowLockError(ex.Message);
        }
        finally
        {
            MasterPasswordBox.Clear();
            ConfirmPasswordBox.Clear();
        }
    }

    private async void OnUnlockWithHello(object sender, RoutedEventArgs e) => await TryHelloAsync(silentOnCancel: false);

    private async Task TryHelloAsync(bool silentOnCancel)
    {
        try
        {
            using var key = await _services.Hello.UnlockKeyAsync();
            OpenVault(_services.Repository.UnlockWithKey(key));
        }
        catch (HelloUnlockException ex)
        {
            if (!silentOnCancel) ShowLockError(ex.Message);
        }
        catch (VaultAuthenticationException ex)
        {
            ShowLockError(ex.Message);
        }
    }

    private void OpenVault(UnlockedVault vault)
    {
        vault.Locked += OnVaultLocked;
        vault.StartIdleTimer();

        // The vault's own setting is authoritative; re-apply in case it disagrees with the mirror
        // (another machine, a restored backup) and put the mirror back in step.
        var theme = vault.Settings.UseDarkTheme ? AppTheme.Dark : AppTheme.Light;
        ThemeSwitcher.Apply(theme);
        ThemePreference.Write(_services.Paths.Root, theme);

        // Mirror the two numbers the lock screen needs before anything can be decrypted. Written on
        // unlock rather than on every save: they change rarely, and a count one session out of date
        // is better than touching the disk on every edit.
        LockScreenFacts.Write(
            _services.Paths.Root,
            vault.Document.Live.Count(),
            vault.Settings.AutoLockMinutes);

        _model.SetVault(vault);
        _model.Status = "Vault unlocked.";
        UpdateEmptyHint();

        // A vault with accounts but no Hello enrolment: offer it once, since it is the whole point
        // of having chosen "master password + Hello".
        _ = OfferHelloEnrolmentAsync(vault);
        _ = WatchSignedInAccountAsync();
    }

    private async Task OfferHelloEnrolmentAsync(UnlockedVault vault)
    {
        if (vault.Settings.WindowsHelloEnabled || _services.Hello.IsEnrolled) return;
        if (!await HelloUnlock.IsAvailableAsync()) return;

        var answer = Dialog.Confirm(
            this,
            "Windows Hello",
            "Unlock with Windows Hello from now on?\n\n" +
            "Your master password keeps working — Hello is just a faster way in, and it never leaves this machine.",
            "Enable Hello");

        if (!answer) return;

        try
        {
            await _services.Hello.EnrollAsync(vault);
            vault.Settings.WindowsHelloEnabled = true;
            vault.Save();
            _model.Status = "Windows Hello enabled.";
        }
        catch (HelloUnlockException ex)
        {
            Dialog.Say(
                this,
                "Windows Hello",
                ex.Message);
        }
    }

    private void OnVaultLocked(object? sender, VaultLockReason reason)
    {
        Dispatcher.Invoke(() =>
        {
            _model.SetVault(null);
            _model.Status = string.Empty;
            MasterPasswordBox.Clear();

            // Re-render rather than assign: writing prose here is what used to replace the design's
            // lowercase copy with sentence-case English for the rest of the session.
            ShowLockError(null);
            RenderLockPanel(true, reason);

            MasterPasswordBox.Focus();
        });
    }

    private void OnLock(object sender, RoutedEventArgs e) => LockVault(VaultLockReason.Requested);

    private void LockVault(VaultLockReason reason)
    {
        var vault = _model.Vault;
        if (vault is null || vault.IsLocked) return;

        try { vault.Save(); }
        catch (Exception) { /* locking must not be blocked by a failed save */ }

        vault.Lock(reason);
    }

    /// <summary>
    /// Fills the lock panel.
    ///
    /// Single entry point on purpose. The copy used to be written in two places — once at load and
    /// again on every lock — and the second one quietly replaced the design's lowercase register with
    /// sentence-case English that then never went away.
    /// </summary>
    private void RenderLockPanel(bool exists, VaultLockReason? reason)
    {
        LockTitle.Text = exists ? "VAULT LOCKED" : "NEW VAULT";
        UnlockButton.Content = exists ? "UNLOCK THE VAULT" : "CREATE THE VAULT";

        var facts = LockScreenFacts.Read(_services.Paths.Root);

        if (!exists)
        {
            LockSubtitle.Text =
                "pick a master password. it encrypts every account you store here, and there is no reset.";
        }
        else
        {
            // The count comes from the mirror file, so an unread vault simply omits it rather than
            // claiming zero.
            var lead = facts.AccountCount > 0
                ? facts.AccountCount + " accounts sealed in AES-GCM. "
                : "accounts sealed in AES-GCM. ";

            LockSubtitle.Text = reason switch
            {
                VaultLockReason.Idle => lead + "locked after a spell of nothing. back in?",
                VaultLockReason.WorkstationLocked => lead + "you locked windows, so it locked too.",
                _ => lead + "drop the master password and let's go.",
            };
        }

        LockFooter.Text = (facts.IdleMinutes > 0
                              ? "AUTO-LOCKS AFTER " + facts.IdleMinutes + " MIN IDLE"
                              : "AUTO-LOCKS WHEN IDLE")
                          + " \u00b7 NOTHING LEAVES THIS MACHINE";
    }

    private void ShowLockError(string? message)
    {
        LockError.Text = message ?? string.Empty;
        LockErrorPlate.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- accounts ----------------------------------------------------------

    private void OnAddAccount(object sender, RoutedEventArgs e)
    {
        var vault = RequireVault();
        if (vault is null) return;

        var account = new AccountEntry { Region = vault.Document.Accounts.LastOrDefault()?.Region ?? "BR" };
        var editor = new AccountEditorWindow(account, isNew: true) { Owner = this };

        if (editor.ShowDialog() != true) return;

        vault.Document.Accounts.Add(account);
        vault.Save();
        _model.ApplyFilter();
        UpdateEmptyHint();
        _model.Status = "Added " + account.DisplayRiotId + ".";
    }

    private void OnDismissWarning(object sender, RoutedEventArgs e)
    {
        // The plate sits inside the card, whose own click opens the account — swallow it, or
        // dismissing a warning would also open a window.
        e.Handled = true;

        if (sender is not Button { Tag: AccountTile tile }) return;

        var vault = RequireVault();
        if (vault is null || !tile.DismissWarning()) return;

        vault.Save();

        // Refresh, not ApplyFilter: tiles are reused now, so re-running the whole filter would
        // rebuild the grid for a change to one card.
        tile.Refresh();

        _model.Status = "Hidden on " + tile.Title + ". Restore it from the card menu.";
    }

    private void RestoreWarnings(AccountTile tile)
    {
        var vault = RequireVault();
        if (vault is null) return;

        tile.RestoreWarnings();
        vault.Save();
        tile.Refresh();

        _model.Status = "Warnings restored for " + tile.Title + ".";
    }

    private void OnCardMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AccountTile tile }) return;
        e.Handled = true;

        var menu = new ContextMenu();

        menu.Items.Add(MenuItemFor(
            tile.Account.IsFavourite ? "Unpin" : "Pin to front", () => ToggleFavourite(tile)));
        menu.Items.Add(MenuItemFor("Copy password", () => CopyPassword(tile)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("Edit…", () => EditAccount(tile)));
        menu.Items.Add(MenuItemFor("Recovery sheet…", () => ShowRecoverySheet(tile)));
        menu.Items.Add(MenuItemFor("Refresh rank and level", async () => await RefreshOneAsync(tile)));
        menu.Items.Add(MenuItemFor("Estimate account age", async () => await EstimateAgeAsync(tile)));
        menu.Items.Add(new Separator());
        if (tile.HasDismissedWarnings)
            menu.Items.Add(MenuItemFor("Show hidden warnings", () => RestoreWarnings(tile)));

        menu.Items.Add(MenuItemFor("Forget saved session", () => ForgetSession(tile)));
        menu.Items.Add(MenuItemFor("Move to trash…", () => RemoveAccount(tile)));

        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private static MenuItem MenuItemFor(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void EditAccount(AccountTile tile)
    {
        var vault = RequireVault();
        if (vault is null) return;

        var editor = new AccountEditorWindow(tile.Account, isNew: false) { Owner = this };
        if (editor.ShowDialog() != true) return;

        vault.Save();
        tile.Refresh();
        _model.ApplyFilter();
        _model.Status = "Saved changes to " + tile.Account.DisplayRiotId + ".";
    }

    private void ShowRecoverySheet(AccountTile tile)
        => new RecoverySheetWindow(tile.Account, _services.Catalogue.ChampionName) { Owner = this }.ShowDialog();

    private void ForgetSession(AccountTile tile)
    {
        var vault = RequireVault();
        if (vault is null) return;

        tile.Account.Session = null;
        vault.Save();
        tile.Refresh();
        _model.Status = "Forgot the saved session — the next sign-in will type the password.";
    }

    /// <summary>
    /// Moves an account to the trash rather than destroying it.
    ///
    /// This used to delete outright, and its own warning text spelled out why that was the wrong
    /// default: it took the password, the saved session and the entire recovery dossier — the only
    /// things here that cannot be rebuilt by signing in again. Now it is reversible, and purging is
    /// a second, deliberate act from the Vault window.
    /// </summary>
    private void RemoveAccount(AccountTile tile)
    {
        var vault = RequireVault();
        if (vault is null) return;

        var answer = Dialog.Confirm(
            this,
            "Move to trash",
            "Move " + tile.Account.DisplayRiotId + " to the trash?\n\n" +
            "It disappears from the grid but keeps its password and recovery details, and can be " +
            "restored from Vault. The Riot account itself is untouched.",
            "Move to trash");

        if (!answer) return;

        tile.Account.DeletedUtc = DateTimeOffset.UtcNow;
        vault.Save();
        _model.ApplyFilter();
        UpdateEmptyHint();
        _model.Status = "Moved " + tile.Account.DisplayRiotId + " to the trash — restore it from Vault.";
    }

    /// <summary>Copies the stored password, kept out of clipboard history and cleared shortly after.</summary>
    private void CopyPassword(AccountTile tile)
    {
        if (tile.Account.Password is not { IsEmpty: false } password)
        {
            _model.Status = "No password stored for " + tile.Account.DisplayRiotId + ".";
            return;
        }

        _model.Status = SecretClipboard.CopySecret(password.Value)
            ? "Password copied — kept out of clipboard history and cleared in "
              + (int)SecretClipboard.DefaultLifetime.TotalSeconds + "s."
            : "Could not reach the clipboard — another program may be holding it.";
    }

    private void ToggleFavourite(AccountTile tile)
    {
        var vault = RequireVault();
        if (vault is null) return;

        tile.Account.IsFavourite = !tile.Account.IsFavourite;
        vault.Save();
        _model.ApplyFilter();
        _model.Status = tile.Account.IsFavourite
            ? tile.Account.DisplayRiotId + " pinned to the front."
            : tile.Account.DisplayRiotId + " unpinned.";
    }

    // ---- signing in --------------------------------------------------------

    /// <summary>
    /// Opens the account. Signing in is the footer button, deliberately.
    ///
    /// Signing in is not an undoable gesture — it drives the real Riot client and types a real
    /// password — so it stays behind the one control that says it will, and the whole card surface
    /// keeps the safe action. The design shows a card whose body is the login target, but the footer
    /// here was already a working "LOG IN ->" button, so nothing was advertising a behaviour it did
    /// not have; moving login onto the body only made a misclick expensive.
    /// </summary>
    private void OnCardClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AccountTile tile }) return;
        OpenDetail(tile);
    }

    /// <summary>
    /// The keyboard equivalent of clicking the card.
    ///
    /// A clickable Border is invisible to Tab, so the grid was mouse-only in a design that mandates a
    /// focus ring on every focusable control — the ring had nothing to draw on. Enter opens the
    /// account, matching the mouse; Space is deliberately not bound, because it is the key most often
    /// pressed while scrolling.
    /// </summary>
    private void OnCardKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is not FrameworkElement { DataContext: AccountTile tile }) return;

        e.Handled = true;
        OpenDetail(tile);
    }

    private void OnTileKey(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;

        e.Handled = true;
        OnAddAccount(sender, e);
    }

    private async void OnSignInClicked(object sender, RoutedEventArgs e)
    {
        // Stop the click reaching the card underneath, or signing in would also open the window.
        e.Handled = true;

        if (sender is not Button { Tag: AccountTile tile }) return;
        await SignInAsync(tile);
    }

    private void OpenDetail(AccountTile tile)
    {
        var vault = RequireVault();
        if (vault is null) return;

        var window = new AccountDetailWindow(tile.Account, _services) { Owner = this };
        window.SignInRequested += async () => await SignInAsync(tile);

        window.ShowDialog();

        // Editing or refreshing inside the detail window mutates the account in place.
        vault.Save();
        tile.Refresh();
        _model.ApplyFilter();
    }

    private async Task SignInAsync(AccountTile tile)
    {
        var vault = RequireVault();
        if (vault is null) return;

        if (!tile.CanSignIn)
        {
            Dialog.Say(
                this,
                "Nothing to sign in with",
                "This account has no saved password and no usable session, so there is nothing to sign in with. " +
                "Edit it and add the password.");
            return;
        }

        _loginCancellation = new CancellationTokenSource();
        _model.BeginBusy("Signing in as " + tile.Title, isTyping: false);

        var progress = new Progress<LoginProgress>(update =>
            _model.UpdateBusy(update.Message, update.Stage == LoginStage.Typing));

        try
        {
            var result = await _services.Orchestrator.LoginAsync(
                tile.Account,
                vault.Settings,
                progress,
                RequestVerificationCodeAsync,
                _loginCancellation.Token);

            // The account was mutated in place (session, identity, last-used), so persist regardless
            // of the outcome — a captured session is worth keeping even if a later step failed.
            //
            // Saved in its own try because a save failure must not swallow the sign-in result. It
            // did once: a disposed vault key threw here and the only thing reported was
            // "Cannot access a disposed object", with the actual outcome of the login lost.
            string? saveError = null;
            try
            {
                vault.Save();
            }
            catch (Exception ex)
            {
                saveError = ex.Message;
            }

            tile.Refresh();
            _model.ApplyFilter();

            _model.EndBusy(result.Message);

            // The design's "you're in - glhf". Only when the window is out of the way: a notification
            // about the window you are already looking at is noise.
            if (result.IsSuccess && (!IsVisible || WindowState == WindowState.Minimized))
            {
                _tray?.Notify("you're in - glhf", "Signed in as " + tile.Title + ".");
            }

            if (!result.IsSuccess)
            {
                Dialog.Say(
                    this,
                    "Could not sign in",
                    result.Message);
            }

            if (saveError is not null)
            {
                // Worth its own message: the sign-in itself may have been fine, but anything learned
                // along the way — a captured session above all — has just been lost, so the next
                // sign-in will type the password again.
                Dialog.Say(
                    this,
                    "Could not save",
                    "Signed in, but the vault could not be saved, so this account's session and " +
                    "details were not kept. The next sign-in will type the password again.\n\n" + saveError);
            }
        }
        catch (OperationCanceledException)
        {
            _model.EndBusy("Sign-in cancelled.");
        }
        catch (Exception ex)
        {
            _model.EndBusy("Sign-in failed.");
            Dialog.Say(
                this,
                "Could not sign in",
                ex.Message);
        }
        finally
        {
            _loginCancellation?.Dispose();
            _loginCancellation = null;
        }
    }

    /// <summary>
    /// Shown when Riot asks for a two-factor or emailed code. Returning null cancels the sign-in.
    /// </summary>
    private Task<string?> RequestVerificationCodeAsync(VerificationPrompt prompt, CancellationToken cancellationToken)
        => Dispatcher.InvokeAsync(() =>
        {
            var dialog = new VerificationCodeWindow(prompt) { Owner = this };
            return dialog.ShowDialog() == true ? dialog.Code : null;
        }).Task;

    private void OnCancelLogin(object sender, RoutedEventArgs e) => _loginCancellation?.Cancel();

    // ---- riot api ----------------------------------------------------------

    private async void OnRefreshAll(object sender, RoutedEventArgs e)
    {
        var vault = RequireVault();
        if (vault is null) return;

        var key = vault.Settings.RiotApiKey;
        if (key is null || key.IsEmpty)
        {
            Dialog.Say(
                this,
                "No API key",
                "Add a Riot API key in Settings to look up ranks and levels.\n\n" +
                "Use a Personal key from developer.riotgames.com — Development keys expire every 24 hours.");
            return;
        }

        var accounts = vault.Document.Accounts.Where(a => a.Identity.RiotAccountId is not null
                                                          || a.Identity.GameName is not null).ToList();
        if (accounts.Count == 0)
        {
            Dialog.Say(
                this,
                "Nothing to refresh",
                "None of your accounts have been signed into yet, so there is nothing to look up. " +
                "Sign in once and the client fills in the Riot ID automatically.");
            return;
        }

        _loginCancellation = new CancellationTokenSource();
        _model.BeginBusy("Refreshing ranks");

        using var client = new RiotApiClient(key, http: null, trace: _services.Trace);
        var updated = 0;
        var renamed = 0;

        try
        {
            for (var i = 0; i < accounts.Count; i++)
            {
                _model.UpdateBusy((i + 1) + " of " + accounts.Count + " — " + accounts[i].DisplayRiotId, false);

                var outcome = await client.RefreshAsync(
                    accounts[i], DateTimeOffset.UtcNow, _loginCancellation.Token);

                if (outcome.Success) updated++;
                if (outcome.Renamed) renamed++;
            }

            vault.Save();
            _model.ApplyFilter();

            var message = "Refreshed " + updated + " of " + accounts.Count + " accounts.";
            if (renamed > 0) message += "  " + renamed + " had been renamed since last time.";
            _model.EndBusy(message);
        }
        catch (OperationCanceledException)
        {
            vault.Save();
            _model.ApplyFilter();
            _model.EndBusy("Refresh cancelled.");
        }
        catch (RiotApiKeyRejectedException ex)
        {
            _model.EndBusy("API key rejected.");
            Dialog.Say(
                this,
                "Riot API",
                ex.Message);
        }
        catch (RiotApiRateLimitedException ex)
        {
            _model.EndBusy("Rate limited.");
            Dialog.Say(
                this,
                "Riot API",
                ex.Message);
        }
        finally
        {
            _loginCancellation?.Dispose();
            _loginCancellation = null;
        }
    }

    private async Task RefreshOneAsync(AccountTile tile)
    {
        var vault = RequireVault();
        var key = vault?.Settings.RiotApiKey;
        if (vault is null || key is null || key.IsEmpty)
        {
            Dialog.Say(
                this,
                "No API key",
                "Add a Riot API key in Settings first.");
            return;
        }

        _model.BeginBusy("Refreshing " + tile.Title);
        using var client = new RiotApiClient(key, http: null, trace: _services.Trace);

        try
        {
            var outcome = await client.RefreshAsync(tile.Account, DateTimeOffset.UtcNow, CancellationToken.None);
            vault.Save();
            tile.Refresh();
            _model.EndBusy(outcome.Message ?? (outcome.Renamed ? "Refreshed — this account was renamed." : "Refreshed."));
        }
        catch (Exception ex)
        {
            _model.EndBusy("Refresh failed.");
            Dialog.Say(
                this,
                "Riot API",
                ex.Message);
        }
    }

    /// <summary>
    /// Works out roughly how old an account is. Costs a burst of requests, so it is a deliberate
    /// per-account action rather than part of the bulk refresh.
    /// </summary>
    private async Task EstimateAgeAsync(AccountTile tile)
    {
        var vault = RequireVault();
        var key = vault?.Settings.RiotApiKey;
        if (vault is null || key is null || key.IsEmpty)
        {
            Dialog.Say(
                this,
                "No API key",
                "Add a Riot API key in Settings first.");
            return;
        }

        if (tile.Account.Identity.RiotAccountId is null)
        {
            Dialog.Say(
                this,
                "Not enough information",
                "Sign in to this account once first, so the client can tell us its PUUID.");
            return;
        }

        _model.BeginBusy("Estimating how old " + tile.Title + " is",
            isTyping: false);
        _model.UpdateBusy("Searching match history — this takes a few seconds.", false);

        using var client = new RiotApiClient(key, http: null, trace: _services.Trace);
        try
        {
            var estimate = await client.EstimateAgeAsync(tile.Account, CancellationToken.None);
            if (estimate is null)
            {
                _model.EndBusy("No match history found for this account.");
                return;
            }

            tile.Account.Identity.OldestKnownMatchId = estimate.OldestMatchId;
            tile.Account.Identity.OldestKnownMatchUtc = estimate.OldestMatchUtc;
            tile.Account.Identity.AgeIsLowerBoundOnly = estimate.LowerBoundOnly;
            vault.Save();
            tile.Refresh();

            _model.EndBusy(estimate.Describe());
            Dialog.Say(
                this,
                "Account age",
                estimate.Describe());
        }
        catch (Exception ex)
        {
            _model.EndBusy("Could not estimate the age.");
            Dialog.Say(
                this,
                "Riot API",
                ex.Message);
        }
    }

    // ---- settings ----------------------------------------------------------

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var vault = RequireVault();
        if (vault is null) return;

        var settings = new SettingsWindow(vault, _services) { Owner = this };
        settings.ShowDialog();

        vault.Save();
        vault.StartIdleTimer();   // pick up a changed auto-lock interval
        _model.ApplyFilter();
    }

    // ---- helpers -----------------------------------------------------------

    private UnlockedVault? RequireVault()
    {
        var vault = _model.Vault;
        if (vault is not null && !vault.IsLocked) return vault;

        _model.SetVault(null);
        return null;
    }

    /// <summary>
    /// Shows the icons already on disk, and quietly fetches any that are missing.
    ///
    /// Cached ones are applied synchronously so the grid never flickers; the download runs detached
    /// because an account list must not wait on Riot's CDN, and an icon that arrives late simply
    /// appears on the next refresh.
    /// </summary>
    private void LoadIcons()
    {
        var tiles = _model.VisibleAccounts.ToList();
        foreach (var tile in tiles) tile.LoadIcon(_services.Icons);

        var missing = tiles
            .Where(t => !t.HasIcon && t.Account.Identity.ProfileIconId is > 0)
            .ToList();

        if (missing.Count == 0) return;

        _ = Task.Run(async () =>
        {
            var fetched = false;

            foreach (var tile in missing)
            {
                var id = tile.Account.Identity.ProfileIconId!.Value;
                if (await _services.Icons.GetIconPathAsync(id, CancellationToken.None) is not null)
                    fetched = true;
            }

            if (fetched)
                Dispatcher.Invoke(() => { foreach (var tile in missing) tile.LoadIcon(_services.Icons); });
        });
    }

    private void UpdateEmptyHint()
    {
        var vault = _model.Vault;
        if (vault is null || vault.IsLocked)
        {
            EmptyHint.Visibility = Visibility.Collapsed;
            return;
        }

        if (vault.Document.Accounts.Count == 0)
        {
            EmptyHint.Text = "No accounts yet.\n\nClick \"Add account\" to store your first one. " +
                             "The first sign-in types the password; after that it is instant.";
            EmptyHint.Visibility = Visibility.Visible;
        }
        else if (_model.VisibleAccounts.Count == 0)
        {
            EmptyHint.Text = "Nothing matches that search.";
            EmptyHint.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyHint.Visibility = Visibility.Collapsed;
        }
    }
}
