using System;
using System.IO;
using System.Windows;
using LAM.Core.Model;
using LAM.Core.Recovery;
using Microsoft.Win32;

namespace LAM.App.Views;

public partial class RecoverySheetWindow : Window
{
    private readonly AccountEntry _account;
    private readonly Func<int, string>? _championName;

    public RecoverySheetWindow(AccountEntry account, Func<int, string>? championName = null)
    {
        InitializeComponent();

        _account = account;
        _championName = championName;
        Title = "Recovery sheet — " + account.DisplayRiotId;
        Render();
    }

    private void Render()
        => SheetText.Text = RecoverySheet.Render(_account, IncludeSecretsBox.IsChecked == true, _championName);

    private void OnToggleSecrets(object sender, RoutedEventArgs e) => Render();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(SheetText.Text);
            if (IncludeSecretsBox.IsChecked == true)
            {
                Dialog.Say(
                    this,
                    "Copied",
                    "Copied — including passwords. Anything on the clipboard can be read by other " +
                    "programs, so paste it where you need it and then copy something else.");
            }
        }
        catch (Exception ex)
        {
            Dialog.Say(
                this,
                "Could not copy",
                ex.Message);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var safeName = string.Join("_", _account.DisplayRiotId.Split(Path.GetInvalidFileNameChars()));

        var dialog = new SaveFileDialog
        {
            Title = "Save recovery sheet",
            Filter = "Text file (*.txt)|*.txt",
            FileName = "recovery-" + safeName + ".txt",
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, SheetText.Text);

            if (IncludeSecretsBox.IsChecked == true)
            {
                Dialog.Say(
                    this,
                    "Saved",
                    "Saved — including passwords, in plain text. This file has none of the vault's " +
                    "protection, so store it somewhere encrypted or delete it once you are done.");
            }
        }
        catch (Exception ex)
        {
            Dialog.Say(
                this,
                "Could not save",
                ex.Message);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
