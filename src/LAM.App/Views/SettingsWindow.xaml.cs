using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text;
using System.Windows;
using LAM.App.Services;
using LAM.Core.Model;
using LAM.Core.Riot;
using LAM.Core.Vault;
using LAM.Core.Windows;
using Microsoft.Win32;

namespace LAM.App.Views;

public partial class SettingsWindow : Window
{
    private sealed record StrategyChoice(StrategyPreference Value, string Label, string Help)
    {
        public override string ToString() => Label;
    }

    private static readonly StrategyChoice[] Strategies =
    [
        new(StrategyPreference.PreferSessionSwap, "Saved session, then type if needed",
            "The default. Types your password once per account, saves the session it produces, and " +
            "every later sign-in is instant. If Riot expires the session it types once more and carries on."),
        new(StrategyPreference.AlwaysAutofill, "Always type the password",
            "Never uses a saved session. Predictable, but every sign-in types your password and you " +
            "should not touch the mouse while it does."),
        new(StrategyPreference.SessionSwapOnly, "Only use saved sessions — never type",
            "No keystrokes are ever injected. The safest option, but an account whose session has " +
            "expired will simply refuse to sign in until you capture a new one."),
    ];

    private readonly UnlockedVault _vault;
    private readonly AppServices _services;

    public SettingsWindow(UnlockedVault vault, AppServices services)
    {
        InitializeComponent();

        _vault = vault;
        _services = services;

        StrategyBox.ItemsSource = Strategies;
        StrategyBox.SelectionChanged += (_, _) => UpdateStrategyHelp();

        Load();
    }

    private void Load()
    {
        var settings = _vault.Settings;

        AutoLockBox.Text = settings.AutoLockMinutes.ToString();
        LockOnWorkstationBox.IsChecked = settings.LockOnWorkstationLock;
        LockOnMinimizeBox.IsChecked = settings.LockOnMinimize;
        AbortOnFocusLossBox.IsChecked = settings.AbortTypingOnFocusLoss;
        MachineBoundBox.IsChecked = _vault.BindToMachine;

        StrategyBox.SelectedItem = Strategies.FirstOrDefault(s => s.Value == settings.Strategy) ?? Strategies[0];
        LaunchGameBox.IsChecked = settings.LaunchGameAfterSignIn;
        CarrySettingsBox.IsChecked = settings.CarryGameSettings;
        TimeoutBox.Text = settings.LoginWindowTimeoutSeconds.ToString();
        ClientPathBox.Text = settings.RiotClientPathOverride ?? string.Empty;

        ApiKeyBox.Password = settings.RiotApiKey?.Value ?? string.Empty;
        BackupsBox.Text = settings.BackupsToKeep.ToString();

        UpdateStrategyHelp();
        UpdateHelloStatus();
        UpdateClientPathStatus();
        UpdateBackupStatus();
        UpdateSettingsProfileStatus();
        UpdateDiagnostics();
    }

    private void UpdateStrategyHelp()
        => StrategyHelp.Text = (StrategyBox.SelectedItem as StrategyChoice)?.Help ?? string.Empty;

    private void UpdateHelloStatus()
    {
        var enrolled = _services.Hello.IsEnrolled;
        HelloStatus.Text = enrolled
            ? "Enabled. Your master password still works, and always will."
            : "Not set up. You unlock with your master password every time.";

        HelloEnableButton.IsEnabled = !enrolled;
        HelloDisableButton.IsEnabled = enrolled;
    }

    private void UpdateClientPathStatus()
    {
        var discovered = _services.RiotPaths;
        ClientPathStatus.Text = string.IsNullOrWhiteSpace(ClientPathBox.Text)
            ? "Leave blank to detect it automatically. Currently using: " + discovered.ClientServicesExe
              + (discovered.ClientExists ? "" : "   [NOT FOUND]")
            : File.Exists(ClientPathBox.Text) ? "Found." : "That file does not exist.";
    }

    private void UpdateBackupStatus()
    {
        var backups = _services.Repository.ListBackups();
        BackupStatus.Text = backups.Count == 0
            ? "No automatic backups yet."
            : backups.Count + " backup" + (backups.Count == 1 ? "" : "s") + " kept, newest "
              + backups[0].LastWriteTime.ToString("d MMM yyyy HH:mm") + ".";
    }

