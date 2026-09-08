using System.IO;
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
        MatchProbe = new ClientMatchProbe(RiotPaths);
        Repair = new ClientRepair(RiotPaths, Processes);
        Capture.UseLootProbe(LootProbe);
        Capture.UseMatchProbe(MatchProbe);
        Capture.UseAccountProbe(AccountProbe);
        GameSettings = new GameSettingsProfile(RiotPaths, Paths.GameSettingsDirectory, Trace.Write);
        Capture.UseStatsProbe(StatsProbe);
        Capture.UseGameSettings(GameSettings);

        Orchestrator = LoginOrchestrator.CreateDefault(
            RiotPaths, Yaml, Processes, Launcher, Capture, UiProfile, clock: null, trace: Trace);

        // Stealth is opt-in, off by default, and unavailable until the build is pointed at a real
        // hostname and certificate. Everything about it fails open: a null session simply means the
        // client launches exactly as it always has.
        Orchestrator.UseStealth((settings, token) =>
            LAM.Core.Stealth.StealthEndpoints.Configured
                ? LAM.Core.Stealth.StealthSession.TryStartAsync(
                    new LAM.Core.Stealth.StealthOptions(
                        LAM.Core.Stealth.StealthEndpoints.Host,
                        LAM.Core.Stealth.StealthEndpoints.CertificateUrl,
                        Path.Combine(Paths.Root, "stealth.pfx"),
                        EmbeddedCertificate()),
                    () => settings.StealthMode,
                    () => settings.LobbyChat,
                    Trace.Write,
                    token)
                : Task.FromResult<LAM.Core.Stealth.StealthSession?>(null));
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

    public ClientMatchProbe MatchProbe { get; }
    public ClientRepair Repair { get; }
    public GameSettingsProfile GameSettings { get; }
    public LoginOrchestrator Orchestrator { get; }

    /// <summary>
    /// Rebuilds the orchestrator after the Riot client path is changed in settings, so the new path
    /// takes effect without a restart.
    /// </summary>
    public AppServices WithRiotClientPath(string? path) => new(Paths.Root, path);

    /// <summary>
    /// The certificate shipped with this build, if there is one.
    ///
    /// A fresh install has to work before it can reach the refresh URL, and a user behind a filter
    /// that blocks it should still get stealth for as long as this copy is valid. Absent until the
    /// asset is added, which is why every caller treats null as ordinary.
    /// </summary>
    private static byte[]? EmbeddedCertificate()
    {
        try
        {
            var stream = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/stealth.pfx"))?.Stream;

            if (stream is null) return null;

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }
}
