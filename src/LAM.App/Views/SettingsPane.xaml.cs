using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LAM.App.Services;
using LAM.Core.Vault;

namespace LAM.App.Views;

/// <summary>
/// Settings, laid out as the design's four groups.
///
/// Two rows are drawn but disabled: auto-accept queue, which was ruled out, and stealth login, which
/// is deferred. They are shown rather than omitted so the panel matches the design and so the
/// decision stays visible instead of quietly disappearing.
/// </summary>
public partial class SettingsPane : UserControl
{
    private readonly UnlockedVault _vault;
    private readonly AppServices _services;

    public SettingsPane(UnlockedVault vault, AppServices services)
    {
        InitializeComponent();

        _vault = vault;
        _services = services;

        Loaded += (_, _) => Render();
    }

    private void Say(string message) => StatusText.Text = message;

    private void Save()
    {
        try { _vault.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say("Could not save (" + ex.GetType().Name + ").");
        }
    }

    private void Render()
    {
        var settings = _vault.Settings;
        var backups = _services.Repository.ListBackups();

        var health = VaultHealth.Inspect(
            _vault.Document, backups, DateTimeOffset.UtcNow,
            _services.Repository.Paths.BackupDirectory,
            Path.GetDirectoryName(_services.Repository.Paths.VaultFile));

        Groups.ItemsSource = new List<SettingGroupView>
        {
            new("VAULT",
            [
                SettingRowView.Press("master password", "there is no reset — write it down",
                    "change", () => Say("Master password change opens from the lock screen.")),
                SettingRowView.Toggle("windows hello", "a second door to the same key",
                    _services.Hello.IsEnrolled, _ => { }, enabled: false,
                    tip: "Enrolled during unlock."),
                SettingRowView.Toggle("lock when windows locks", "clears the key from memory",
                    settings.LockOnWorkstationLock, on => { settings.LockOnWorkstationLock = on; Save(); }),
                SettingRowView.Toggle("lock on minimise", "for a shared desk",
                    settings.LockOnMinimize, on => { settings.LockOnMinimize = on; Save(); }),
            ]),

            new("LOGIN",
            [
                SettingRowView.Toggle("launch league after sign-in", "presses play for you",
                    settings.LaunchGameAfterSignIn,
                    on => { settings.LaunchGameAfterSignIn = on; Save(); }),
                SettingRowView.Toggle("dark theme", "the accent lifts so it stays readable on dark",
                    settings.UseDarkTheme,
                    on =>
                    {
                        settings.UseDarkTheme = on;
                        Save();

                        var theme = on ? AppTheme.Dark : AppTheme.Light;
                        ThemeSwitcher.Apply(theme);
                        ThemePreference.Write(_services.Paths.Root, theme);
                    }),
                SettingRowView.Toggle("minimize to tray", "keeps it out of the taskbar",
                    settings.MinimizeToTray,
                    on => { settings.MinimizeToTray = on; Save(); }),
                SettingRowView.Readout("quick swap hotkey", "raises the flyout from anywhere",
                    "alt + \\"),
                SettingRowView.Toggle("lobby chat while hidden",
                    "keeps champion select and lobby chat working",
                    settings.LobbyChat,
                    on => { settings.LobbyChat = on; Save(); },
                    enabled: LAM.Core.Stealth.StealthEndpoints.Configured,
                    tip: "The presence that makes lobby and champion-select chat work is addressed at "
                         + "the room rather than at your friends list, so it is passed through "
                         + "untouched — which means a lobby can see a status your friends list is "
                         + "not being told." + Environment.NewLine + Environment.NewLine
                         + "Turn this off to hide that too. Lobby and champion-select chat "
                         + "then stop working entirely."),

                SettingRowView.Toggle("hide names while streaming", "redacts when capture software runs",
                    settings.RedactWhileStreaming,
                    on => { settings.RedactWhileStreaming = on; Save(); }),

                SettingRowView.Toggle("stealth login", StealthNote(),
                    settings.StealthLogin,
                    on => { settings.StealthLogin = on; Save(); },
                    enabled: LAM.Core.Stealth.StealthEndpoints.Configured,
                    tip: StealthTip()),

                SettingRowView.Press("appear as",
                    "what the friends list is told while stealth is on",
                    settings.StealthMode.ToString().ToLowerInvariant(),
                    () =>
                    {
                        // Three states, so a button that cycles beats a dropdown for one setting.
                        settings.StealthMode = settings.StealthMode switch
                        {
                            LAM.Core.Stealth.StealthMode.Offline => LAM.Core.Stealth.StealthMode.Mobile,
                            LAM.Core.Stealth.StealthMode.Mobile => LAM.Core.Stealth.StealthMode.Online,
                            _ => LAM.Core.Stealth.StealthMode.Offline,
                        };

                        Save();
                        Render();
                    }),
                SettingRowView.Toggle("auto-accept queue", "accepts the ready check for you",
                    false, _ => { }, enabled: false,
                    tip: "Deliberately not built. It plays the game on your behalf, which is the "
                         + "line this app does not cross."),
            ]),

            new("DATA",
            [
                SettingRowView.Readout("vault file", "encrypted with AES-GCM",
                    Shorten(_services.Repository.Paths.VaultFile)),
                SettingRowView.Readout("backups", backups.Count == 0
                        ? "taken before every save"
                        : "newest " + backups.Max(b => b.LastWriteTime).ToString("d MMM HH:mm"),
                    backups.Count + " kept"),
                SettingRowView.Press("backup folder", "copy it somewhere off this machine",
                    "open", OpenBackups),
                SettingRowView.Readout("health", "checked locally, nothing sent anywhere",
                    health.Count == 0 ? "all clear" : health.Count + " to look at"),
                SettingRowView.Press("advanced",
                    "sign-in strategy, idle lock, Hello, machine binding, client paths",
                    "open", OpenAdvanced),
            ]),

            new("CLIENT",
            [
                SettingRowView.Readout("riot client", "discovered from the install manifest",
                    Shorten(_services.RiotPaths.ClientServicesExe)),
                SettingRowView.Readout("league install", "where the lockfile is read from",
                    Shorten(_services.RiotPaths.LeagueInstallDirectory ?? "not found")),
                SettingRowView.Readout("integrity", "must match the client, or input is refused",
                    LAM.Core.Windows.ElevationInfo.IsElevated ? "elevated" : "normal"),
                SettingRowView.Readout("access", "no injection, no memory reads", "lcu only"),
                SettingRowView.Press("riot api key",
                    _vault.Settings.RiotApiKey is null
                        ? "not set - REFRESH RANKS cannot reach the ranked ladder without it"
                        : "stored in the vault, sent only to Riot",
                    _vault.Settings.RiotApiKey is null ? "add" : "change",
                    OpenAdvanced),
            ]),
        };

        Say(health.Count == 0
            ? "Vault healthy. " + _vault.Document.Live.Count() + " accounts stored."
            : string.Join("   ·   ", health.Take(3).Select(h => h.Title.ToUpperInvariant())));
    }

