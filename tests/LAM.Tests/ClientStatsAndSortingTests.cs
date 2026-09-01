using LAM.Core.Model;
using LAM.Core.Riot;
using Xunit;

namespace LAM.Tests;

/// <summary>
/// Rank as the client reports it, which differs from the public API in ways that quietly produce
/// wrong output rather than errors.
/// </summary>
public sealed class RankInfoTests
{
    [Fact]
    public void Placements_are_reported_instead_of_a_meaningless_lp_total()
    {
        // Only the client exposes this, and it is the difference between "unranked" and
        // "one game from ranked" — which matters when picking an account to play.
        var placing = new RankInfo { Tier = "EMERALD", Division = "IV", LeaguePoints = 55, PlacementsRemaining = 1 };

        Assert.Equal("Emerald IV — 1 placement left", placing.ToString());
    }

    [Fact]
    public void A_settled_rank_shows_league_points()
    {
        var settled = new RankInfo { Tier = "DIAMOND", Division = "III", LeaguePoints = 27 };

        Assert.Equal("Diamond III — 27 LP", settled.ToString());
    }

    [Fact]
    public void Win_rate_needs_enough_games_to_mean_anything()
    {
        Assert.Equal(50, new RankInfo { Wins = 129, Losses = 128 }.WinRate);
        Assert.Null(new RankInfo { Wins = 3, Losses = 1 }.WinRate);   // four games says nothing
    }

    [Fact]
    public void An_apex_tier_has_no_division()
    {
        var challenger = new RankInfo { Tier = "CHALLENGER", Division = null, LeaguePoints = 1204 };

        Assert.Equal("Challenger — 1204 LP", challenger.ToString());
    }
}

/// <summary>
/// The snapshot the client provides, and how it folds into an account.
/// </summary>
public sealed class ClientStatsTests
{
    private static ClientStats RealWorld() => new()
    {
        CapturedUtc = DateTimeOffset.UtcNow,
        Solo = new RankInfo { Tier = "DIAMOND", Division = "III", LeaguePoints = 27, Wins = 129, Losses = 128 },
        Flex = new RankInfo { Tier = "EMERALD", Division = "IV", LeaguePoints = 55, PlacementsRemaining = 1 },
        PeakTier = "DIAMOND",
        RiotPoints = 2160,
        BlueEssence = 272382,
        ChampionsOwned = 236,
        SkinsOwned = 1582,
        HonorLevel = 4,
    };

    [Fact]
    public void Client_ranks_supersede_whatever_the_api_left_behind()
    {
        // The client is exact at the moment of capture; the API needs a key that expires daily and
        // returns an encrypted id that goes stale when the key is regenerated.
        var account = new AccountEntry();
        account.Identity.SoloRank = new RankInfo { Tier = "GOLD", Division = "II", LeaguePoints = 4 };

        RealWorld().ApplyTo(account);

        Assert.Equal("DIAMOND", account.Identity.SoloRank!.Tier);
        Assert.Equal("EMERALD", account.Identity.FlexRank!.Tier);
        Assert.NotNull(account.Identity.ClientStats);
    }

    [Fact]
    public void The_capture_time_is_kept_so_the_ui_can_admit_it_is_a_snapshot()
    {
        // These can only be read while that account is signed in. Presenting them as live would be a
        // lie the moment an account sits unused for a week.
        var account = new AccountEntry();
        var stats = RealWorld();

        stats.ApplyTo(account);

        Assert.Equal(stats.CapturedUtc, account.Identity.ClientStats!.CapturedUtc);
    }

    [Fact]
    public void An_empty_snapshot_is_recognised_as_empty()
    {
        Assert.True(new ClientStats().IsEmpty);
        Assert.False(RealWorld().IsEmpty);
    }
}

/// <summary>The settings-carrying feature, which writes into the game's own configuration.</summary>
public sealed class GameSettingsProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lam-settings", Guid.NewGuid().ToString("N"));
    private readonly string _league;

    public GameSettingsProfileTests()
    {
        _league = Path.Combine(_root, "League of Legends");
        Directory.CreateDirectory(Path.Combine(_league, "Config"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string ConfigFile(string name) => Path.Combine(_league, "Config", name);

    private GameSettingsProfile Profile()
    {
        // The config directory is passed explicitly. RiotPaths.Discover falls back to the *real*
        // installation when its override path does not exist, and this class writes files — the
        // first version of this test reached the actual League Config folder and was only stopped
        // by Windows denying the write.
        return new GameSettingsProfile(
            RiotPaths.Discover(),
            Path.Combine(_root, "storage"),
            trace: null,
            configDirectoryOverride: Path.Combine(_league, "Config"));
    }

    private void WriteSettings(string content)
    {
        foreach (var file in GameSettingsProfile.SettingsFiles)
            File.WriteAllText(ConfigFile(file), content);
    }

    [Fact]
    public void Saving_then_restoring_puts_your_settings_back()
    {
        var profile = Profile();

        WriteSettings("my keybinds");
        Assert.True(profile.Save().Success);

        WriteSettings("whatever League reset them to");
        Assert.True(profile.Restore(clientIsRunning: false).Success);

        Assert.Equal("my keybinds", File.ReadAllText(ConfigFile("game.cfg")));
    }

    [Fact]
    public void Restoring_is_refused_while_the_client_is_running()
    {
        // League holds these files open and rewrites them on exit, so a restore underneath it would
        // appear to work and then silently revert — worse than declining.
        var profile = Profile();

        WriteSettings("mine");
        profile.Save();
        WriteSettings("theirs");

        var result = profile.Restore(clientIsRunning: true);

        Assert.True(result.WasSkipped);
        Assert.Equal("theirs", File.ReadAllText(ConfigFile("game.cfg")));
    }

    [Fact]
    public void The_pristine_copy_is_taken_once_and_never_overwritten()
    {
        // The important half. Re-copying would replace the originals with whatever we last wrote,
        // and Revert would then restore our own changes — a backup that stops being a backup.
        var profile = Profile();

        WriteSettings("the user's original settings");
        profile.Save();

        WriteSettings("changed later");
        profile.Save();                      // second save must not refresh the pristine copy

        Assert.True(profile.RevertToPristine().Success);
        Assert.Equal("the user's original settings", File.ReadAllText(ConfigFile("game.cfg")));
    }

    [Fact]
    public void Reverting_without_ever_having_written_is_a_no_op()
    {
        var profile = Profile();

        Assert.True(profile.RevertToPristine().WasSkipped);
    }

    [Fact]
    public void Restoring_without_a_saved_profile_is_a_no_op()
    {
        var profile = Profile();

        WriteSettings("untouched");
        Assert.True(profile.Restore(clientIsRunning: false).WasSkipped);
        Assert.Equal("untouched", File.ReadAllText(ConfigFile("game.cfg")));
    }
}
