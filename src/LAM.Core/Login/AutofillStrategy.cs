using FlaUI.Core.AutomationElements;
using LAM.Core.Login.Input;
using LAM.Core.Model;
using LAM.Core.Riot;
using LAM.Core.Vault;

namespace LAM.Core.Login;

/// <summary>
/// Signs in by typing the stored credentials into the Riot Client's login form.
///
/// This is the universal path: it works for any account, including one whose saved session has
/// expired, and it is what bootstraps the fast path — after it succeeds, the session it produced is
/// captured and every later login for that account skips typing entirely.
///
/// It is also the riskier of the two mechanisms, so the safety work sits here. Before every single
/// keystroke the guard confirms the foreground window still belongs to the Riot Client, and focus
/// is taken only once the fields have been located — not across the twenty-second wait for the
/// client to start, which is what previously made the guard abort on a client that had merely
/// swapped its own window. A stale session is cleared beforehand so the client actually presents a
/// form instead of booting straight into a lobby.
/// </summary>
public sealed class AutofillStrategy : ILoginStrategy
{
    private readonly RiotPaths _paths;
    private readonly RiotYamlService _yaml;
    private readonly RiotProcessManager _processes;
    private readonly RiotLauncher _launcher;
    private readonly PostLoginCapture _capture;
    private readonly LoginUiProfile _profile;
    private readonly LoginTrace _trace;

    /// <summary>The page's visible text immediately before submitting, for captcha diffing.</summary>
    private string _preSubmitText = string.Empty;

    public AutofillStrategy(
        RiotPaths paths,
        RiotYamlService yaml,
        RiotProcessManager processes,
        RiotLauncher launcher,
        PostLoginCapture capture,
        LoginUiProfile profile,
        LoginTrace? trace = null)
    {
        _paths = paths;
        _yaml = yaml;
        _processes = processes;
        _launcher = launcher;
        _capture = capture;
        _profile = profile;
        _trace = trace ?? LoginTrace.Null;
    }

    public string Name => "autofill";

    public bool CanAttempt(LoginContext context)
    {
        if (context.Settings.Strategy == StrategyPreference.SessionSwapOnly) return false;
        return context.Account.Password is { IsEmpty: false }
               && !string.IsNullOrWhiteSpace(context.Account.LoginUsername);
    }

    public async Task<LoginResult> LoginAsync(LoginContext context, CancellationToken cancellationToken)
    {
        var account = context.Account;

        if (account.Password is null || account.Password.IsEmpty)
            return LoginResult.Failed("No password is saved for this account, so it cannot be typed in.");

        context.Report(LoginStage.CheckingGameNotRunning, "Checking no game is running…");
        if (_processes.IsGameInProgress()) throw new GameInProgressException();

        context.Report(LoginStage.ClosingClients, "Closing the Riot Client…");
        await _processes.CloseClientsAsync(cancellationToken);

        PrepareSessionState(context);
        ApplyRegion(context);

        context.Report(LoginStage.LaunchingClient, "Starting the Riot Client…");
        _launcher.Launch();

        using var inspector = new RiotWindowInspector(_profile);

        context.Report(LoginStage.WaitingForClient, "Waiting for the sign-in window…");
        var window = await inspector.WaitForClientWindowAsync(
            TimeSpan.FromSeconds(context.Settings.LoginWindowTimeoutSeconds), cancellationToken);

        if (window is null)
        {
            _trace.Write("client window never appeared");
            return LoginResult.Failed("The Riot Client sign-in window never appeared.");
        }

        _trace.Write("client window hwnd=0x" + RiotWindowInspector.HandleOf(window).ToString("X"));

        // Locate the fields BEFORE taking focus. This wait can run for twenty seconds while the
        // client finishes starting, and holding a focus lock across it is what made the guard abort
        // on a client that had merely swapped its own window.
        context.Report(LoginStage.LocatingLoginForm, "Looking for the sign-in fields…");
        var (formWindow, form) = await inspector.WaitForLoginFormAsync(
            window, TimeSpan.FromSeconds(_profile.AccessibilityWarmupSeconds), cancellationToken);

        window = formWindow ?? window;
        var handle = RiotWindowInspector.HandleOf(window);

        _trace.Write(form is not null
            ? "login fields found via UI Automation on hwnd=0x" + handle.ToString("X")
            : "login fields NOT found; falling back to tab order on hwnd=0x" + handle.ToString("X"));

        // Metadata for every interactive control on the page. This is what lets the selectors above
        // be corrected from what the client actually exposes — it obfuscates its window class and
        // title, so the real control names are otherwise unknowable. Never values, only metadata.
        if (window is not null)
        {
            _trace.Write("page controls:");
            foreach (var line in inspector.DescribePageControls(window)) _trace.Write(line);
            _trace.Write(form?.Submit is not null
                ? "sign-in button identified"
                : "no sign-in button identified — Enter will be used");
        }

        var guard = FocusGuard.ForWindow(handle, context.Settings.AbortTypingOnFocusLoss);

        if (!guard.TryFocus())
        {
            _trace.Write("could not focus the client; foreground is " + guard.DescribeForeground());
            return LoginResult.Failed("Could not bring the Riot Client to the front, so nothing was typed.");
        }

        _trace.Write("focused; foreground is " + guard.DescribeForeground());

        try
        {
            await TypeCredentialsAsync(context, inspector, window, form, guard, cancellationToken);
        }
        catch (FocusLostException ex)
        {
            return LoginResult.Aborted(ex.Message);
        }
        catch (InputBlockedException ex)
        {
            return LoginResult.Failed(ex.Message, ex);
        }

        var result = await WaitForSignInAsync(context, inspector, window, guard, cancellationToken);
        if (!result.IsSuccess) return result;

        await _capture.RunAsync(context, cancellationToken);
        return result;
    }