    /// <summary>
    /// Opens the full settings window.
    ///
    /// It holds fourteen settings this pane does not — the sign-in strategy, the idle-lock interval,
    /// Windows Hello enrolment, machine binding, the client paths and the Riot API key — and until
    /// now nothing in the new shell opened it: OnOpenSettings had no caller in any XAML, so those
    /// fourteen were unreachable in the running app. Restyling it is separate work; being able to
    /// reach it is not optional.
    /// </summary>
    private void OpenAdvanced()
    {
        var owner = Window.GetWindow(this);

        var window = new SettingsWindow(_vault, _services) { Owner = owner };
        window.ShowDialog();

        _vault.Save();
        _vault.StartIdleTimer();

        // Re-read: the window may have changed a value this pane also shows.
        Render();
    }

    /// <summary>The row's own note, which has to tell the truth about an unconfigured build.</summary>
    private static string StealthNote()
        => LAM.Core.Stealth.StealthEndpoints.Configured
            ? "your friends list is told you are offline"
            : "unavailable in this build — no certificate is configured";

    /// <summary>
    /// What the user is actually agreeing to.
    ///
    /// The previous version of this row asserted that "Riot's terms prohibit intercepting client
    /// traffic". That was a recorded decision, so it is replaced rather than quietly deleted — and
    /// replaced with the actual record, which is less definite in both directions than that sentence
    /// implied: there is no published Riot approval of this technique, and no documented enforcement
    /// against it either, after years of it being widely used.
    /// </summary>
    private static string StealthTip()
        => LAM.Core.Stealth.StealthEndpoints.Configured
            ? "Stands between the League client and Riot's chat server on this machine and rewrites "
              + "your status. It reads no game memory, changes no game files and never touches "
              + "anti-cheat.\n\nRiot has never published a position on this technique, and there is "
              + "no documented case of it being actioned. That is not the same as approval. Your call."
            : "This build has no stealth certificate configured, so the option cannot be switched on. "
              + "Signing in is unaffected.";

    private static string Shorten(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Collapsing the profile keeps the Windows username off a screen that may be shared or
        // screenshotted, and the path stays recognisable.
        return path.StartsWith(profile, StringComparison.OrdinalIgnoreCase)
            ? "~" + path[profile.Length..]
            : path;
    }

    private void OpenBackups()
    {
        var folder = _services.Repository.Paths.BackupDirectory;

        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception
                                       or UnauthorizedAccessException)
        {
            Say("Could not open " + folder);
        }
    }
}

public sealed record SettingGroupView(string Title, IReadOnlyList<SettingRowView> Rows);

/// <summary>One row: a toggle, a read-only value, or an action.</summary>
public sealed class SettingRowView : INotifyPropertyChanged
{
    private bool _isOn;
    private Action<bool>? _changed;

    private SettingRowView(string label, string note) { Label = label; Note = note; }

    public static SettingRowView Toggle(
        string label, string note, bool on, Action<bool> changed,
        bool enabled = true, string? tip = null)
        => new(label, note)
        {
            IsToggle = true,
            _isOn = on,
            _changed = changed,
            IsEnabled = enabled,
            Tip = tip,
        };

    public static SettingRowView Readout(string label, string note, string value)
        => new(label, note) { IsValue = true, Value = value };

    public static SettingRowView Press(string label, string note, string actionLabel, Action run)
        => new(label, note) { IsAction = true, ActionLabel = actionLabel, Action = new Relay(run) };

    public string Label { get; }
    public string Note { get; }

    public bool IsToggle { get; private init; }
    public bool IsValue { get; private init; }
    public bool IsAction { get; private init; }

    public bool IsEnabled { get; private init; } = true;
    public string? Tip { get; private init; }
    public string Value { get; private init; } = string.Empty;
    public string ActionLabel { get; private init; } = string.Empty;
    public ICommand? Action { get; private init; }


    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value) return;

            _isOn = value;
            _changed?.Invoke(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOn)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private sealed class Relay(Action run) : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
