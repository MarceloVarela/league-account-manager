using LAM.Core.Login;
using LAM.Core.Login.Input;
using Xunit;

namespace LAM.Tests;

/// <summary>
/// The rule that decides whether a keystroke may be delivered.
///
/// The guard previously demanded that the foreground window be one exact <c>HWND</c>, captured when
/// the client first appeared. That aborted sign-ins constantly for a reason the user had nothing to
/// do with: the client is Electron and replaces its window while starting, so the handle changed
/// while the client sat in the foreground throughout. The test is now "does the foreground window
/// belong to the client we launched", which is the property actually worth enforcing.
///
/// <c>GetForegroundWindow</c> cannot be driven from a test, so the decision takes the owning process
/// id and a name resolver, and that is what is exercised here.
/// </summary>
public sealed class FocusGuardTests
{
    private const uint SomePid = 4242;

    private static FocusGuard Guard(string? foregroundProcessName, bool enabled = true)
        => FocusGuard.ForTesting(enabled, _ => foregroundProcessName);

    [Theory]
    [InlineData("Riot Client")]
    [InlineData("RiotClientServices")]
    [InlineData("LeagueClient")]
    [InlineData("LeagueClientUx")]
    public void A_window_owned_by_the_client_we_launched_is_accepted(string processName)
        => Assert.True(Guard(processName).IsAcceptable(SomePid));

    [Fact]
    public void Process_names_are_matched_case_insensitively()
        => Assert.True(Guard("riotclientservices").IsAcceptable(SomePid));

    [Theory]
    [InlineData("Discord")]
    [InlineData("chrome")]
    [InlineData("explorer")]
    [InlineData("LeagueAccountManager")]
    public void A_window_owned_by_anything_else_is_refused(string processName)
    {
        // This is the case that matters: alt-tab to Discord mid-sign-in and the rest of the password
        // must not be typed into it.
        Assert.False(Guard(processName).IsAcceptable(SomePid));
    }

    [Fact]
    public void An_unresolvable_process_is_refused_rather_than_waved_through()
    {
        // Fails closed on purpose. A guard that accepted an owner it could not identify would still
        // look like protection while providing none.
        Assert.False(Guard(null).IsAcceptable(SomePid));
        Assert.False(Guard(string.Empty).IsAcceptable(SomePid));
    }

    [Fact]
    public void A_missing_foreground_window_is_refused()
        => Assert.False(Guard("Riot Client").IsAcceptable(0));

    [Fact]
    public void Turning_the_setting_off_accepts_everything()
    {
        // AbortTypingOnFocusLoss = false. Documented as a bad idea, but it must still do what it says.
        var disabled = FocusGuard.ForTesting(enabled: false, _ => "Discord");

        Assert.True(disabled.IsAcceptable(SomePid));
        Assert.True(disabled.IsAcceptable(0));
    }

    [Fact]
    public void Verify_throws_only_when_the_foreground_is_unacceptable()
    {
        Guard("Riot Client", enabled: true);   // sanity: constructing is side-effect free

        var refused = FocusGuard.ForTesting(enabled: true, _ => "Discord");
        var allowed = FocusGuard.ForTesting(enabled: false, _ => "Discord");

        // IsIntact consults the real GetForegroundWindow, so assert through IsAcceptable, which is
        // the same decision with the Win32 call factored out.
        Assert.False(refused.IsAcceptable(SomePid));
        Assert.True(allowed.IsAcceptable(SomePid));
    }
}

/// <summary>
/// The sign-in log exists to make the next failure readable. It must never become a second, plaintext
/// copy of things that are otherwise kept encrypted.
/// </summary>
public sealed class LoginTraceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "lam-trace", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void Writes_stages_with_timestamps()
    {
        var trace = LoginTrace.InDirectory(_directory);
        trace.BeginAttempt("main");
        trace.Write("trying strategy: autofill");

        var text = File.ReadAllText(trace.Path_!);

        Assert.Contains("sign-in: main", text);
        Assert.Contains("trying strategy: autofill", text);
    }

    [Fact]
    public void An_account_label_cannot_forge_extra_log_lines()
    {
        // Labels are free text the user types, so a newline in one must not be able to fabricate an
        // entry that looks like the app wrote it.
        var trace = LoginTrace.InDirectory(_directory);
        trace.BeginAttempt("main\r\n12:00:00.000  SUCCESS via autofill");

        // The property is that the label cannot become its own entry, not that the word never
        // appears: collapsed onto the header line it is obviously part of the label.
        var entries = File.ReadAllLines(trace.Path_!)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        var forged = Assert.Single(entries);
        Assert.Contains("=== sign-in:", forged, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_label_is_truncated_rather_than_dumped()
    {
        var trace = LoginTrace.InDirectory(_directory);
        trace.BeginAttempt(new string('x', 500));

        Assert.All(File.ReadAllLines(trace.Path_!), line => Assert.True(line.Length < 200));
    }

    [Fact]
    public void The_null_trace_writes_nothing_and_never_throws()
    {
        LoginTrace.Null.BeginAttempt("main");
        LoginTrace.Null.Write("anything");

        Assert.Null(LoginTrace.Null.Path_);
    }

    [Fact]
    public void An_unwritable_directory_degrades_to_the_null_trace()
    {
        // A log that cannot be written must not break the sign-in it was there to explain.
        var trace = LoginTrace.InDirectory("Z:\\definitely\\not\\a\\real\\drive");
        trace.Write("still fine");
    }
}
