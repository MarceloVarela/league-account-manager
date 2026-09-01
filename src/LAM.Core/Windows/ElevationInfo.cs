using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

namespace LAM.Core.Windows;

/// <summary>
/// Answers "are we allowed to drive the Riot Client?", which on Windows comes down to integrity
/// levels.
///
/// This exists because the failure it describes is otherwise invisible. If the Riot Client runs
/// elevated and this app does not, UIPI silently refuses our keystrokes, hides the client's UI
/// Automation tree, and denies us the right to close it — each of which surfaces as its own vague,
/// unrelated-looking error. Reporting the mismatch as a fact is far more use than three mysteries.
///
/// Everything here is read-only. The app never writes compatibility flags: those belong to the user.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ElevationInfo
{
    private const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    /// <summary>Whether this process is running with an elevated administrator token.</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>A compatibility layer Windows applies to one executable.</summary>
    /// <param name="Hive">"HKCU" or "HKLM" — per-user flags are the ones people actually set.</param>
    /// <param name="ExecutablePath">The executable the layer applies to.</param>
    /// <param name="Layers">The raw layer string, e.g. <c>~ RUNASADMIN</c>.</param>
    public sealed record CompatibilityLayer(string Hive, string ExecutablePath, string Layers)
    {
        public bool ForcesElevation =>
            Layers.Contains("RUNASADMIN", StringComparison.OrdinalIgnoreCase);

        public string FileName => Path.GetFileName(ExecutablePath);

        public override string ToString() => FileName + "  =>  " + Layers + "  [" + Hive + "]";
    }

    /// <summary>
    /// Every compatibility layer set on a Riot executable, from both hives.
    ///
    /// Matched on the path containing "Riot Games" rather than on an exact filename, so the League
    /// client and the game itself are picked up too — both are processes this app tries to close.
    /// </summary>
    public static IReadOnlyList<CompatibilityLayer> RiotCompatibilityLayers()
    {
        var results = new List<CompatibilityLayer>();
        Collect(Registry.CurrentUser, "HKCU", results);
        Collect(Registry.LocalMachine, "HKLM", results);
        return results;
    }

    private static void Collect(RegistryKey hive, string hiveName, List<CompatibilityLayer> into)
    {
        try
        {
            using var key = hive.OpenSubKey(LayersKey, writable: false);
            if (key is null) return;

            foreach (var name in key.GetValueNames())
            {
                if (!name.Contains("Riot Games", StringComparison.OrdinalIgnoreCase)) continue;
                if (key.GetValue(name) is not string layers) continue;

                into.Add(new CompatibilityLayer(hiveName, name, layers));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Diagnostics only — a hive we cannot read must never break anything.
        }
    }

    /// <summary>
    /// The one line worth showing a user: whether this app can actually drive the client it is about
    /// to launch, and if not, what to do about it.
    /// </summary>
    public static string DescribeCompatibility()
    {
        var elevated = IsElevated;
        var forced = RiotCompatibilityLayers().Where(l => l.ForcesElevation).ToList();

        if (forced.Count == 0)
        {
            return elevated
                ? "Running elevated. The Riot Client is not forced to run as administrator, so this is more privilege than needed."
                : "Running normally, and the Riot Client is not forced to run as administrator. Nothing needs elevation.";
        }

        var distinct = forced.Select(l => l.FileName).Distinct().ToList();
        var names = string.Join(", ", distinct);
        var verb = distinct.Count == 1 ? " is" : " are";

        // Plain ASCII: this string is printed to a console as well as shown in the UI, and the
        // console mangles the arrow character.
        return elevated
            ? "Running elevated, matching the Riot Client (" + names + verb + " set to run as administrator). Sign-in can drive the client."
            : "MISMATCH: " + names + verb + " set to run as administrator but this app is not. Windows will block " +
              "typing into the client, reading its window, and closing it. Restart the account manager as " +
              "administrator, or clear that tick box in the executable's Properties > Compatibility tab.";
    }
}
