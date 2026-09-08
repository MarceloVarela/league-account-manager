using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

// Both namespaces define Application; the tray needs the WPF one to read a packed resource.
using Application = System.Windows.Application;

namespace LAM.App.Services;

/// <summary>
/// The notification-area icon, and the minimise-to-tray behaviour that gives it a purpose.
///
/// WPF has no tray icon of its own, so this wraps the WinForms <see cref="NotifyIcon"/> — the only
/// supported route, and the reason the project enables WindowsForms. Nothing else from that stack is
/// used: the flyout and the balloon are ordinary WPF windows.
///
/// The icon is the 16px SIMPLIFIED cut, not a scaled-down crest. The handoff is explicit that the
/// filigree loses definition below 32px, and the tray is the one place the app is drawn that small.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public event Action? Activated;
    public event Action? OpenRequested;
    public event Action? LockRequested;
    public event Action? ExitRequested;

    /// <summary>Raised when the status submenu is used. Null mode means the submenu is unavailable.</summary>
    public event Action<LAM.Core.Stealth.StealthMode>? StealthModeRequested;

    /// <summary>Reads the current mode so the menu can tick it. Null while stealth is switched off.</summary>
    public Func<LAM.Core.Stealth.StealthMode?>? StealthMode { get; set; }

    public TrayIcon()
    {
        _icon = new NotifyIcon
        {
            Text = "Hextech Manager",
            Visible = true,
            Icon = LoadIcon(),
        };

        // Left click raises the flyout; the menu carries the rest, because a tray icon whose only
        // gesture is a click is undiscoverable.
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) Activated?.Invoke();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Quick swap", null, (_, _) => Activated?.Invoke());
        menu.Items.Add("Open manager", null, (_, _) => OpenRequested?.Invoke());

        // Appear as: the whole point of putting it here is changing status without leaving the game,
        // so it sits one right-click away rather than behind the app window.
        var appear = new ToolStripMenuItem("Appear as");

        foreach (var mode in new[]
                 {
                     LAM.Core.Stealth.StealthMode.Online,
                     LAM.Core.Stealth.StealthMode.Offline,
                     LAM.Core.Stealth.StealthMode.Mobile,
                 })
        {
            var captured = mode;

            appear.DropDownItems.Add(new ToolStripMenuItem(
                mode.ToString(), null, (_, _) => StealthModeRequested?.Invoke(captured)));
        }

        // Ticked and enabled state are decided when it opens, because both depend on settings that
        // can change while the icon is just sitting there.
        appear.DropDownOpening += (_, _) =>
        {
            var current = StealthMode?.Invoke();

            appear.Enabled = current is not null;

            foreach (ToolStripMenuItem item in appear.DropDownItems)
            {
                item.Checked = current is not null
                               && string.Equals(item.Text, current.ToString(), StringComparison.Ordinal);
            }
        };

        menu.Items.Add(appear);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Lock vault", null, (_, _) => LockRequested?.Invoke());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _icon.ContextMenuStrip = menu;
    }

    private static Icon LoadIcon()
    {
        try
        {
            var stream = Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/hextech.ico"))?.Stream;

            if (stream is not null) return new Icon(stream, new Size(16, 16));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            // Fall through: a missing icon must not stop the app from reaching the tray at all.
        }

        return SystemIcons.Application;
    }

    /// <summary>The "you're in" notification. Silent, because a login the user just asked for is not news.</summary>
    public void Notify(string title, string message)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = ToolTipIcon.None;
        _icon.ShowBalloonTip(4000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