    /// <summary>
    /// Clears any live session so the client shows its login form, and asks it to remember the next
    /// one.
    ///
    /// These two settings pull in opposite directions — a resumable session means no form to type
    /// into — which is exactly why the app owns the state rather than leaving it to you. The device
    /// cookie is kept so Riot does not treat the sign-in as coming from a new machine and email a
    /// verification code every single time.
    /// </summary>
    private void PrepareSessionState(LoginContext context)
    {
        context.Report(LoginStage.PreparingSession, "Clearing the previous session…");
        _yaml.ClearSessionKeepingDevice();

        if (context.Settings.Strategy != StrategyPreference.AlwaysAutofill)
            _yaml.EnableSessionPersistence();
    }

    private void ApplyRegion(LoginContext context)
    {
        var account = context.Account;
        if (string.IsNullOrWhiteSpace(account.Region)) return;

        context.Report(LoginStage.ApplyingRegion, "Setting region to " + account.Region + "…");
        if (!_yaml.TrySetRegionAndLocale(account.Region, account.Locale, out var error) && error is not null)
            context.Report(LoginStage.ApplyingRegion, error);
    }

    /// <summary>
    /// Types the credentials. The fields have already been located (or not) by the caller, so this
    /// runs immediately after focus is taken and the exposed window is only as long as the typing.
    /// </summary>
    private async Task TypeCredentialsAsync(
        LoginContext context,
        RiotWindowInspector inspector,
        Window? window,
        LoginFormElements? form,
        FocusGuard guard,
        CancellationToken cancellationToken)
    {
        var sender = new InputSender(guard);
        var account = context.Account;

        context.Report(LoginStage.Typing,
            form is null
                ? "Typing your details — please don't touch the mouse or keyboard…"
                : "Filling in your details — please don't touch the mouse or keyboard…");

        if (form is not null)
        {
            // Ticked before anything is typed. Doing it between the password and the submit — where
            // it used to sit — risks disturbing focus at the one moment that matters.
            await SetStaySignedInAsync(context, form.StaySignedIn, cancellationToken);

            TryFocusElement(form.Username);
            await sender.ClearFieldAsync(cancellationToken);
            await sender.TypeAsync(account.LoginUsername, cancellationToken);
            VerifyTypedUsername(form.Username, account.LoginUsername);

            TryFocusElement(form.Password);
            await sender.ClearFieldAsync(cancellationToken);
            using (var password = SecretBuffer.FromString(account.Password!.Value))
            {
                await sender.TypeSecretAsync(password, cancellationToken);
            }
            VerifyTypedPasswordLength(form.Password, account.Password!.Value.Length);

            // Snapshot the page before submitting. Captcha detection compares against this, so the
            // permanently displayed "protected by hCaptcha" footer cannot be mistaken for a live
            // challenge — which is exactly what it was doing on every run.
            _preSubmitText = window is null ? string.Empty : inspector.ReadVisibleText(window);

            await SubmitAsync(inspector, window, form, sender, cancellationToken);
        }
        else
        {
            // Blind mode: the accessibility tree never appeared. The client focuses the username
            // field on a fresh login page, so tab order is enough — this is how every tool in this
            // space works, we simply prefer not to rely on it.
            context.Report(LoginStage.Typing,
                "Could not read the form; falling back to tab order. Please don't touch anything.");

            await sender.ClearFieldAsync(cancellationToken);
            await sender.TypeAsync(account.LoginUsername, cancellationToken);
            await sender.PressTabAsync(cancellationToken);
            using var password = SecretBuffer.FromString(account.Password!.Value);
            await sender.TypeSecretAsync(password, cancellationToken);

            _preSubmitText = window is null ? string.Empty : inspector.ReadVisibleText(window);

            _trace.Write("submitting with Enter (blind mode)");
            await sender.PressEnterAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Submits the form.
    ///
    /// Enter alone does not work on this login page — the credentials simply sit there and the
    /// sign-in times out — so the submit button is clicked when one can be identified safely.
    /// <see cref="SubmitButtonChooser"/> deliberately returns nothing when the choice is ambiguous,
    /// and Enter is the fallback: a login that fails to submit is a far better outcome than one that
    /// clicks an unidentified button on a page full of identity-provider logins.
    /// </summary>
    private async Task SubmitAsync(
        RiotWindowInspector inspector,
        Window? window,
        LoginFormElements form,
        InputSender sender,
        CancellationToken cancellationToken)
    {
        // Re-read the page now that both fields hold text. The client keeps its submit button
        // disabled until then, so the scan taken before typing never saw it — and the chooser, which
        // skips disabled buttons, fell through to the version label in the footer and clicked that.
        var submit = form.Submit;

        if (window is not null)
        {
            await Task.Delay(250, cancellationToken);   // let the form re-evaluate its own state
            var refreshed = inspector.TryReadLoginForm(window);

            if (refreshed?.Submit is not null)
            {
                submit = refreshed.Submit;
                _trace.Write("sign-in button located after typing");
            }
            else if (submit is null)
            {
                _trace.Write("still no sign-in button after typing");
            }
        }

        if (submit is not null && TryInvoke(submit))
        {
            _trace.Write("submitted by clicking the sign-in button");
            return;
        }

        // Re-focus the password field so Enter lands somewhere that can act on it.
        TryFocusElement(form.Password);
        _trace.Write(form.Submit is null
            ? "no sign-in button identified; submitting with Enter"
            : "sign-in button could not be invoked; submitting with Enter");

        await sender.PressEnterAsync(cancellationToken);
    }

    private bool TryInvoke(AutomationElement element)
    {
        try
        {
            if (element.Patterns.Invoke.IsSupported)
            {
                element.Patterns.Invoke.Pattern.Invoke();
                return true;
            }

            // Electron controls often expose no Invoke pattern; a real click still works.
            element.Click();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _trace.Write("could not activate the sign-in button: " + ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Confirms the username field holds what we sent. Cheap, and it catches the failure mode where
    /// characters are dropped or appended to a value the client had pre-filled.
    /// </summary>
    private void VerifyTypedUsername(AutomationElement field, string expected)
    {
        var actual = ReadValue(field);
        if (actual is null)
        {
            _trace.Write("username field could not be read back");
            return;
        }

        _trace.Write(string.Equals(actual, expected, StringComparison.Ordinal)
            ? "username verified"
            : "USERNAME MISMATCH: field holds " + actual.Length + " chars, expected " + expected.Length);
    }

    /// <summary>
    /// Compares only the length of the password field's mask. The value itself is a row of bullets,
    /// and even that is not written anywhere — a wrong length is the signal worth having.
    /// </summary>
    private void VerifyTypedPasswordLength(AutomationElement field, int expectedLength)
    {
        var actual = ReadValue(field);
        if (string.IsNullOrEmpty(actual))
        {
            _trace.Write("password field does not report a value (normal for a masked field)");
            return;
        }

        _trace.Write(actual.Length == expectedLength
            ? "password length verified"
            : "PASSWORD LENGTH MISMATCH: field holds " + actual.Length + " chars, expected " + expectedLength);
    }

    /// <summary>
    /// The parts of <paramref name="current"/> that were not in <paramref name="baseline"/>.
    ///
    /// The page text is read as fragments joined by a separator, so this compares fragment by
    /// fragment rather than doing a character diff — a fragment either was on screen before
    /// submitting or it was not.
    /// </summary>
    internal static string NewSince(string baseline, string current)
    {
        if (string.IsNullOrEmpty(current)) return string.Empty;
        if (string.IsNullOrEmpty(baseline)) return current;

        var before = baseline
            .Split('│', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = current
            .Split('│', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0 && !before.Contains(part));

        return string.Join(" │ ", added);
    }

    private static string? ReadValue(AutomationElement element)
    {
        try
        {
            return element.Patterns.Value.IsSupported
                ? element.Patterns.Value.Pattern.Value.ValueOrDefault
                : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Ticks "Stay signed in" so a session exists to capture.
    ///
    /// Without it the client discards the session on exit and every future login types again, which
    /// would quietly reduce the app to the same experience as the tools that warn you not to move
    /// your mouse.
    /// </summary>
    /// <summary>
    /// Ticks "Stay signed in" so a session exists to capture.
    ///
    /// Without it the client discards the session on exit, nothing is captured, and the whole
    /// "type once then never again" design never engages — every future sign-in would type. The
    /// toggle pattern silently did nothing the first time this ran, so the state is now read back
    /// and a real click is used as the fallback.
    /// </summary>
    private async Task SetStaySignedInAsync(
        LoginContext context, CheckBox? checkBox, CancellationToken cancellationToken)
    {
        var wanted = context.Settings.Strategy != StrategyPreference.AlwaysAutofill;

        if (checkBox is null)
        {
            _trace.Write("no \"stay signed in\" checkbox found" + (wanted ? " — sessions cannot be captured" : ""));
            return;
        }

        if (ReadChecked(checkBox) == wanted)
        {
            _trace.Write("\"stay signed in\" already " + (wanted ? "ticked" : "unticked"));
            return;
        }

        TrySetChecked(checkBox, wanted);
        await Task.Delay(200, cancellationToken);

        if (ReadChecked(checkBox) == wanted)
        {
            _trace.Write("\"stay signed in\" set to " + wanted);
            return;
        }

        // The toggle pattern reported success but nothing changed, or is not supported at all —
        // Electron controls frequently only respond to a real click. Retried because the very same
        // click has been observed failing once and then working on the next attempt.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            TryClick(checkBox);
            await Task.Delay(250, cancellationToken);

            if (ReadChecked(checkBox) == wanted)
            {
                _trace.Write("\"stay signed in\" set to " + wanted + " by clicking (attempt " + attempt + ")");
                return;
            }
        }

        var final = ReadChecked(checkBox);
        _trace.Write("COULD NOT SET \"stay signed in\" (wanted " + wanted + ", is " + (final?.ToString() ?? "unknown") + ")"
                     + " — no session will be captured, so the next sign-in will type again");
    }

    private static bool? ReadChecked(CheckBox checkBox)
    {
        try { return checkBox.IsChecked; }
        catch (Exception) { return null; }
    }

    private static void TrySetChecked(CheckBox checkBox, bool value)
    {
        try { checkBox.IsChecked = value; }
        catch (Exception) { /* fall through to a click */ }
    }

    private void TryClick(AutomationElement element)
    {
        try { element.Click(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _trace.Write("could not click element: " + ex.GetType().Name);
        }
    }

    private static void TryFocusElement(AutomationElement element)
    {
        try { element.Focus(); }
        catch (Exception) { /* fall back to whatever already has focus */ }
    }

    /// <summary>
    /// Waits for the client to report an authorised session, watching the page for the things that
    /// mean waiting longer is pointless: a rejected password, a rate limit, a request for a
    /// verification code, or a bot check that only the user can clear.
    /// </summary>
    private async Task<LoginResult> WaitForSignInAsync(
        LoginContext context,
        RiotWindowInspector inspector,
        Window? window,
        FocusGuard guard,
        CancellationToken cancellationToken)
    {
        context.Report(LoginStage.WaitingForSignIn, "Signing in…");

        var lockfile = await Lockfile.WaitForAsync(
            _paths.RiotClientLockfile, TimeSpan.FromSeconds(30), cancellationToken);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(context.Settings.LoginWindowTimeoutSeconds);
        var codeAttempted = false;
        var captchaAnnounced = false;
        var submittedAt = DateTimeOffset.UtcNow;

        RiotLocalApiClient? client = lockfile is null ? null : new RiotLocalApiClient(lockfile);

        try
        {
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Re-read the lockfile as we go. The client rewrites it with a fresh port when it
            // restarts after a successful login, and holding the original meant polling a dead port
            // for the full timeout and concluding the sign-in had failed when it had worked.
            var current = Lockfile.Read(_paths.RiotClientLockfile);
            if (current is not null && current.Port != lockfile?.Port)
            {
                _trace.Write("client restarted on port " + current.Port + "; following it");
                client?.Dispose();
                client = new RiotLocalApiClient(current);
                lockfile = current;
            }

            if (client is not null && await client.IsAuthorizedAsync(cancellationToken))
            {
                _trace.Write("client reports an authorised session");
                return LoginResult.Success("Signed in.");
            }

            var text = window is null ? string.Empty : inspector.ReadVisibleText(window);

            if (_profile.MatchesAny(text, _profile.WrongCredentialPhrases))
            {
                _trace.Write("client rejected the credentials");
                return LoginResult.Failed("Riot rejected the username or password for this account.");
            }

            if (_profile.MatchesAny(text, _profile.RateLimitPhrases))
            {
                _trace.Write("client reports rate limiting");
                return LoginResult.Failed("Riot is rate limiting sign-ins. Wait a few minutes and try again.");
            }

            // A bot check is the user's to complete. The app must not attempt to work around it, so
            // it says what is happening, stops counting down, and keeps watching.
            //
            // Only text that appeared *after* submitting counts. The login page always footers
            // "THIS APP IS PROTECTED BY HCAPTCHA", and matching that reported a challenge on every
            // single run; diffing makes static boilerplate structurally unable to trigger this.
            if (_profile.MatchesAny(NewSince(_preSubmitText, text), _profile.CaptchaPhrases))
            {
                if (!captchaAnnounced)
                {
                    captchaAnnounced = true;
                    _trace.Write("a bot check appeared; handing control to the user");
                    context.Report(LoginStage.WaitingForSignIn,
                        "Riot is showing a security check — please complete it in the client. Waiting…");
                    deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3);
                }
            }

            if (!codeAttempted && _profile.MatchesAny(text, _profile.VerificationPhrases))
            {
                codeAttempted = true;
                var handled = await SupplyVerificationCodeAsync(context, inspector, window, guard, cancellationToken);
                if (handled is not null) return handled;
                deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
            }

            await Task.Delay(1000, cancellationToken);
        }

        // Distinguish "the form is still sitting there untouched" from "it went somewhere and stalled".
        var stillOnForm = window is not null && inspector.TryReadLoginForm(window) is not null;
        var waited = (int)(DateTimeOffset.UtcNow - submittedAt).TotalSeconds;

        if (stillOnForm)
        {
            _trace.Write("timed out after " + waited + "s with the login form still displayed");
            return LoginResult.Failed(
                "Your details were entered but the client never accepted them — the sign-in form is " +
                "still showing. If a security check appeared, complete it and try again; otherwise the " +
                "sign-in button may not have been found. The log at " +
                (_trace.Path_ ?? "the vault folder") + " lists what was on the page.");
        }

        _trace.Write("timed out after " + waited + "s; the form is gone but no session was reported");
        return LoginResult.Failed(
            "The sign-in was submitted but the client never reported a session. It may still be " +
            "loading — check the Riot Client window.");
        }
        finally
        {
            client?.Dispose();
        }
    }

    /// <summary>
    /// Asks for the emailed or authenticator code and types it.
    ///
    /// An account with two-factor enabled can never be fully hands-free, so this stops and asks
    /// rather than failing with something vague.
    /// </summary>
    private async Task<LoginResult?> SupplyVerificationCodeAsync(
        LoginContext context,
        RiotWindowInspector inspector,
        Window? window,
        FocusGuard guard,
        CancellationToken cancellationToken)
    {
        if (context.RequestVerificationCode is null)
            return LoginResult.Failed("This account needs a verification code, and there is no way to ask for one here.");

        context.Report(LoginStage.WaitingForVerificationCode, "Waiting for your verification code…");

        var code = await context.RequestVerificationCode(
            new VerificationPrompt(
                "Verification code needed",
                "Riot asked for a verification code for " + context.Account.DisplayRiotId +
                ". Check your email or authenticator app and enter it below."),
            cancellationToken);

        if (string.IsNullOrWhiteSpace(code))
            return LoginResult.Aborted("Sign-in cancelled at the verification step.");

        var field = window is null ? null : inspector.TryFindVerificationField(window);
        if (field is not null) TryFocusElement(field);

        if (!guard.TryFocus())
            return LoginResult.Failed("Lost the Riot Client window while entering the code.");

        try
        {
            var sender = new InputSender(guard);
            await sender.TypeAsync(code.Trim(), cancellationToken);
            await sender.PressEnterAsync(cancellationToken);
        }
        catch (FocusLostException ex)
        {
            return LoginResult.Aborted(ex.Message);
        }

        return null;   // keep waiting; the outer loop decides the outcome
    }
}
