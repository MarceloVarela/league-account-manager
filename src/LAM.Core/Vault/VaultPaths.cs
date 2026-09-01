namespace LAM.Core.Vault;

/// <summary>Where the app keeps its state. All under %APPDATA%, never beside the executable.</summary>
public sealed class VaultPaths
{
    public VaultPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LeagueAccountManager");
        BackupDirectory = Path.Combine(Root, "backups");
    }

    public string Root { get; }
    public string VaultFile => Path.Combine(Root, "vault.dat");
    public string HelloFile => Path.Combine(Root, "hello.bin");
    public string BackupDirectory { get; }

    /// <summary>Safety copies of Riot config files taken before we first modify them.</summary>
    public string RiotConfigBackupDirectory => Path.Combine(Root, "riot-config-backup");

    /// <summary>Where the sign-in trace goes. Diagnostics only, and never holds a secret.</summary>
    public string LogDirectory => Path.Combine(Root, "logs");

    /// <summary>Cached profile icons from Data Dragon. Public artwork; nothing sensitive.</summary>
    public string IconCacheDirectory => Path.Combine(Root, "icons");

    /// <summary>Saved League settings profile, plus pristine copies for reverting.</summary>
    public string GameSettingsDirectory => Path.Combine(Root, "game-settings");

    /// <summary>Shared skin/champion catalogue and tile art. Public data, so not encrypted.</summary>
    public string CatalogueDirectory => Path.Combine(Root, "catalogue");

    public string SkinArtDirectory => Path.Combine(CatalogueDirectory, "tiles");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(BackupDirectory);
        Directory.CreateDirectory(RiotConfigBackupDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(IconCacheDirectory);
        Directory.CreateDirectory(SkinArtDirectory);
    }

    public bool VaultExists => File.Exists(VaultFile);
}
