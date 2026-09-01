using System.Diagnostics;

namespace LAM.Core.Riot;

public sealed record RepairResult(string Action, bool Succeeded, string Detail);

/// <summary>
/// The handful of things that fix a Riot Client which has got itself stuck.
///
/// All of it is the sort of thing you would otherwise do by hand: end the processes, delete a
/// lockfile that outlived the process that wrote it, clear the embedded browser's cache. Worth having
/// in one place because a stale lockfile in particular makes this app look broken — every probe reads
/// that file to find the client's port, so a leftover one sends every request to a port nothing is
/// listening on.
///
/// Vanguard is never touched. It is a kernel anti-cheat, and interfering with it is exactly the
/// behaviour that gets tools flagged — see <see cref="RiotProcessManager.ProtectedProcesses"/>.
/// </summary>
public sealed class ClientRepair
{
    private readonly RiotPaths _paths;
    private readonly RiotProcessManager _processes;

    public ClientRepair(RiotPaths paths, RiotProcessManager processes)
    {
        _paths = paths;
        _processes = processes;
    }

    /// <summary>
    /// Whether a lockfile exists whose process is gone.
    ///
    /// This is the interesting failure: the file says "the client is listening on port N" and nothing
    /// is. Every read then fails in a way that looks like the client refusing to answer.
    /// </summary>
    public IReadOnlyList<string> StaleLockfiles()
    {
        var stale = new List<string>();

        foreach (var path in new[] { _paths.RiotClientLockfile, _paths.LeagueLockfile })
            if (path is not null && IsLockfileStale(path)) stale.Add(path);

        return stale;
    }

    /// <summary>
    /// Whether one lockfile has outlived the process that wrote it.
    ///
    /// Pulled out as a pure function of the path so it can be tested against a temporary file: the
    /// real lockfile locations are derived from machine paths that cannot be redirected, so testing
    /// through <see cref="StaleLockfiles"/> would mean reading whatever this machine happens to have.
    ///
    /// A file that will not parse counts as stale. It is either truncated or from a client version
    /// that writes a different shape, and in both cases it is useless for finding a port.
    /// </summary>
    internal static bool IsLockfileStale(string path)
    {
        if (!File.Exists(path)) return false;

        var lockfile = Lockfile.Read(path);
        return lockfile is null || !ProcessExists(lockfile.ProcessId);
    }

    private static bool ProcessExists(int pid)
    {
        if (pid <= 0) return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes lockfiles whose process no longer exists.
    ///
    /// Only stale ones: deleting a live lockfile would break the running client's own API and take
    /// this app's access with it.
    /// </summary>
    public RepairResult ClearStaleLockfiles()
    {
        var stale = StaleLockfiles();
        if (stale.Count == 0)
            return new RepairResult("Clear stale lockfiles", true, "None found — nothing to clear.");

        var removed = 0;
        var failed = new List<string>();

        foreach (var path in stale)
        {
            try
            {
                File.Delete(path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(Path.GetFileName(Path.GetDirectoryName(path)) ?? path);
            }
        }

        return new RepairResult(
            "Clear stale lockfiles",
            failed.Count == 0,
            failed.Count == 0
                ? "Removed " + removed + "."
                : "Removed " + removed + "; could not remove " + string.Join(", ", failed) + ".");
    }

    /// <summary>
    /// Ends the Riot and League clients, leaving Vanguard alone.
    ///
    /// Asks first and only kills what will not go quietly, which is what
    /// <see cref="RiotProcessManager.CloseClientsAsync"/> already does for the sign-in path.
    /// </summary>
    public async Task<RepairResult> StopClientsAsync(CancellationToken cancellationToken)
    {
        if (_processes.IsGameInProgress())
        {
            return new RepairResult(
                "Close the Riot and League clients", false,
                "A game is in progress - refusing, because ending it would count as a leave.");
        }

        try
        {
            var stopped = await _processes.CloseClientsAsync(cancellationToken);
            return new RepairResult(
                "Close the Riot and League clients", true,
                stopped == 0 ? "Nothing was running." : "Closed " + stopped + " processes.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new RepairResult("Close the Riot and League clients", false, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Clears the League client's embedded-browser cache.
    ///
    /// The client's UI is a web view, and a corrupt cache shows up as a blank or half-drawn client
    /// that reinstalling appears to fix. It is safe to delete — it is rebuilt on next launch — but
    /// only while the client is closed, or it is locked and half-deleted.
    /// </summary>
    public RepairResult ClearBrowserCache()
    {
        if (_processes.DescribeRunningClients().Count > 0)
        {
            return new RepairResult(
                "Clear the client's browser cache", false,
                "Close the clients first — the cache is locked while they run, and a half-deleted "
                + "cache is worse than a stale one.");
        }

        var install = _paths.LeagueInstallDirectory;
        if (install is null)
            return new RepairResult("Clear the client's browser cache", false, "League install not found.");

        var candidates = new[]
        {
            Path.Combine(install, "Cache"),
            Path.Combine(install, "LeagueClient", "Cache"),
            Path.Combine(install, "Config", "Cache"),
        };

        var cleared = 0;

        foreach (var directory in candidates.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                cleared++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A locked subfolder is not worth failing the whole repair over.
            }
        }

        return new RepairResult(
            "Clear the client's browser cache",
            true,
            cleared == 0 ? "No cache folders found." : "Cleared " + cleared + ".");
    }

    /// <summary>Folders worth opening by hand when something needs looking at.</summary>
    public IReadOnlyList<(string Name, string Path)> UsefulFolders()
    {
        var folders = new List<(string, string)>
        {
            ("Riot Client config", Path.GetDirectoryName(_paths.PrivateSettingsFile) ?? string.Empty),
        };

        if (_paths.LeagueInstallDirectory is { } install)
        {
            folders.Add(("League install", install));
            folders.Add(("League logs", Path.Combine(install, "Logs")));
        }

        return [.. folders.Where(f => !string.IsNullOrWhiteSpace(f.Item2))];
    }
}
