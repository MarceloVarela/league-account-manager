using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using LAM.Core.Model;
using LAM.Core.Riot;

namespace LAM.App.Views;

public partial class AccountEditorWindow : Window
{
    private static readonly string[] CommonLocales =
    [
        "en_US", "en_GB", "pt_BR", "es_ES", "es_MX", "fr_FR", "de_DE", "it_IT",
        "pl_PL", "ru_RU", "tr_TR", "ja_JP", "ko_KR", "zh_TW",
    ];

    /// <summary>Month options. The blank entry means "I only remember the year".</summary>
    private static readonly MonthOption[] Months =
    [
        new(null, "(month unknown)"),
        new(1, "January"), new(2, "February"), new(3, "March"), new(4, "April"),
        new(5, "May"), new(6, "June"), new(7, "July"), new(8, "August"),
        new(9, "September"), new(10, "October"), new(11, "November"), new(12, "December"),
    ];

    /// <summary>
    /// Years back to League's launch. Nothing older can be a League account, and the blank entry
    /// covers "no idea" without forcing a wrong guess into the record.
    /// </summary>
    private static readonly YearOption[] Years = BuildYears();

    private sealed record MonthOption(int? Number, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record YearOption(int? Value, string Label)
    {
        public override string ToString() => Label;
    }

    private static YearOption[] BuildYears()
    {
        var years = new List<YearOption> { new(null, "(year unknown)") };
        for (var year = DateTime.Now.Year; year >= 2009; year--)
            years.Add(new YearOption(year, year.ToString()));
        return years.ToArray();
    }

    private readonly AccountEntry _account;
    private bool _revealed;

    public AccountEditorWindow(AccountEntry account, bool isNew)
    {
        InitializeComponent();

        _account = account;
        Title = isNew ? "Add account" : "Edit " + account.DisplayRiotId;

        RegionBox.ItemsSource = RiotRegions.All;
        LocaleBox.ItemsSource = CommonLocales;

        CreatedMonthBox.ItemsSource = Months;
        CreatedYearBox.ItemsSource = Years;

        Load();
        LabelBox.Focus();
    }

    private void Load()
    {
        LabelBox.Text = _account.Label;
        UsernameBox.Text = _account.LoginUsername;
        PasswordBox.Password = _account.Password?.Value ?? string.Empty;
        RegionBox.SelectedItem = RiotRegions.IsKnown(_account.Region) ? _account.Region : "BR";
        LocaleBox.Text = _account.Locale;
        TagsBox.Text = string.Join(", ", _account.Tags);
        NotesBox.Text = _account.Notes;

        var recovery = _account.Recovery;
        RecoveryEmailBox.Text = recovery.Email ?? string.Empty;
        EmailProviderBox.Text = recovery.EmailProvider ?? string.Empty;
        PhoneBox.Text = recovery.PhoneNumber ?? string.Empty;
        EmailControlledBox.IsChecked = recovery.EmailStillControlled;
        EmailPasswordBox.Password = recovery.EmailPassword?.Value ?? string.Empty;
        var created = recovery.ApproximateCreated;
        CreatedYearBox.SelectedItem = Years.FirstOrDefault(y => y.Value == created?.Year) ?? Years[0];
        // A stored day of 1 with no month is how "year only" was saved; month 0 never occurs.
        CreatedMonthBox.SelectedItem = Months.FirstOrDefault(m => m.Number == created?.Month) ?? Months[0];
        FirstChampionBox.Text = recovery.FirstChampionPurchased ?? string.Empty;
        PurchaseReferenceBox.Text = recovery.FirstPurchaseReference ?? string.Empty;
        MfaBox.IsChecked = recovery.MfaEnabled;
        BackupCodesBox.Text = string.Join(Environment.NewLine, recovery.MfaBackupCodes.Select(c => c.Value));
        SecurityAnswersBox.Text = recovery.SecurityAnswers?.Value ?? string.Empty;
        RecoveryNotesBox.Text = recovery.Notes ?? string.Empty;

        KnownText.Text = DescribeKnown();
    }

    /// <summary>Renders the auto-captured identity as plain text — read-only by nature.</summary>
    private string DescribeKnown()
    {
        var identity = _account.Identity;
        var text = new StringBuilder();

        void Line(string label, string? value)
            => text.AppendLine(label.PadRight(22) + (string.IsNullOrWhiteSpace(value) ? "—" : value));

        Line("Riot account id", identity.RiotAccountId);
        Line("Riot ID", identity.GameName is null ? null : identity.GameName + "#" + identity.TagLine);
        Line("Summoner level", identity.SummonerLevel?.ToString());
        Line("Platform", identity.Platform);
        Line("Solo queue", identity.SoloRank?.ToString());
        Line("Flex queue", identity.FlexRank?.ToString());
        Line("Last activity", identity.LastActivityUtc?.LocalDateTime.ToString("d MMM yyyy"));
        Line("Summoner id", identity.SummonerId);
        Line("Account id", identity.AccountId);
        Line("Last refreshed", identity.LastRefreshedUtc?.LocalDateTime.ToString("d MMM yyyy HH:mm"));

        text.AppendLine();
        if (identity.OldestKnownMatchUtc is { } oldest)
        {
            Line("Oldest match", oldest.LocalDateTime.ToString("d MMM yyyy"));
            text.AppendLine(identity.AgeIsLowerBoundOnly
                ? "  (Riot's match history stops around mid-2021, so the account may be much older.)"
                : "  This is the earliest game on record.");
        }
        else
        {
            Line("Account age", "not estimated yet — use the ⋯ menu on the card");
        }

        if (identity.NameHistory.Count > 1)
        {
            text.AppendLine();
            text.AppendLine("Previous Riot IDs");
            foreach (var entry in identity.NameHistory)
                text.AppendLine("  " + entry + "   (seen " + entry.FirstSeenUtc.LocalDateTime.ToString("d MMM yyyy") + ")");
        }

        text.AppendLine();
        if (_account.Session is { } session)
        {
            text.AppendLine("Saved session captured " + session.CapturedUtc.LocalDateTime.ToString("d MMM yyyy HH:mm"));
            text.AppendLine(session.IsProbablyUsable(DateTimeOffset.UtcNow)
                ? "  Next sign-in will be instant."
                : "  Expired — the next sign-in will type the password and capture a fresh one.");
        }
        else
        {
            text.AppendLine("No saved session yet. The first sign-in types the password, then captures one.");
        }

        return text.ToString();
    }

    /// <summary>
    /// Reveals the password in place. The plain box mirrors the masked one so the value is never
    /// put on the clipboard, which other applications can read.
    /// </summary>
    private void OnToggleReveal(object sender, RoutedEventArgs e)
    {
        _revealed = !_revealed;

        if (_revealed)
        {
            PasswordPlainBox.Text = PasswordBox.Password;
            PasswordPlainBox.Visibility = Visibility.Visible;
            PasswordBox.Visibility = Visibility.Collapsed;
            RevealButton.Content = "Hide";
        }
        else
        {
            PasswordBox.Password = PasswordPlainBox.Text;
            PasswordBox.Visibility = Visibility.Visible;
            PasswordPlainBox.Visibility = Visibility.Collapsed;
            RevealButton.Content = "Show";
        }
    }

    private string CurrentPassword => _revealed ? PasswordPlainBox.Text : PasswordBox.Password;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LabelBox.Text) && string.IsNullOrWhiteSpace(UsernameBox.Text))
        {
            Fail("Give the account a name or a login username, so it can be told apart from the others.");
            return;
        }

        _account.Label = LabelBox.Text.Trim();
        _account.LoginUsername = UsernameBox.Text.Trim();

        var password = CurrentPassword;
        // Through SetPassword so the previous one is kept. A rotation typed with a typo would
        // otherwise lock the account out of this app's own record of it, with nothing to fall back on.
        _account.SetPassword(
            string.IsNullOrEmpty(password) ? null : new SecretText(password),
            DateTimeOffset.UtcNow);

        _account.Region = RegionBox.SelectedItem as string ?? "BR";
        _account.Locale = string.IsNullOrWhiteSpace(LocaleBox.Text) ? "en_US" : LocaleBox.Text.Trim();

        _account.Tags = TagsBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _account.Notes = NotesBox.Text.Trim();

        var recovery = _account.Recovery;
        recovery.Email = Nullable(RecoveryEmailBox.Text);
        recovery.EmailProvider = Nullable(EmailProviderBox.Text);
        recovery.PhoneNumber = Nullable(PhoneBox.Text);
        recovery.EmailStillControlled = EmailControlledBox.IsChecked == true;
        recovery.EmailPassword = string.IsNullOrEmpty(EmailPasswordBox.Password)
            ? null : new SecretText(EmailPasswordBox.Password);
        recovery.ApproximateCreated = ReadApproximateCreated();
        recovery.FirstChampionPurchased = Nullable(FirstChampionBox.Text);
        recovery.FirstPurchaseReference = Nullable(PurchaseReferenceBox.Text);
        recovery.MfaEnabled = MfaBox.IsChecked == true;
        recovery.MfaBackupCodes = BackupCodesBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(code => new SecretText(code))
            .ToList();
        recovery.SecurityAnswers = string.IsNullOrWhiteSpace(SecurityAnswersBox.Text)
            ? null : new SecretText(SecurityAnswersBox.Text.Trim());
        recovery.Notes = Nullable(RecoveryNotesBox.Text);

        DialogResult = true;
    }

    /// <summary>
    /// Folds the month and year pickers back into the stored <see cref="DateOnly"/>.
    ///
    /// A year with no month is recorded as January 1st of that year — the field is explicitly an
    /// approximation, and a real year is worth more to a support ticket than a null.
    /// </summary>
    private DateOnly? ReadApproximateCreated()
    {
        if (CreatedYearBox.SelectedItem is not YearOption { Value: { } year }) return null;

        var month = (CreatedMonthBox.SelectedItem as MonthOption)?.Number ?? 1;
        return new DateOnly(year, month, 1);
    }

    private static string? Nullable(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void Fail(string message)
    {
        ValidationMessage.Text = message;
        ValidationMessage.Visibility = Visibility.Visible;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
