using System.Windows;
using System.Windows.Input;
using LAM.Core.Login;

namespace LAM.App.Views;

public partial class VerificationCodeWindow : Window
{
    public VerificationCodeWindow(VerificationPrompt prompt)
    {
        InitializeComponent();

        Title = prompt.Title;
        Explanation.Text = prompt.Message;

        Loaded += (_, _) => CodeBox.Focus();
    }

    public string? Code { get; private set; }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnOk(sender, e);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CodeBox.Text)) return;
        Code = CodeBox.Text.Trim();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
