using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using LAM.Core.Model;
using LAM.Core.Vault;

namespace LAM.App.Views;

/// <summary>
/// The vault's own state: what is wrong with it, what is in the trash, and whether the backups
/// would actually survive the thing they exist for.
/// </summary>
public partial class VaultWindow : Window
{
    private readonly UnlockedVault _vault;
    private readonly VaultRepository _repository;

    public VaultWindow(UnlockedVault vault, VaultRepository repository)
    {
        InitializeComponent();

        _vault = vault;
        _repository = repository;
        Loaded += (_, _) => Render();
    }

    private void Render()
    {
        var now = DateTimeOffset.UtcNow;
        var backups = _repository.ListBackups();

        var findings = VaultHealth.Inspect(
            _vault.Document,
            backups,
            now,
            _repository.Paths.BackupDirectory,
            Path.GetDirectoryName(_repository.Paths.VaultFile));

        FindingsList.ItemsSource = findings.Select(f => new FindingRow(f)).ToList();

        var live = _vault.Document.Live.Count();
        HeadlineText.Text = findings.Count == 0
            ? live + " accounts, and nothing to flag."
            : live + " accounts. " + findings.Count + " thing" + (findings.Count == 1 ? "" : "s") + " worth a look.";

        RenderTrash();
        RenderBackups(backups, now);
    }

    private void RenderTrash()
    {
        var trashed = _vault.Document.Trashed
            .OrderByDescending(a => a.DeletedUtc)
            .Select(a => new TrashRow(a))
            .ToList();

        TrashList.ItemsSource = trashed;
        TrashEmptyText.Visibility = trashed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderBackups(IReadOnlyList<FileInfo> backups, DateTimeOffset now)
    {
        var text = new StringBuilder();

        text.AppendLine("A backup is taken automatically before every save, and the oldest are pruned.");
        text.AppendLine();

        if (backups.Count == 0)
        {
            text.AppendLine("No backups yet — one appears the next time anything changes.");
        }
        else
        {
            text.AppendLine(backups.Count + " backups, newest first:");
            foreach (var backup in backups.OrderByDescending(b => b.LastWriteTimeUtc).Take(10))
            {
                text.AppendLine("  " + backup.LastWriteTime.ToString("d MMM yyyy HH:mm").PadRight(22)
                                + (backup.Length / 1024) + " KB");
            }
        }

        text.AppendLine();
        text.AppendLine("Folder: " + _repository.Paths.BackupDirectory);
        text.AppendLine();
        text.AppendLine("These sit beside the vault, so they cover a bad write and nothing more —");
        text.AppendLine("a failed disk, a ransomware run or a wiped profile would take the vault and");
        text.AppendLine("every backup with it. Copying this folder somewhere else occasionally is the");
        text.AppendLine("difference between an inconvenience and losing every password at once.");

        BackupText.Text = text.ToString();
    }

    // ---- actions -----------------------------------------------------------

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AccountEntry account }) return;

        account.DeletedUtc = null;
        _vault.Save();
        Render();
        StatusText.Text = "Restored " + account.DisplayRiotId + ".";
    }

    /// <summary>
    /// Permanently deletes a trashed account. The only irreversible action in the window, so it says
    /// exactly what goes with it rather than asking a generic "are you sure".
    /// </summary>
    private void OnPurge(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AccountEntry account }) return;

        var answer = MessageBox.Show(
            "Permanently delete " + account.DisplayRiotId + "?\n\n" +
            "This destroys the stored password, the saved session and the whole recovery dossier — " +
            "the recovery details cannot be rebuilt by signing in again. There is no undo.\n\n" +
            "The Riot account itself is untouched.",
            "Purge account",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        _vault.Document.Accounts.RemoveAll(a => a.Id == account.Id);
        _vault.Save();
        Render();
        StatusText.Text = "Purged " + account.DisplayRiotId + ".";
    }

    private void OnOpenBackups(object sender, RoutedEventArgs e)
    {
        var folder = _repository.Paths.BackupDirectory;

        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception
                                       or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not open " + folder;
        }
    }
}

/// <summary>One health finding, with the colour its severity earns.</summary>
public sealed class FindingRow
{
    public FindingRow(HealthFinding finding)
    {
        Title = finding.Title;
        Detail = finding.Detail;

        (Badge, Accent) = finding.Severity switch
        {
            HealthSeverity.Serious => ("SERIOUS", (Brush)new SolidColorBrush(Color.FromRgb(0xE5, 0x5B, 0x5B))),
            HealthSeverity.Warning => ("WORTH FIXING", new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30))),
            _ => ("NOTE", new SolidColorBrush(Color.FromRgb(0x6A, 0x7A, 0x90))),
        };

        Accent.Freeze();
    }

    public string Title { get; }
    public string Detail { get; }
    public string Badge { get; }
    public Brush Accent { get; }
}

/// <summary>One trashed account, with enough context to decide whether to restore it.</summary>
public sealed class TrashRow
{
    public TrashRow(AccountEntry account)
    {
        Account = account;
        Title = account.DisplayRiotId;

        var deleted = account.DeletedUtc?.LocalDateTime.ToString("d MMM yyyy") ?? "unknown date";
        var bits = new List<string> { "trashed " + deleted };

        if (account.Password is { IsEmpty: false }) bits.Add("password stored");
        if (account.Session is not null) bits.Add("session stored");
        if (!string.IsNullOrWhiteSpace(account.Recovery.Email)) bits.Add("recovery email stored");
        if (account.Identity.OwnedSkinIds.Length > 0)
            bits.Add(account.Identity.OwnedSkinIds.Length + " skins recorded");

        Detail = string.Join(" · ", bits);
    }

    public AccountEntry Account { get; }
    public string Title { get; }
    public string Detail { get; }
}
