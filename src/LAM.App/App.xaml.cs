using LAM.App.Views;
using System.Windows;
using System.Windows.Threading;

namespace LAM.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A crash with the vault open would otherwise take the whole app down and, worse, could
        // surface a stack trace to the user. Catch it, say something useful, and keep running.
        DispatcherUnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Dialog.Say(null, "Something went wrong", Describe(e.Exception));

        e.Handled = true;
    }

    /// <summary>
    /// Something a person can act on, for any exception.
    ///
    /// Written after a dialog shipped that was completely empty: a failing TPM makes Windows Hello
    /// throw a COMException whose Message is "" — length zero, not null — so the box had a title, a
    /// blank body and an OK button, and told the user nothing at all. An exception type and an
    /// HRESULT are not friendly, but they are searchable, and they beat silence.
    /// </summary>
    private static string Describe(Exception ex)
    {
        if (!string.IsNullOrWhiteSpace(ex.Message)) return ex.Message;

        var inner = ex.InnerException;
        if (inner is not null && !string.IsNullOrWhiteSpace(inner.Message)) return inner.Message;

        return ex.GetType().Name + " (0x" + ex.HResult.ToString("X8") + "), with no message. "
               + "If this keeps happening, note that code — it is the only clue Windows gave us.";
    }
}
