using System.Diagnostics;

namespace LAM.Core.Riot;

/// <summary>
/// Shuts the Riot and League clients down so a different account can be launched.
///
/// Two rules matter here. Vanguard (<c>vgc</c>, <c>vgk</c>, <c>vgtray</c>) is never touched — it is
/// a kernel anti-cheat, killing it achieves nothing useful and interfering with it is exactly the
/// behaviour that gets tools flagged. And a live game is never interrupted: closing the client
/// mid-match earns a leaver penalty, so <see cref="IsGameInProgress"/> blocks the switch instead.
/// </summary>
public class RiotProcessManager
{
    /// <summary>
    /// Client processes we terminate. "Riot Client" is the Electron shell and normally has several
    /// PIDs (a main process plus renderers); all of them are matched by name.
    /// </summary>
    private static readonly string[] ClientProcessNames =
    [
        "RiotClientServices",
        "Riot Client",
        "RiotClientCrashHandler",
        "RiotClientUx",
        "RiotClientUxRender",
        "LeagueClient",
        "LeagueClientUx",
        "LeagueClientUxRender",
    ];

    /// <summary>
    /// The game executable. Deliberately absent from <see cref="ClientProcessNames"/>: its presence
    /// means a match is running and the whole switch must be refused.
    /// </summary>
    private const string GameProcessName = "League of Legends";

    /// <summary>Never terminated. Listed so the intent survives future edits to this file.</summary>
    private static readonly string[] ProtectedProcessNames = ["vgc", "vgk", "vgtray"];

    /// <summary>True when a match is in progress, in champion select, or otherwise in-game.</summary>
    /// <remarks>Virtual so tests can drive the refusal path without a real game running.</remarks>
    public virtual bool IsGameInProgress() => FindByName(GameProcessName).Count > 0;

    public bool IsClientRunning() => ClientProcessNames.Any(name => FindByName(name).Count > 0);

    /// <summary>
    /// Closes every Riot client process and waits for them to exit.
    ///
    /// Asks politely first — a clean exit lets the client flush its own settings, which matters
    /// because we are about to read the session file it writes on shutdown — then kills whatever
    /// is still standing.
    /// </summary>
    public async Task<int> CloseClientsAsync(CancellationToken cancellationToken, TimeSpan? graceTimeout = null)
    {
        var grace = graceTimeout ?? TimeSpan.FromSeconds(5);
        var processes = ClientProcessNames.SelectMany(FindByName).ToList();
        if (processes.Count == 0) return 0;

        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited) process.CloseMainWindow();
            }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
            {
                // No main window (renderer processes have none) — the kill below handles it.
            }
        }

        var deadline = DateTimeOffset.UtcNow + grace;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (processes.All(HasExited)) break;
            await Task.Delay(200, cancellationToken);
        }

        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone, or owned by another user. Nothing useful to do either way.
            }
        }

        // The Riot Client releases its lockfile slightly after the process object reports exit;
        // launching into that window makes the new client fight the old one for the port.
        await WaitForAllExitedAsync(processes, TimeSpan.FromSeconds(10), cancellationToken);

        foreach (var process in processes) process.Dispose();
        return processes.Count;
    }

    private static async Task WaitForAllExitedAsync(
        IReadOnlyCollection<Process> processes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (processes.All(HasExited)) return;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(200, cancellationToken);
        }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static List<Process> FindByName(string name)
    {
        try
        {
            return Process.GetProcessesByName(name).ToList();
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Exposed so a diagnostics screen can show what the app would close.</summary>
    public IReadOnlyList<string> DescribeRunningClients()
        => ClientProcessNames
            .SelectMany(name => FindByName(name).Select(p => new { name, p }))
            .Select(x =>
            {
                var id = x.p.Id;
                x.p.Dispose();
                return $"{x.name} (pid {id})";
            })
            .ToList();

    public static IReadOnlyList<string> ProtectedProcesses => ProtectedProcessNames;

    /// <summary>
    /// The client processes this app owns. Public so the focus guard can decide whether the window
    /// receiving keystrokes belongs to the client we launched, without keeping a second copy of the
    /// list that could drift out of step with this one.
    /// </summary>
    public static IReadOnlyList<string> ClientProcesses => ClientProcessNames;
}
