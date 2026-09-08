using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace LAM.App.Services;

/// <summary>
/// A system-wide hotkey — <c>alt + \</c> by default — that raises the quick-swap flyout from
/// anywhere, including from inside the game.
///
/// System-wide is the whole point: the flyout exists to swap accounts without hunting for the
/// window, so a key that only works when the app already has focus would be useless. That means
/// <c>RegisterHotKey</c> rather than a WPF InputBinding, and it means the registration can legitimately
/// fail — another application may already own the combination. A failure is reported, never thrown:
/// losing a convenience must not stop the app from starting.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly int _id = 0x4C41;   // "LA" — arbitrary, just has to be unique per window.
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _registered;

    public event Action? Pressed;

    /// <summary>True when the key is ours. False means something else already owns it.</summary>
    public bool IsRegistered => _registered;

    public void Attach(Window window)
    {
        _handle = new WindowInteropHelper(window).EnsureHandle();

        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(Hook);

        // MOD_NOREPEAT, or holding the combination fires continuously.
        var key = KeyInterop.VirtualKeyFromKey(Key.OemBackslash);

        _registered = RegisterHotKey(_handle, _id, ModAlt | ModNoRepeat, (uint)key);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey || wParam.ToInt32() != _id) return IntPtr.Zero;

        handled = true;
        Pressed?.Invoke();

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered) UnregisterHotKey(_handle, _id);

        _source?.RemoveHook(Hook);
        _source = null;
        _registered = false;
    }
}
