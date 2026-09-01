using System.Text.Json;

namespace LAM.Core.Riot;

/// <summary>
/// Locates the Riot Client and the files we need to read or write.
///
/// Everything is discovered from <c>RiotClientInstalls.json</c> rather than hardcoded, because
/// people install the client on a second drive and Riot has moved these paths before. Only the
/// location of that one manifest is assumed, and it is fixed by the installer.
/// </summary>
public sealed class RiotPaths
{
    /// <summary>Written by the Riot installer; the entry point for discovering everything else.</summary>
    public static string InstallsManifest => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Riot Games", "RiotClientInstalls.json");

    private RiotPaths(string clientServicesExe, string? leagueInstallDirectory)
    {
        ClientServicesExe = clientServicesExe;
        LeagueInstallDirectory = leagueInstallDirectory;
    }

    /// <summary>RiotClientServices.exe — the launcher we start, not the Electron UI process.</summary>
    public string ClientServicesExe { get; }

    /// <summary>The League install root, when one is registered.</summary>
    public string? LeagueInstallDirectory { get; }

    private static string LocalRiot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games");

    public string RiotClientConfigDirectory => Path.Combine(LocalRiot, "Riot Client", "Config");
    public string RiotClientDataDirectory => Path.Combine(LocalRiot, "Riot Client", "Data");

    /// <summary>Holds the session cookies. This is the file the swap strategy reads and writes.</summary>
    public string PrivateSettingsFile => Path.Combine(RiotClientDataDirectory, "RiotGamesPrivateSettings.yaml");

    /// <summary>Holds region and locale, among ~60 unrelated keys we must not disturb.</summary>
    public string ClientSettingsFile => Path.Combine(RiotClientConfigDirectory, "RiotClientSettings.yaml");

    /// <summary>The Riot Client local API lockfile; present only while the client is running.</summary>
    public string RiotClientLockfile => Path.Combine(RiotClientConfigDirectory, "lockfile");

    /// <summary>The League client (LCU) lockfile; present only once League itself is up.</summary>
    public string? LeagueLockfile => LeagueInstallDirectory is null
        ? null
        : Path.Combine(LeagueInstallDirectory, "lockfile");

    /// <summary>
    /// Resolves the install from the manifest, falling back to the default location if the manifest
    /// is missing or unreadable. <paramref name="overridePath"/> wins when the user has set one.
    /// </summary>
    public static RiotPaths Discover(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return new RiotPaths(overridePath, FindLeagueDirectory());

        var fromManifest = ReadManifest();
        if (fromManifest is not null)
            return new RiotPaths(fromManifest.Value.Client, fromManifest.Value.League);

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Riot Games", "Riot Client", "RiotClientServices.exe");

        if (!File.Exists(fallback))
            fallback = @"C:\Riot Games\Riot Client\RiotClientServices.exe";

        return new RiotPaths(fallback, FindLeagueDirectory());
    }

    private static (string Client, string? League)? ReadManifest()
    {
        try
        {
            if (!File.Exists(InstallsManifest)) return null;

            using var document = JsonDocument.Parse(File.ReadAllText(InstallsManifest));
            var root = document.RootElement;

            // rc_live is the live-patchline client; rc_default is the general one. Either works,
            // and in practice they point at the same executable.
            var client = ReadString(root, "rc_live") ?? ReadString(root, "rc_default");
            if (client is null) return null;

            client = Normalise(client);
            if (!File.Exists(client)) return null;

            return (client, FindLeagueDirectory(root));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// The manifest keys <c>associated_client</c> by install directory, so the League root is the
    /// key whose path looks like a League install rather than a value.
    /// </summary>
    private static string? FindLeagueDirectory(JsonElement? root = null)
    {
        try
        {
            JsonDocument? owned = null;
            JsonElement element;

            if (root is not null)
            {
                element = root.Value;
            }
            else
            {
                if (!File.Exists(InstallsManifest)) return FallbackLeagueDirectory();
                owned = JsonDocument.Parse(File.ReadAllText(InstallsManifest));
                element = owned.RootElement;
            }

            using (owned)
            {
                if (element.TryGetProperty("associated_client", out var associated)
                    && associated.ValueKind == JsonValueKind.Object)
                {
                    foreach (var entry in associated.EnumerateObject())
                    {
                        var directory = Normalise(entry.Name).TrimEnd(Path.DirectorySeparatorChar);
                        if (directory.Contains("League of Legends", StringComparison.OrdinalIgnoreCase)
                            && Directory.Exists(directory))
                        {
                            return directory;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // fall through
        }

        return FallbackLeagueDirectory();
    }

    private static string? FallbackLeagueDirectory()
    {
        var guess = @"C:\Riot Games\League of Legends";
        return Directory.Exists(guess) ? guess : null;
    }

    /// <summary>The manifest stores forward slashes on Windows; normalise before touching the disk.</summary>
    private static string Normalise(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    public bool ClientExists => File.Exists(ClientServicesExe);
}
