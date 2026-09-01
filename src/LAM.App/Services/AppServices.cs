using LAM.Core.Login;
using LAM.Core.Riot;
using LAM.Core.Vault;

namespace LAM.App.Services;

/// <summary>
/// The composition root. Small enough that a container would be more ceremony than it is worth.
/// </summary>
public sealed class AppServices
{
    public AppServices(string? vaultRootOverride = null, string? riotClientOverride = null)
    {
        Paths = new VaultPaths(vaultRootOverride);
        Paths.EnsureCreated();

        Repository = new VaultRepository(Paths);
        Hello = new HelloUnlock(Paths);

        RiotPaths = RiotPaths.Discover(riotClientOverride);
        Yaml = new RiotYamlService(RiotPaths, Paths.RiotConfigBackupDirectory);
        Processes = new RiotProcessManager();
        Launcher = new RiotLauncher(RiotPaths);
        Identity = new LcuIdentityProbe(RiotPaths);
        UiProfile = LoginUiProfile.LoadOrDefault(Paths.Root);
        Trace = LoginTrace.InDirectory(Paths.LogDirectory);

        Capture = new PostLoginCapture(Yaml, Identity, clock: null, trace: Trace);
        GameStarter = new GameStarter(Processes, UiProfile, Trace);
        AccountProbe = new ClientAccountProbe(RiotPaths, Yaml);
        Icons = new ProfileIconCache(Paths.IconCacheDirectory);
        Catalogue = new SkinCatalogue(Paths.CatalogueDirectory);
        SkinArt = new SkinArtCache(Paths.SkinArtDirectory, RiotPaths);
        Catalogue.TryLoad();
        Capture.UseGameStarter(GameStarter);
        StatsProbe = new ClientStatsProbe(RiotPaths);
        LootProbe = new ClientLootProbe(RiotPaths);
        Repair = new ClientRepair(RiotPaths, Processes);
        Capture.UseLootProbe(LootProbe);
        Capture.UseAccountProbe(AccountProbe);
        GameSettings = new GameSettingsProfile(RiotPaths, Paths.GameSettingsDirectory, Trace.Write);
        Capture.UseStatsProbe(StatsProbe);
        Capture.UseGameSettings(GameSettings);

        Orchestrator = LoginOrchestrator.CreateDefault(
            RiotPaths, Yaml, Processes, Launcher, Capture, UiProfile, clock: null, trace: Trace);
    }

    public VaultPaths Paths { get; }
    public VaultRepository Repository { get; }
    public HelloUnlock Hello { get; }

    public RiotPaths RiotPaths { get; }
    public RiotYamlService Yaml { get; }
    public RiotProcessManager Processes { get; }
    public RiotLauncher Launcher { get; }
    public LcuIdentityProbe Identity { get; }
    public PostLoginCapture Capture { get; }
    public LoginUiProfile UiProfile { get; }
    public LoginTrace Trace { get; }
    public GameStarter GameStarter { get; }
    public ClientAccountProbe AccountProbe { get; }
    public ProfileIconCache Icons { get; }
    public SkinCatalogue Catalogue { get; }
    public SkinArtCache SkinArt { get; }
    public ClientStatsProbe StatsProbe { get; }
    public ClientLootProbe LootProbe { get; }
    public ClientRepair Repair { get; }
    public GameSettingsProfile GameSettings { get; }
    public LoginOrchestrator Orchestrator { get; }

    /// <summary>
    /// Rebuilds the orchestrator after the Riot client path is changed in settings, so the new path
    /// takes effect without a restart.
    /// </summary>
    public AppServices WithRiotClientPath(string? path) => new(Paths.Root, path);
}
