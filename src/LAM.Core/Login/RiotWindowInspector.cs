using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace LAM.Core.Login;

/// <summary>
/// Finds the Riot Client window and the controls on its login page.
///
/// The client is an Electron app, so its UI is a web page rather than native controls. Electron only
/// builds an accessibility tree once a UI Automation client attaches to it, which is what
/// constructing <see cref="UIA3Automation"/> does — but the tree takes a moment to appear, so every
/// lookup here polls rather than asking once.
///
/// When the tree never materialises the caller falls back to blind, tab-ordered typing. That is why
/// this class returns nulls instead of throwing: "I could not see the form" is an expected outcome
/// with a working answer, not an error.
/// </summary>
public sealed class RiotWindowInspector : IDisposable
{
    private readonly LoginUiProfile _profile;
    private readonly UIA3Automation _automation;

    public RiotWindowInspector(LoginUiProfile profile)
    {
        _profile = profile;
        _automation = new UIA3Automation();
    }

    /// <summary>
    /// Waits for the Electron client's main window.
    ///
    /// The client runs as several processes named "Riot Client" — one main plus renderers — and only
    /// one of them owns a window, so we look for a real window handle rather than trusting the
    /// first matching process.
    /// </summary>
    public async Task<Window?> WaitForClientWindowAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var process in Process.GetProcessesByName("Riot Client"))
            {
                try
                {
                    if (process.MainWindowHandle == nint.Zero) continue;

                    var element = _automation.FromHandle(process.MainWindowHandle);
                    var window = element?.AsWindow();
                    if (window is not null && !window.IsOffscreen) return window;
                }
                catch (Exception ex) when (ex is InvalidOperationException or COMException)
                {
                    // The process exited between enumeration and inspection.
                }
                finally
                {
                    process.Dispose();
                }
            }

            await Task.Delay(500, cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// Waits for the login fields, re-finding the client window on every poll.
    ///
    /// Re-finding matters: the client is Electron and swaps windows while it starts up, so a handle
    /// captured at second zero can be dead by the time the login page exists. Whatever window the
    /// client currently owns is the one asked, and the window that answered is returned alongside
    /// the fields so the caller focuses the right thing.
    ///
    /// Matching is by automation id, name and help text against the configured hints. If the hints
    /// find nothing but the page has exactly two edit boxes, we take them in document order — a
    /// login form with two fields is not ambiguous, and this keeps working when Riot renames things,
    /// which it does: the client reports its window class and title as single characters.
    /// </summary>
    /// <returns>
    /// The window in use, and the fields if they were found. A null form means the accessibility
    /// tree never produced them and the caller should fall back to tab order — not that the client
    /// is missing.
    /// </returns>
    public async Task<(Window? Window, LoginFormElements? Form)> WaitForLoginFormAsync(
        Window? window, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var current = window;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A window that has gone away yields nothing useful; ask for the current one instead.
            if (current is null || !IsAlive(current))
                current = await WaitForClientWindowAsync(TimeSpan.FromSeconds(5), cancellationToken);

            if (current is not null)
            {
                var form = TryReadLoginForm(current);
                if (form is not null) return (current, form);
            }

            await Task.Delay(500, cancellationToken);
        }

        return (current, null);
    }

    private static bool IsAlive(Window window)
    {
        try { return window.IsAvailable; }
        catch (Exception) { return false; }
    }

    /// <summary>The window's native handle, or zero. For focusing and for the trace log.</summary>
    public static nint HandleOf(Window? window)
    {
        try { return window?.Properties.NativeWindowHandle.ValueOrDefault ?? nint.Zero; }
        catch (Exception) { return nint.Zero; }
    }

    public LoginFormElements? TryReadLoginForm(Window window)
    {
        try
        {
            var edits = window
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
                .Where(IsUsable)
                .ToList();

            if (edits.Count == 0) return null;

            var username = edits.FirstOrDefault(e => Matches(e, _profile.UsernameHints));
            var password = edits.FirstOrDefault(e => Matches(e, _profile.PasswordHints))
                           ?? edits.FirstOrDefault(IsPasswordField);

            if (username is null && password is null && edits.Count == 2)
            {
                username = edits[0];
                password = edits[1];
            }
            else if (username is null && password is not null && edits.Count >= 2)
            {
                username = edits.FirstOrDefault(e => !ReferenceEquals(e, password));
            }

            if (username is null || password is null) return null;

            var staySignedIn = window
                .FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox))
                .FirstOrDefault(e => Matches(e, _profile.StaySignedInHints))
                ?? window.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox)).FirstOrDefault();

            return new LoginFormElements(
                username, password, staySignedIn?.AsCheckBox(), FindSubmitButton(window, password));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or TimeoutException)
        {
            // The page re-rendered underneath us; the caller's poll will try again.
            return null;
        }
    }

    /// <summary>
    /// Collects the visible text of the window, used to tell "wrong password" from "enter your
    /// verification code" from "you are being rate limited".
    /// </summary>
    public string ReadVisibleText(Window window)
    {
        try
        {
            var parts = window
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Select(e => SafeName(e))
                .Where(text => !string.IsNullOrWhiteSpace(text));

            return string.Join(" │ ", parts);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or TimeoutException)
        {
            return string.Empty;
        }
    }

    /// <summary>Finds a code entry field that appeared after submitting credentials.</summary>
    public AutomationElement? TryFindVerificationField(Window window)
    {
        try
        {
            return window
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
                .Where(IsUsable)
                .FirstOrDefault(e => string.IsNullOrEmpty(SafeValue(e)));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Locates the button that submits the form, deferring the actual choice to
    /// <see cref="SubmitButtonChooser"/> so the rules are testable.
    ///
    /// Returns null when no button can be identified safely. That is a good outcome, not a failure:
    /// the caller falls back to pressing Enter, which is far better than clicking a button we could
    /// not identify on a page full of identity-provider logins.
    /// </summary>
    private AutomationElement? FindSubmitButton(Window window, AutomationElement password)
    {
        try
        {
            var buttons = window
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Where(IsUsable)
                .ToList();

            if (buttons.Count == 0) return null;

            var candidates = buttons.Select(DescribeButton).ToList();
            var passwordTop = BoundsOf(password).Top;

            var index = SubmitButtonChooser.Choose(candidates, passwordTop, _profile);
            return index is null ? null : buttons[index.Value];
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Metadata for one button, shared by the submit and Play choosers.</summary>
    public static ButtonCandidate DescribeButton(AutomationElement element)
    {
        var bounds = BoundsOf(element);
        return new ButtonCandidate(
            SafeName(element),
            SafeAutomationId(element),
            SafeHelpText(element),
            IsEnabledSafe(element),
            bounds.Top,
            bounds.Width * bounds.Height);
    }

    private static System.Drawing.Rectangle BoundsOf(AutomationElement element)
    {
        try { return element.BoundingRectangle; }
        catch (Exception) { return System.Drawing.Rectangle.Empty; }
    }

    private static bool IsEnabledSafe(AutomationElement element)
    {
        try { return element.Properties.IsEnabled.ValueOrDefault; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Describes the page's interactive controls for the trace log.
    ///
    /// Deliberately metadata only — control type, name, automation id, enabled state and bounds.
    /// Never element values: a field's value is the username, or the password mask, and the log file
    /// is not encrypted. This exists so the selectors above can be corrected from what the client
    /// actually exposes rather than from guesswork, since it obfuscates its window class and title.
    /// </summary>
    public IReadOnlyList<string> DescribePageControls(Window window)
    {
        var lines = new List<string>();

        void Collect(ControlType type, string label)
        {
            try
            {
                foreach (var element in window.FindAllDescendants(cf => cf.ByControlType(type)))
                {
                    var bounds = BoundsOf(element);
                    lines.Add(
                        "  " + label
                        + " name='" + (SafeName(element) ?? "") + "'"
                        + " id='" + (SafeAutomationId(element) ?? "") + "'"
                        + " help='" + (SafeHelpText(element) ?? "") + "'"
                        + " enabled=" + IsEnabledSafe(element)
                        + " at=" + bounds.Left + "," + bounds.Top
                        + " size=" + bounds.Width + "x" + bounds.Height);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or TimeoutException)
            {
                lines.Add("  " + label + " <could not be read>");
            }
        }

        Collect(ControlType.Edit, "Edit    ");
        Collect(ControlType.Button, "Button  ");
        Collect(ControlType.CheckBox, "CheckBox");

        return lines;
    }

    private bool Matches(AutomationElement element, IEnumerable<string> hints)
    {
        var candidates = new[] { SafeAutomationId(element), SafeName(element), SafeHelpText(element) };
        return candidates.Any(candidate => _profile.MatchesAny(candidate, hints));
    }

    /// <summary>
    /// A password box reports its value as a run of mask characters, or refuses to report it at all.
    /// Either is a strong signal when the hints come up empty.
    /// </summary>
    private static bool IsPasswordField(AutomationElement element)
    {
        try
        {
            if (element.Patterns.Value.IsSupported)
            {
                var value = element.Patterns.Value.Pattern.Value.ValueOrDefault;
                if (!string.IsNullOrEmpty(value) && value.All(c => c is '•' or '*' or '●'))
                    return true;
            }

            return element.Properties.IsPassword.ValueOrDefault;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsUsable(AutomationElement element)
    {
        try
        {
            return element.IsAvailable && !element.IsOffscreen && element.Properties.IsEnabled.ValueOrDefault;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? SafeName(AutomationElement element)
    {
        try { return element.Properties.Name.ValueOrDefault; } catch { return null; }
    }

    private static string? SafeAutomationId(AutomationElement element)
    {
        try { return element.Properties.AutomationId.ValueOrDefault; } catch { return null; }
    }

    private static string? SafeHelpText(AutomationElement element)
    {
        try { return element.Properties.HelpText.ValueOrDefault; } catch { return null; }
    }

    private static string? SafeValue(AutomationElement element)
    {
        try
        {
            return element.Patterns.Value.IsSupported
                ? element.Patterns.Value.Pattern.Value.ValueOrDefault
                : null;
        }
        catch { return null; }
    }

    public void Dispose() => _automation.Dispose();
}

/// <summary>The controls we need on the login page. Any of them may be null in blind mode.</summary>
public sealed record LoginFormElements(
    AutomationElement Username,
    AutomationElement Password,
    CheckBox? StaySignedIn,
    AutomationElement? Submit);

