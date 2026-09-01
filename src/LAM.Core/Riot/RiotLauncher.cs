using System.ComponentModel;
using System.Diagnostics;

namespace LAM.Core.Riot;

/// <summary>Starts the Riot Client, optionally going straight into a product.</summary>
public sealed class RiotLauncher
{
    /// <summary>The user dismissed the UAC prompt.</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>CreateProcess refused because the target needs a higher integrity level.</summary>
    private const int ErrorElevationRequired = 740;

    private readonly RiotPaths _paths;

    public RiotLauncher(RiotPaths paths) => _paths = paths;

    /// <summary>
    /// Launches the client into League. These are the launcher's own documented arguments — the
    /// same ones the desktop shortcut uses — so nothing here depends on undocumented behaviour.
    /// </summary>
    public Process Launch(string product = "league_of_legends", string patchline = "live")
    {
        if (!_paths.ClientExists)
            throw new RiotClientNotFoundException(
                "Could not find RiotClientServices.exe. Expected it at " + _paths.ClientServicesExe +
                ". Set the path manually in Settings if the client lives somewhere else.");

        var info = new ProcessStartInfo
        {
            FileName = _paths.ClientServicesExe,

            // ShellExecute rather than CreateProcess, because only ShellExecute can elevate.
            // RiotClientServices.exe ships an asInvoker manifest, but a machine can still carry a
            // "Run as administrator" compatibility layer for it (HKCU\...\AppCompatFlags\Layers),
            // which is common in League troubleshooting guides. Under CreateProcess that layer is a
            // hard failure — ERROR_ELEVATION_REQUIRED — rather than a UAC prompt.
            UseShellExecute = true,

            WorkingDirectory = Path.GetDirectoryName(_paths.ClientServicesExe) ?? string.Empty,

            // ArgumentList is not honoured under ShellExecute, so the command line is built here.
            // Both values are fixed identifiers with no spaces or quotes, so plain concatenation is
            // safe — nothing user-supplied reaches this string.
            Arguments = "--launch-product=" + product + " --launch-patchline=" + patchline,
        };

        try
        {
            return Process.Start(info)
                   ?? throw new RiotClientNotFoundException("The Riot Client failed to start.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            throw new RiotClientLaunchException(
                "The Windows permission prompt was dismissed, so the Riot Client did not start. " +
                "It is set to run as administrator on this PC, so that prompt has to be accepted.", ex);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired)
        {
            // Should be unreachable now that we go through ShellExecute. If it ever fires, say what
            // to look at instead of surfacing a bare Win32 message.
            throw new RiotClientLaunchException(
                "Windows refused to start the Riot Client because it requires administrator rights. " +
                "Run the account manager as administrator, or clear the \"Run this program as an " +
                "administrator\" tick box on RiotClientServices.exe (Properties → Compatibility).", ex);
        }
        catch (Win32Exception ex)
        {
            throw new RiotClientLaunchException(
                "Windows could not start the Riot Client: " + ex.Message, ex);
        }
    }
}

public sealed class RiotClientNotFoundException : Exception
{
    public RiotClientNotFoundException(string message) : base(message) { }
}

/// <summary>The client was found but Windows refused to start it.</summary>
public sealed class RiotClientLaunchException : Exception
{
    public RiotClientLaunchException(string message, Exception inner) : base(message, inner) { }
}
