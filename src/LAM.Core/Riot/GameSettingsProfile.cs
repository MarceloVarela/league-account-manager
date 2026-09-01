namespace LAM.Core.Riot;

/// <summary>
/// Carries League's per-account settings across an account switch.
///
/// League stores hotkeys, HUD layout and video options per account and resets them on every switch,
/// which is why a tool exists that does nothing else. This saves a snapshot once and restores it
/// after a sign-in completes.
///
/// **This writes into the game's own configuration**, which makes it the most destructive thing in
/// the app, so it is built defensively throughout: off unless explicitly enabled, a pristine copy of
/// every file taken before the first ever write, a restore that refuses to run while the client is
/// up (it would simply overwrite us on exit), and a one-click revert. Losing somebody's keybinds
/// would be a far worse outcome than not carrying them.
/// </summary>
public sealed class GameSettingsProfile
{
    /// <summary>
    /// The files League rewrites per account.
    ///
    /// <c>PersistedSettings.json</c> is the big one — hotkeys, HUD scale, video. <c>game.cfg</c> and
    /// <c>input.ini</c> hold older-style settings that some options still land in.
    /// </summary>
    public static readonly string[] SettingsFiles =
    [
        "PersistedSettings.json",
        "game.cfg",
        "input.ini",
    ];

    private readonly RiotPaths _paths;
    private readonly string _profileDirectory;
    private readonly string _pristineDirectory;
    private readonly string? _configOverride;
    private readonly LoginTraceWriter _trace;

    /// <param name="configDirectoryOverride">
    /// Where League's Config folder lives. Normally null so it is derived from the discovered
    /// install — but tests must set it, because <see cref="RiotPaths.Discover"/> quietly falls back
    /// to the real installation when given a path that does not exist, and this class writes files.
    /// A test that reached the real Config folder would rewrite the user's actual keybinds.
    /// </param>
    public GameSettingsProfile(
        RiotPaths paths,
        string storageDirectory,
        LoginTraceWriter? trace = null,
        string? configDirectoryOverride = null)
    {
        _paths = paths;
        _profileDirectory = Path.Combine(storageDirectory, "settings-profile");
        _pristineDirectory = Path.Combine(storageDirectory, "settings-pristine");
        _configOverride = configDirectoryOverride;
        _trace = trace ?? (_ => { });
    }

    /// <summary>Where League keeps the files, or null when the install cannot be found.</summary>
    public string? ConfigDirectory =>
        _configOverride
        ?? (_paths.LeagueInstallDirectory is { } install ? Path.Combine(install, "Config") : null);

    public bool HasSavedProfile =>
        Directory.Exists(_profileDirectory)
        && SettingsFiles.Any(f => File.Exists(Path.Combine(_profileDirectory, f)));

    public DateTimeOffset? SavedAtUtc
    {
        get
        {
            if (!HasSavedProfile) return null;

            var newest = SettingsFiles
                .Select(f => Path.Combine(_profileDirectory, f))
                .Where(File.Exists)
                .Max(File.GetLastWriteTimeUtc);

            return new DateTimeOffset(newest, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Captures the current settings as the profile to restore from now on.
    ///
    /// Meant to be run once, with the client closed and the settings as you want them.
    /// </summary>
    public SettingsResult Save()
    {
        var config = ConfigDirectory;
        if (config is null || !Directory.Exists(config))
            return SettingsResult.Failed("Could not find League's Config folder.");

        try
        {
            Directory.CreateDirectory(_profileDirectory);
            CapturePristine(config);

            var copied = 0;
            foreach (var file in SettingsFiles)
            {
                var source = Path.Combine(config, file);
                if (!File.Exists(source)) continue;

                File.Copy(source, Path.Combine(_profileDirectory, file), overwrite: true);
                copied++;
            }

            _trace("settings profile saved (" + copied + " files)");

            return copied == 0
                ? SettingsResult.Failed("No settings files were found to save.")
                : SettingsResult.Ok("Saved " + copied + " settings files.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SettingsResult.Failed("Could not read League's settings: " + ex.Message);
        }
    }

    /// <summary>
    /// Restores the saved profile over the current account's settings.
    ///
    /// Refuses while the client is running: League holds these files in memory and rewrites them on
    /// exit, so restoring underneath it would appear to work and then silently revert.
    /// </summary>
    public SettingsResult Restore(bool clientIsRunning)
    {
        if (!HasSavedProfile) return SettingsResult.Skipped("No settings profile has been saved yet.");

        if (clientIsRunning)
        {
            _trace("settings restore skipped: the client is running and would overwrite it");
            return SettingsResult.Skipped("League is running; its settings will be restored next time.");
        }

        var config = ConfigDirectory;
        if (config is null || !Directory.Exists(config))
            return SettingsResult.Failed("Could not find League's Config folder.");

        try
        {
            CapturePristine(config);

            var restored = 0;
            foreach (var file in SettingsFiles)
            {
                var source = Path.Combine(_profileDirectory, file);
                if (!File.Exists(source)) continue;

                File.Copy(source, Path.Combine(config, file), overwrite: true);
                restored++;
            }

            _trace("settings profile restored (" + restored + " files)");
            return SettingsResult.Ok("Restored your settings (" + restored + " files).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SettingsResult.Failed("Could not write League's settings: " + ex.Message);
        }
    }

    /// <summary>Puts back the files exactly as they were before this app first touched them.</summary>
    public SettingsResult RevertToPristine()
    {
        if (!Directory.Exists(_pristineDirectory))
            return SettingsResult.Skipped("Nothing to revert — this app has never changed your settings.");

        var config = ConfigDirectory;
        if (config is null || !Directory.Exists(config))
            return SettingsResult.Failed("Could not find League's Config folder.");

        try
        {
            var reverted = 0;
            foreach (var file in SettingsFiles)
            {
                var source = Path.Combine(_pristineDirectory, file);
                if (!File.Exists(source)) continue;

                File.Copy(source, Path.Combine(config, file), overwrite: true);
                reverted++;
            }

            _trace("settings reverted to pristine (" + reverted + " files)");
            return SettingsResult.Ok("Reverted " + reverted + " files to how they were originally.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SettingsResult.Failed("Could not restore the originals: " + ex.Message);
        }
    }

    /// <summary>
    /// Copies each file aside the first time it is about to be modified, and never again.
    ///
    /// Never again is the important half: re-copying would overwrite the originals with whatever we
    /// last wrote, and the revert would then restore our own changes rather than the user's real
    /// settings — a backup that quietly stops being a backup.
    /// </summary>
    private void CapturePristine(string config)
    {
        Directory.CreateDirectory(_pristineDirectory);

        foreach (var file in SettingsFiles)
        {
            var source = Path.Combine(config, file);
            var target = Path.Combine(_pristineDirectory, file);

            if (!File.Exists(source) || File.Exists(target)) continue;

            try { File.Copy(source, target); }
            catch (IOException) { /* a missing safety copy must not block the operation it guards */ }
        }
    }
}

/// <summary>Minimal sink so this class can log without depending on the login pipeline.</summary>
public delegate void LoginTraceWriter(string message);

public sealed record SettingsResult(bool Success, bool WasSkipped, string Message)
{
    public static SettingsResult Ok(string message) => new(true, false, message);
    public static SettingsResult Skipped(string message) => new(false, true, message);
    public static SettingsResult Failed(string message) => new(false, false, message);
}
