namespace LAM.Core.Riot;

/// <summary>
/// The credentials the Riot Client and the League client write for their own local HTTP APIs.
///
/// Format is <c>name:pid:port:password:protocol</c>. The file exists only while the process is
/// running and is rewritten with a fresh port and password on every launch, so it must be re-read
/// after each restart rather than cached.
/// </summary>
public sealed record Lockfile(string Name, int ProcessId, int Port, string Password, string Protocol)
{
    public Uri BaseAddress => new($"{Protocol}://127.0.0.1:{Port}");

    /// <summary>The Basic auth value: the user is always literally "riot".</summary>
    public string BasicAuthParameter =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"riot:{Password}"));

    public static bool TryParse(string? contents, out Lockfile? lockfile)
    {
        lockfile = null;
        if (string.IsNullOrWhiteSpace(contents)) return false;

        var parts = contents.Trim().Split(':');
        if (parts.Length != 5) return false;
        if (!int.TryParse(parts[1], out var pid)) return false;
        if (!int.TryParse(parts[2], out var port)) return false;
        if (port is <= 0 or > 65535) return false;
        if (string.IsNullOrEmpty(parts[3])) return false;

        var protocol = parts[4].ToLowerInvariant();
        if (protocol is not ("https" or "http")) return false;

        lockfile = new Lockfile(parts[0], pid, port, parts[3], protocol);
        return true;
    }

    /// <summary>
    /// Reads a lockfile from disk. Opened with the widest possible sharing because the owning
    /// process keeps its own handle open — a plain File.ReadAllText intermittently throws here.
    /// </summary>
    public static Lockfile? Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return TryParse(reader.ReadToEnd(), out var lockfile) ? lockfile : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Polls until the lockfile appears and its process is alive, or the token trips.</summary>
    public static async Task<Lockfile?> WaitForAsync(
        string? path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lockfile = Read(path);
            if (lockfile is not null && ProcessIsAlive(lockfile.ProcessId))
                return lockfile;

            await Task.Delay(250, cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// A lockfile left behind by a crashed client would otherwise send us at a dead port, so the
    /// pid is checked rather than trusted.
    /// </summary>
    private static bool ProcessIsAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
