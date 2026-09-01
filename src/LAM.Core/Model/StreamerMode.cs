using System.Diagnostics;

namespace LAM.Core.Model;

/// <summary>
/// Hides account names while capture software is running.
///
/// A Riot ID on screen is enough for someone to find the account, and a smurf's whole point is that
/// it is not obviously yours. The detection is deliberately crude — the process names of the common
/// capture tools — because the alternative is asking the user to remember a toggle before every
/// stream, which is exactly the thing they will forget once.
/// </summary>
public static class StreamerMode
{
    /// <summary>
    /// Capture and streaming software worth hiding from.
    ///
    /// Names only, matched case-insensitively without extensions, as <see cref="Process"/> reports them.
    /// </summary>
    private static readonly string[] CaptureProcesses =
    [
        "obs64", "obs32", "obs",
        "Streamlabs OBS", "Streamlabs Desktop",
        "XSplit.Core", "XSplitBroadcaster",
        "Twitch Studio",
        "nvcontainer",        // ShadowPlay's host; broad, hence the opt-in below
    ];

    /// <summary>
    /// Whether anything that captures the screen appears to be running.
    ///
    /// <paramref name="includeBroadOnes"/> covers processes that are often running for reasons
    /// unrelated to recording — NVIDIA's container is present on most machines with a GeForce card —
    /// so it is off unless asked for, or the app would redact itself permanently for many users.
    /// </summary>
    public static bool CaptureSoftwareRunning(bool includeBroadOnes = false)
    {
        var names = includeBroadOnes
            ? CaptureProcesses
            : CaptureProcesses.Where(n => n != "nvcontainer").ToArray();

        foreach (var name in names)
        {
            try
            {
                if (Process.GetProcessesByName(name).Length > 0) return true;
            }
            catch (InvalidOperationException)
            {
                // A process list that cannot be read is not evidence of anything.
            }
        }

        return false;
    }

    /// <summary>
    /// Redacts a Riot ID, keeping just enough to tell accounts apart.
    ///
    /// The first character and the length are kept because the point is to stay usable — you still
    /// need to know which card is which — while not being searchable.
    /// </summary>
    public static string Redact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "•••";

        var hash = value.Split('#');
        var name = hash[0];

        var masked = name.Length <= 1
            ? "•"
            : name[0] + new string('•', Math.Min(name.Length - 1, 7));

        return hash.Length > 1 ? masked + "#•••" : masked;
    }
}
