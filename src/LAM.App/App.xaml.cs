using LAM.App.Views;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace LAM.App;

public partial class App : Application
{
    // Fixed names, not path-derived: the single-file exe self-extracts to a different temp folder on
    // every launch, so anything based on the executable path would never match between two launches.
    // Global\ so it is one object across the session regardless of where each copy runs from.
    private const string InstanceMutexName = @"Global\LeagueAccountManager.instance";
    private const string ActivateSignalName = @"Global\LeagueAccountManager.activate";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateSignal;
    private volatile bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);

        if (!createdNew)
        {
            // An instance is already running. Wake its window and leave — never build a second one.
            // We hold the foreground right now (we were just launched), so grant it away first so the
            // primary's SetForegroundWindow/Activate is allowed to take it.
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateSignalName, out var existing))
                {
                    NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
                    existing.Set();
                    existing.Dispose();
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The primary is still mid-startup and hasn't created the signal yet. Nothing we can
                // do from here; it will come up on its own.
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateSignalName);

        base.OnStartup(e);

        StartActivateListener();

        // A crash with the vault open would otherwise take the whole app down and, worse, could
        // surface a stack trace to the user. Catch it, say something useful, and keep running.
        DispatcherUnhandledException += OnUnhandledException;
    }

    /// <summary>
    /// Waits for a second launch to signal, then brings the existing window to the front. One
    /// background thread for the life of the primary instance.
    /// </summary>
    private void StartActivateListener()
    {
        var listener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!_activateSignal!.WaitOne()) return;
                }
                catch (ObjectDisposedException) { return; }
                catch (AbandonedMutexException) { return; }

                if (_shuttingDown) return;

                try
                {
                    Dispatcher.Invoke(() => (MainWindow as LAM.App.Views.MainWindow)?.BringToFront());
                }
                catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException)
                {
                    // Shutting down while a signal was in flight. Nothing to show.
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "hextech-single-instance",
        };

        listener.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;

        // Wake the listener so it sees _shuttingDown and exits, then release both handles.
        try { _activateSignal?.Set(); }
        catch (ObjectDisposedException) { }

        _activateSignal?.Dispose();
        _instanceMutex?.Dispose();

        base.OnExit(e);
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

    private static class NativeMethods
    {
        public const uint ASFW_ANY = 0xFFFFFFFF;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AllowSetForegroundWindow(uint dwProcessId);
    }
}
