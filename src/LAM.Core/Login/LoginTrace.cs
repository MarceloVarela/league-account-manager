namespace LAM.Core.Login;

/// <summary>
/// An append-only record of what a sign-in actually did.
///
/// Exists because the sign-in drives another application's UI, which means when it misbehaves there
/// is nothing to look at afterwards — the last two faults were diagnosed from screenshots. This
/// records the stage sequence, which window was used and how the login fields were found, so the
/// next one can be read rather than guessed at.
///
/// It records **no secrets**: no password, no username, no session YAML, no cookie. Stage names,
/// window handles, process ids and outcomes only. Anything that would be worth stealing stays in the
/// encrypted vault, and a log file is not encrypted.
/// </summary>
public sealed class LoginTrace
{
    private const long MaxBytes = 512 * 1024;

    private readonly string? _path;
    private readonly Lock _gate = new();

    /// <summary>A trace that writes nowhere. Used by tests and when no log directory is configured.</summary>
    public static LoginTrace Null { get; } = new(null);

    private LoginTrace(string? path) => _path = path;

    /// <summary>Creates a trace writing to <c>login.log</c> inside <paramref name="logDirectory"/>.</summary>
    public static LoginTrace InDirectory(string logDirectory)
    {
        try
        {
            Directory.CreateDirectory(logDirectory);
            return new LoginTrace(Path.Combine(logDirectory, "login.log"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Null;
        }
    }

    public string? Path_ => _path;

    /// <summary>Marks the start of one sign-in attempt, so runs are easy to tell apart.</summary>
    public void BeginAttempt(string accountLabel)
    {
        Write(string.Empty);
        Write("=== sign-in: " + Sanitise(accountLabel) + " ===");
    }

    public void Write(string message)
    {
        if (_path is null) return;

        var line = message.Length == 0
            ? string.Empty
            : DateTimeOffset.Now.ToString("HH:mm:ss.fff") + "  " + message;

        lock (_gate)
        {
            try
            {
                RollIfLarge();
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Diagnostics must never break the thing they are diagnosing.
            }
        }
    }

    /// <summary>
    /// A label is user-supplied free text, so it is trimmed and stripped of newlines before being
    /// written — a log entry should never be able to forge another log entry.
    /// </summary>
    private static string Sanitise(string value)
    {
        var cleaned = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return cleaned.Length <= 60 ? cleaned : cleaned[..60];
    }

    /// <summary>
    /// Keeps one previous file and starts fresh past the cap, so the log cannot grow without bound
    /// on a machine that signs in several times a day for a year.
    /// </summary>
    private void RollIfLarge()
    {
        if (_path is null) return;

        var file = new FileInfo(_path);
        if (!file.Exists || file.Length < MaxBytes) return;

        var previous = _path + ".1";
        try
        {
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(_path, previous);
        }
        catch (IOException)
        {
            // Keep appending rather than losing the entry.
        }
    }
}
