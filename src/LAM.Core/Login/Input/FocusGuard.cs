using System.Diagnostics;
using LAM.Core.Riot;

namespace LAM.Core.Login.Input;

/// <summary>
/// Makes sure keystrokes only ever land in the Riot Client.
///
/// Synthetic input goes to whatever has focus *at the moment it is delivered*, not to the window we
/// targeted when we started. So if you alt-tab, or a notification steals focus, the remaining
/// characters of your password get typed into whatever is in front — a chat box, a browser, a
/// stream. That is the real reason tools in this space tell you not to touch the mouse. Rather than
/// warn, we check before every keystroke and stop.
///
/// The check is on the *owning process*, not on a window handle. An earlier version pinned one HWND
/// and demanded an exact match, which aborted constantly for a reason that had nothing to do with
/// the user: the client is Electron and replaces its window during startup, so the handle changed
/// while the client sat there in the foreground the whole time. What actually matters is "are these
/// keystrokes going to the client we launched, or to something else", and that survives the client
/// recreating windows, showing a child dialog, or moving between splash and login.
/// </summary>
public sealed class FocusGuard
{
    private readonly nint _target;
    private readonly bool _enabled;
    private readonly Func<uint, string?> _resolveProcessName;

    private FocusGuard(nint target, bool enabled, Func<uint, string?>? resolveProcessName = null)
    {
        _target = target;
        _enabled = enabled;
        _resolveProcessName = resolveProcessName ?? DefaultResolveProcessName;
    }

    /// <summary>Captures the currently focused window as the preferred target.</summary>
    public static FocusGuard ForForegroundWindow(bool enabled = true)
        => new(NativeInput.GetForegroundWindow(), enabled);

    public static FocusGuard ForWindow(nint handle, bool enabled = true)
        => new(handle, enabled);

    /// <summary>A guard that permits anything. Only for tests and for the explicit opt-out setting.</summary>
    public static FocusGuard Disabled => new(nint.Zero, enabled: false);

    /// <summary>Test seam: supply the process-name lookup instead of asking Windows.</summary>
    internal static FocusGuard ForTesting(bool enabled, Func<uint, string?> resolveProcessName)
        => new(nint.Zero, enabled, resolveProcessName);

    public nint Target => _target;

    public bool IsIntact => IsAcceptable(ForegroundProcessId());

    /// <summary>
    /// Whether a foreground window owned by <paramref name="processId"/> may receive our keystrokes.
    ///
    /// Anything that is not one of our client processes is refused, including the case where the
    /// owner cannot be resolved at all. Failing closed matters here: a guard that waved through an
    /// unknown window would be worse than having no guard, because it reads as protection.
    /// </summary>
    internal bool IsAcceptable(uint processId)
    {
        if (!_enabled) return true;
        if (processId == 0) return false;

        var name = _resolveProcessName(processId);
        if (string.IsNullOrEmpty(name)) return false;

        return RiotProcessManager.ClientProcesses
            .Any(client => string.Equals(client, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Throws if focus has left the Riot Client. Called before each keystroke.</summary>
    public void Verify()
    {
        if (IsIntact) return;

        throw new FocusLostException(
            "Focus moved away from the Riot Client while signing in, so typing was stopped. " +
            "Nothing further was sent. Try again and leave the mouse and keyboard alone for a couple of seconds.");
    }

    /// <summary>Describes what currently holds focus. For the trace log, never for a secret.</summary>
    public string DescribeForeground()
    {
        var foreground = NativeInput.GetForegroundWindow();
        var pid = ForegroundProcessId();
        var name = pid == 0 ? null : _resolveProcessName(pid);

        return "hwnd=0x" + foreground.ToString("X") + " pid=" + pid + " process=" + (name ?? "?");
    }

    private static uint ForegroundProcessId()
    {
        var foreground = NativeInput.GetForegroundWindow();
        if (foreground == nint.Zero) return 0;

        NativeInput.GetWindowThreadProcessId(foreground, out var pid);
        return pid;
    }

    private static string? DefaultResolveProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Exited between the foreground query and this lookup, or not visible to us. Either way
            // we cannot vouch for it, and IsAcceptable treats null as a refusal.
            return null;
        }
    }

    /// <summary>
    /// Brings the target window to the front and confirms a Riot window ended up there.
    ///
    /// Windows refuses <c>SetForegroundWindow</c> from a process that does not own the foreground,
    /// so we briefly attach to the target's input queue — the standard workaround, and the same one
    /// automation frameworks use.
    /// </summary>
    public bool TryFocus()
    {
        if (_target == nint.Zero || !NativeInput.IsWindow(_target)) return false;
        if (IsIntact && NativeInput.GetForegroundWindow() == _target) return true;

        NativeInput.ShowWindow(_target, NativeInput.SwRestore);

        var foreground = NativeInput.GetForegroundWindow();
        var currentThread = NativeInput.GetCurrentThreadId();
        var foregroundThread = NativeInput.GetWindowThreadProcessId(foreground, out _);
        var targetThread = NativeInput.GetWindowThreadProcessId(_target, out _);

        var attachedForeground = foregroundThread != currentThread
                                 && NativeInput.AttachThreadInput(currentThread, foregroundThread, true);
        var attachedTarget = targetThread != currentThread
                             && NativeInput.AttachThreadInput(currentThread, targetThread, true);

        try
        {
            NativeInput.SetForegroundWindow(_target);
        }
        finally
        {
            if (attachedTarget) NativeInput.AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground) NativeInput.AttachThreadInput(currentThread, foregroundThread, false);
        }

        // Success is "a Riot window is in front", not "this exact handle is in front" — the client
        // may legitimately have swapped which of its windows is active while we were asking.
        return IsIntact;
    }
}

/// <summary>
/// Raised when focus left the Riot Client mid-sequence. Not an error in the usual sense — it means
/// the safety check did its job.
/// </summary>
public sealed class FocusLostException : Exception
{
    public FocusLostException(string message) : base(message) { }
}