    private void UpdateDiagnostics()
    {
        var paths = _services.RiotPaths;
        var yaml = _services.Yaml;
        var text = new StringBuilder();

        void Line(string label, string value) => text.AppendLine(label.PadRight(24) + value);

        Line("Riot Client", paths.ClientServicesExe + (paths.ClientExists ? "  [found]" : "  [MISSING]"));
        Line("League install", paths.LeagueInstallDirectory ?? "(not registered)");
        Line("Settings file", File.Exists(paths.ClientSettingsFile) ? "found" : "missing");
        Line("Session file", File.Exists(paths.PrivateSettingsFile) ? "found" : "missing");

        var session = yaml.ReadPrivateSettings();
        var cookies = RiotYamlService.DescribeCookies(session);
        Line("Cookies stored", cookies.Count == 0 ? "(none)" : string.Join(", ", cookies));
        Line("Resumable session", RiotYamlService.ContainsSession(session) ? "yes" : "no");

        var (region, locale) = yaml.ReadRegionAndLocale();
        Line("Client region", (region ?? "?") + " / " + (locale ?? "?"));

        text.AppendLine();
        Line("Game running", _services.Processes.IsGameInProgress() ? "YES — sign-in would be refused" : "no");
        Line("Client running", _services.Processes.IsClientRunning() ? "yes" : "no");
        Line("Never touched", string.Join(", ", RiotProcessManager.ProtectedProcesses) + "  (Vanguard)");

        // An integrity-level mismatch breaks typing, window reading and closing the client all at
        // once, each with its own unhelpful error. State it plainly instead.
        text.AppendLine();
        Line("Running elevated", ElevationInfo.IsElevated ? "yes" : "no");

        var layers = ElevationInfo.RiotCompatibilityLayers();
        if (layers.Count == 0)
        {
            Line("Riot compatibility flags", "(none)");
        }
        else
        {
            text.AppendLine("  Riot compatibility flags");
            foreach (var layer in layers) text.AppendLine("    " + layer);
        }

        text.AppendLine();
        foreach (var wrapped in Wrap(ElevationInfo.DescribeCompatibility(), 74))
            text.AppendLine("  " + wrapped);

        text.AppendLine();
        Line("Vault folder", _services.Paths.Root);
        Line("Sign-in log", _services.Trace.Path_ ?? "(not writable)");
        Line("Machine-bound", _vault.BindToMachine ? "yes" : "no");

        text.AppendLine();
        text.AppendLine("If \"Resumable session\" says no even after signing in with");
        text.AppendLine("\"Stay signed in\" ticked, this client does not persist sessions and");
        text.AppendLine("every sign-in will type the password. Everything else still works.");

        DiagnosticsText.Text = text.ToString();
    }

