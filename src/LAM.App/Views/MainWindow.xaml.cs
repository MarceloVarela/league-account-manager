using System;
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

        Loaded += OnLoaded;
        Closing += OnClosing;

        // The hint text depends on the filtered list, so keep it in step with the list itself
        // rather than remembering to update it at every call site that re-filters.
        _model.VisibleAccounts.CollectionChanged += (_, _) =>
        {
            UpdateEmptyHint();
            LoadIcons();
        };

        // Any interaction counts as activity, so the idle lock only fires when the app really has
        // been sitting untouched.
        PreviewMouseDown += (_, _) => _model.Vault?.Touch();
        PreviewKeyDown += (_, _) => _model.Vault?.Touch();

        SystemEvents.SessionSwitch += OnSessionSwitch;
        StateChanged += OnStateChanged;

        // "/" focuses search, the way it does everywhere else.
        PreviewKeyDown += OnGlobalKey;
    }

    // ---- lifecycle ---------------------------------------------------------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var exists = _services.Paths.VaultExists;

        LockTitle.Text = exists ? "Unlock your accounts" : "Create your vault";
        LockSubtitle.Text = exists
            ? "Your accounts are encrypted on this machine."
            : "Pick a master password. It encrypts every account and password you store here.";

        ConfirmPanel.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
        UnlockButton.Content = exists ? "Unlock" : "Create vault";

        if (exists && _services.Hello.IsEnrolled && await HelloUnlock.IsAvailableAsync())
        {
            HelloButton.Visibility = Visibility.Visible;
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
        var vault = _model.Vault;
        if (WindowState == WindowState.Minimized && vault is { IsLocked: false } && vault.Settings.LockOnMinimize)
            LockVault(VaultLockReason.Requested);
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

        var answer = MessageBox.Show(
            "Unlock with Windows Hello from now on?\n\n" +
            "Your master password keeps working — Hello is just a faster way in, and it never leaves this machine.",
            "Windows Hello",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        try
        {
            await _services.Hello.EnrollAsync(vault);
            vault.Settings.WindowsHelloEnabled = true;
            vault.Save();
            _model.Status = "Windows Hello enabled.";
        }
        catch (HelloUnlockException ex)
        {
            MessageBox.Show(ex.Message, "Windows Hello", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnVaultLocked(object? sender, VaultLockReason reason)
    {
        Dispatcher.Invoke(() =>
        {
            _model.SetVault(null);
            _model.Status = string.Empty;
            MasterPasswordBox.Clear();

            LockSubtitle.Text = reason switch
            {
                VaultLockReason.Idle => "Locked after a period of inactivity.",
                VaultLockReason.WorkstationLocked => "Locked because you locked Windows.",
                _ => "Your accounts are encrypted on this machine.",
            };

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

    private void ShowLockError(string? message)
    {
        LockError.Text = message ?? string.Empty;
        LockError.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
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

        var answer = MessageBox.Show(
            "Move " + tile.Account.DisplayRiotId + " to the trash?\n\n" +
            "It disappears from the grid but keeps its password and recovery details, and can be " +
            "restored from Vault. The Riot account itself is untouched.",
            "Move to trash",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

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

    /// <summary>Opens the vault's own health and trash view.</summary>
    private void OnOpenVault(object sender, RoutedEventArgs e)
    {
        var vault = RequireVault();
        if (vault is null) return;

        new VaultWindow(vault, _services.Repository) { Owner = this }.ShowDialog();

        vault.Save();
        _model.ApplyFilter();
        UpdateEmptyHint();
    }

    // ---- signing in --------------------------------------------------------

    /// <summary>Opens the account. Signing in is a separate, deliberate button.</summary>
    private void OnCardClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AccountTile tile }) return;
        OpenDetail(tile);
    }

    private async void OnSignInClicked(object sender, RoutedEventArgs e)
    {
        // Stop the click reaching the card underneath, or signing in would also open the window.
        e.Handled = true;

        if (sender is not Button { Tag: AccountTile tile }) return;
        await SignInAsync(tile);
    }

    /// <summary>
    /// Opens the fleet view over every account, not just the filtered ones.
    ///
    /// Deliberately unfiltered: "who owns this skin" is a question about the whole vault, and
    /// answering it from a search-narrowed subset would confidently tell you nobody owns something
    /// that an account hidden by the current filter has.
    /// </summary>
    private void OnOpenFleet(object sender, RoutedEventArgs e)
    {
        var vault = RequireVault();
        if (vault is null) return;

        new FleetWindow(vault.Document.Accounts, _services) { Owner = this }.ShowDialog();
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
            MessageBox.Show(
                "This account has no saved password and no usable session, so there is nothing to sign in with. " +
                "Edit it and add the password.",
                "Nothing to sign in with", MessageBoxButton.OK, MessageBoxImage.Information);
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

            if (!result.IsSuccess)
            {
                MessageBox.Show(result.Message, "Could not sign in",
                    MessageBoxButton.OK,
                    result.Outcome == LoginOutcome.Aborted ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }

            if (saveError is not null)
            {
                // Worth its own message: the sign-in itself may have been fine, but anything learned
                // along the way — a captured session above all — has just been lost, so the next
                // sign-in will type the password again.
                MessageBox.Show(
                    "Signed in, but the vault could not be saved, so this account's session and " +
                    "details were not kept. The next sign-in will type the password again.\n\n" + saveError,
                    "Could not save", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            _model.EndBusy("Sign-in cancelled.");
        }
        catch (Exception ex)
        {
            _model.EndBusy("Sign-in failed.");
            MessageBox.Show(ex.Message, "Could not sign in", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show(
                "Add a Riot API key in Settings to look up ranks and levels.\n\n" +
                "Use a Personal key from developer.riotgames.com — Development keys expire every 24 hours.",
                "No API key", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var accounts = vault.Document.Accounts.Where(a => a.Identity.RiotAccountId is not null
                                                          || a.Identity.GameName is not null).ToList();
        if (accounts.Count == 0)
        {
            MessageBox.Show(
                "None of your accounts have been signed into yet, so there is nothing to look up. " +
                "Sign in once and the client fills in the Riot ID automatically.",
                "Nothing to refresh", MessageBoxButton.OK, MessageBoxImage.Information);
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
            MessageBox.Show(ex.Message, "Riot API", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (RiotApiRateLimitedException ex)
        {
            _model.EndBusy("Rate limited.");
            MessageBox.Show(ex.Message, "Riot API", MessageBoxButton.OK, MessageBoxImage.Information);
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
            MessageBox.Show("Add a Riot API key in Settings first.", "No API key",
                MessageBoxButton.OK, MessageBoxImage.Information);
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
            MessageBox.Show(ex.Message, "Riot API", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show("Add a Riot API key in Settings first.", "No API key",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (tile.Account.Identity.RiotAccountId is null)
        {
            MessageBox.Show("Sign in to this account once first, so the client can tell us its PUUID.",
                "Not enough information", MessageBoxButton.OK, MessageBoxImage.Information);
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
            MessageBox.Show(estimate.Describe(), "Account age", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _model.EndBusy("Could not estimate the age.");
            MessageBox.Show(ex.Message, "Riot API", MessageBoxButton.OK, MessageBoxImage.Warning);
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
