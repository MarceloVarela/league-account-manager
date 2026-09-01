using System.Windows;
using System.Windows.Input;

namespace LAM.App.Views;

/// <summary>Asks for a passphrase, optionally twice. Used for the master password and for exports.</summary>
public partial class PassphraseWindow : Window
{
    private readonly bool _confirm;

    public PassphraseWindow(string title, string explanation, bool confirm)
    {
        InitializeComponent();

        Title = title;
        Explanation.Text = explanation;
        _confirm = confirm;
        ConfirmPanel.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;

        Loaded += (_, _) => FirstBox.Focus();
    }

    /// <summary>Null unless the dialog was accepted.</summary>
    public string? Passphrase { get; private set; }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnOk(sender, e);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var entered = FirstBox.Password;

        if (entered.Length < 8)
        {
            Fail("Use at least 8 characters.");
            return;
        }

        if (_confirm && entered != SecondBox.Password)
        {
            Fail("The two entries do not match.");
            return;
        }

        Passphrase = entered;
        DialogResult = true;
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