    /// <summary>Wraps a sentence to the monospace diagnostics column so it stays readable.</summary>
    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return line.ToString();
    }

    /// <summary>
    /// Checks the key on its own against a trivial endpoint.
    ///
    /// Worth a button because "Riot rejected the API key" is ambiguous between an expired key, a key
    /// pasted short, and a fault in the request — and the three need entirely different responses.
    /// This says which, without needing a refresh to fail first.
    /// </summary>
    private async void OnTestKey(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password.Trim();

        if (string.IsNullOrEmpty(key))
        {
            KeyTestResult.Text = "No key entered.";
            return;
        }

        TestKeyButton.IsEnabled = false;
        KeyTestResult.Text = "Checking with Riot…";

        try
        {
            // Uses what is currently typed rather than what is saved, so a key can be checked before
            // committing it.
            using var client = new RiotApiClient(new SecretText(key), http: null, trace: _services.Trace);

            var region = _vault.Document.Accounts.FirstOrDefault()?.Region ?? "BR";
            var result = await client.TestKeyAsync(region, CancellationToken.None);

            KeyTestResult.Text = result.Message;
            // SetResourceReference, not FindResource: the latter snapshots the brush against
            // whichever palette is live and never follows a theme swap.
            KeyTestResult.SetResourceReference(
                System.Windows.Controls.TextBlock.ForegroundProperty, result.Ok ? "Ok" : "WarnFg");
        }
        catch (Exception ex)
        {
            KeyTestResult.Text = "Could not check the key: " + ex.Message;
            KeyTestResult.SetResourceReference(
                System.Windows.Controls.TextBlock.ForegroundProperty, "WarnFg");
        }
        finally
        {
            TestKeyButton.IsEnabled = true;
        }
    }

    private void UpdateSettingsProfileStatus()
    {
        var profile = _services.GameSettings;

        SettingsProfileStatus.Text = profile.SavedAtUtc is { } saved
            ? "Profile saved " + saved.LocalDateTime.ToString("d MMM yyyy HH:mm") + "."
            : "No settings profile saved yet.";
    }

    private void OnSaveGameSettings(object sender, RoutedEventArgs e)
    {
        var result = _services.GameSettings.Save();
        UpdateSettingsProfileStatus();

        Dialog.Say(
            this,
            "League settings",
            result.Message);
    }

    private void OnRevertGameSettings(object sender, RoutedEventArgs e)
    {
        var answer = Dialog.Confirm(
            this,
            "Revert settings",
            "Put League's settings back exactly as they were before this app first changed them?"
            + Environment.NewLine + Environment.NewLine
            + "Close League first — it holds these files open and rewrites them on exit.",
            "Revert settings");

        if (!answer) return;

        var result = _services.GameSettings.RevertToPristine();
        Dialog.Say(
            this,
            "League settings",
            result.Message);
    }

    private void OnRefreshDiagnostics(object sender, RoutedEventArgs e) => UpdateDiagnostics();

    // ---- hello -------------------------------------------------------------

    private async void OnEnableHello(object sender, RoutedEventArgs e)
    {
        if (!await HelloUnlock.IsAvailableAsync())
        {
            Dialog.Say(
                this,
                "Windows Hello",
                "Windows Hello is not set up on this PC. Add a PIN, fingerprint or face in Windows " +
                "Settings first.");
            return;
        }

        try
        {
            await _services.Hello.EnrollAsync(_vault);
            _vault.Settings.WindowsHelloEnabled = true;
            _vault.Save();
            UpdateHelloStatus();
        }
        catch (HelloUnlockException ex)
        {
            Dialog.Say(
                this,
                "Windows Hello",
                ex.Message);
        }
    }

    private async void OnDisableHello(object sender, RoutedEventArgs e)
    {
        await _services.Hello.RemoveAsync();
        _vault.Settings.WindowsHelloEnabled = false;
        _vault.Save();
        UpdateHelloStatus();
    }

    // ---- master password ---------------------------------------------------

    private void OnChangeMasterPassword(object sender, RoutedEventArgs e)
    {
        var prompt = new PassphraseWindow(
            "Change master password",
            "Enter a new master password. The vault is re-encrypted immediately and the old password stops working.",
            confirm: true) { Owner = this };

        if (prompt.ShowDialog() != true || prompt.Passphrase is null) return;

        using var password = SecretBuffer.FromString(prompt.Passphrase);
        _vault.ChangeMasterPassword(password);

        // The Hello copy was sealed against the old key, so it must be re-made or it will fail on
        // the next unlock — silently, and at the worst moment.
        if (_services.Hello.IsEnrolled)
        {
            Dialog.Say(
                this,
                "Master password",
                "Master password changed.\n\nWindows Hello has to be re-enabled, because the copy it " +
                "held was tied to your old password. Doing that now.");

            _ = ReEnrollHelloAsync();
        }
        else
        {
            Dialog.Say(
                this,
                "Master password",
                "Master password changed.");
        }
    }

    private async System.Threading.Tasks.Task ReEnrollHelloAsync()
    {
        try
        {
            await _services.Hello.RemoveAsync();
            await _services.Hello.EnrollAsync(_vault);
            _vault.Save();
        }
        catch (HelloUnlockException ex)
        {
            _vault.Settings.WindowsHelloEnabled = false;
            _vault.Save();
            Dialog.Say(
                this,
                "Windows Hello",
                ex.Message + "\n\nHello has been turned off; your master password still works.");
        }
        finally
        {
            UpdateHelloStatus();
        }
    }

    // ---- import / export ---------------------------------------------------

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export vault",
            Filter = "League Account Manager vault (*.lamvault)|*.lamvault",
            FileName = "accounts-" + DateTime.Now.ToString("yyyy-MM-dd") + ".lamvault",
        };

        if (dialog.ShowDialog(this) != true) return;

        var prompt = new PassphraseWindow(
            "Export passphrase",
            "This file is not tied to your PC, so this passphrase is the only thing protecting it. " +
            "Use a strong one, and do not reuse your master password.",
            confirm: true) { Owner = this };

        if (prompt.ShowDialog() != true || prompt.Passphrase is null) return;

        using var passphrase = SecretBuffer.FromString(prompt.Passphrase);

        try
        {
            _vault.ExportTo(dialog.FileName, passphrase);
            Dialog.Say(
                this,
                "Export",
                "Exported " + _vault.Document.Accounts.Count + " accounts.\n\n" +
                "Keep this somewhere safe — anyone with the file and the passphrase has every account in it.");
        }
        catch (Exception ex)
        {
            Dialog.Say(
                this,
                "Export failed",
                ex.Message);
        }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import vault",
            Filter = "League Account Manager vault (*.lamvault)|*.lamvault|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != true) return;

        var prompt = new PassphraseWindow(
            "Import passphrase",
            "Enter the passphrase this export was made with.",
            confirm: false) { Owner = this };

        if (prompt.ShowDialog() != true || prompt.Passphrase is null) return;

        using var passphrase = SecretBuffer.FromString(prompt.Passphrase);

        VaultDocument imported;
        try
        {
            imported = UnlockedVault.ReadExport(dialog.FileName, passphrase);
        }
        catch (Exception ex)
        {
            Dialog.Say(
                this,
                "Import failed",
                ex.Message);
            return;
        }

        // Merge rather than replace, matching on id so re-importing your own export is idempotent
        // instead of doubling everything.
        var existing = _vault.Document.Accounts.ToDictionary(a => a.Id);
        var added = 0;
        var updated = 0;

        foreach (var account in imported.Accounts)
        {
            if (existing.ContainsKey(account.Id)) updated++;
            else added++;
        }

        var answer = Dialog.Confirm(
            this,
            "Import",
            "This export holds " + imported.Accounts.Count + " accounts.\n\n" +
            added + " would be added, " + updated + " would replace an account you already have.\n\n" +
            "Continue?",
            "Import accounts");

        if (!answer) return;

        foreach (var account in imported.Accounts)
        {
            _vault.Document.Accounts.RemoveAll(a => a.Id == account.Id);
            _vault.Document.Accounts.Add(account);
        }

        _vault.Save();
        UpdateBackupStatus();

        Dialog.Say(
            this,
            "Import",
            "Imported " + imported.Accounts.Count + " accounts.");
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_services.Paths.Root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Dialog.Say(
                this,
                "Could not open the folder",
                ex.Message);
        }
    }

    private void OnBrowseClient(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Find RiotClientServices.exe",
            Filter = "RiotClientServices.exe|RiotClientServices.exe|Programs (*.exe)|*.exe",
        };

        if (dialog.ShowDialog(this) != true) return;

        ClientPathBox.Text = dialog.FileName;
        UpdateClientPathStatus();
    }

    // ---- save --------------------------------------------------------------

    private void OnDone(object sender, RoutedEventArgs e)
    {
        var settings = _vault.Settings;

        settings.AutoLockMinutes = ParseInt(AutoLockBox.Text, settings.AutoLockMinutes, 0, 720);
        settings.LockOnWorkstationLock = LockOnWorkstationBox.IsChecked == true;
        settings.LockOnMinimize = LockOnMinimizeBox.IsChecked == true;
        settings.AbortTypingOnFocusLoss = AbortOnFocusLossBox.IsChecked == true;

        if (StrategyBox.SelectedItem is StrategyChoice choice) settings.Strategy = choice.Value;

        settings.LaunchGameAfterSignIn = LaunchGameBox.IsChecked == true;
        settings.CarryGameSettings = CarrySettingsBox.IsChecked == true;
        settings.LoginWindowTimeoutSeconds = ParseInt(TimeoutBox.Text, settings.LoginWindowTimeoutSeconds, 10, 600);
        settings.RiotClientPathOverride = string.IsNullOrWhiteSpace(ClientPathBox.Text)
            ? null : ClientPathBox.Text.Trim();

        settings.RiotApiKey = string.IsNullOrWhiteSpace(ApiKeyBox.Password)
            ? null : new SecretText(ApiKeyBox.Password.Trim());

        settings.BackupsToKeep = ParseInt(BackupsBox.Text, settings.BackupsToKeep, 0, 200);

        var bind = MachineBoundBox.IsChecked == true;
        if (bind != _vault.BindToMachine) _vault.SetMachineBinding(bind);
        else _vault.Save();

        DialogResult = true;
    }

    private static int ParseInt(string text, int fallback, int min, int max)
        => int.TryParse(text, out var value) ? Math.Clamp(value, min, max) : fallback;
}
