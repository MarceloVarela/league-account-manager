using LAM.Core.Login;
using LAM.Core.Model;
using LAM.Core.Riot;
using Xunit;

namespace LAM.Tests;

/// <summary>
/// Covers the chaining rule the whole design rests on: try the saved session, type only when that
/// is not an option, and always fold what was learned back into the account.
///
/// The strategies are faked because the real ones drive a running Riot Client. What is under test
/// here is the decision-making, which is where a mistake would be silent — a login that quietly
/// types every time instead of using the fast path still works, it is just no better than the tools
/// this is meant to replace.
/// </summary>
public sealed class LoginOrchestratorTests
{
    private sealed class FakeStrategy : ILoginStrategy
    {
        private readonly Func<LoginContext, LoginResult> _behaviour;

        public FakeStrategy(string name, Func<LoginContext, LoginResult> behaviour)
        {
            Name = name;
            _behaviour = behaviour;
        }

        public string Name { get; }
        public bool Attemptable { get; set; } = true;
        public int Calls { get; private set; }

        public bool CanAttempt(LoginContext context) => Attemptable;

        public Task<LoginResult> LoginAsync(LoginContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_behaviour(context));
        }
    }

    /// <summary>
    /// Stands in for the real process manager so these tests do not depend on whether a League
    /// game happens to be running on the machine executing them — which it was, the first time
    /// this suite ran, and every case failed on the refusal path rather than the logic under test.
    /// </summary>
    private sealed class FakeProcessManager : RiotProcessManager
    {
        public bool GameRunning { get; set; }
        public override bool IsGameInProgress() => GameRunning;
    }

    private static (LoginOrchestrator Orchestrator, AccountEntry Account, List<LoginProgress> Reported)
        Build(params ILoginStrategy[] strategies)
    {
        var account = new AccountEntry { Label = "test", LoginUsername = "user", Password = "pw" };
        var reported = new List<LoginProgress>();
        var orchestrator = new LoginOrchestrator(strategies, new FakeProcessManager());
        return (orchestrator, account, reported);
    }

    private static Task<LoginResult> Run(
        LoginOrchestrator orchestrator, AccountEntry account, List<LoginProgress> reported,
        AppSettings? settings = null)
        => orchestrator.LoginAsync(
            account,
            settings ?? new AppSettings(),
            new Progress<LoginProgress>(reported.Add),
            requestVerificationCode: null,
            CancellationToken.None);

    [Fact]
    public async Task A_falling_back_strategy_hands_over_to_the_next_one()
    {
        var swap = new FakeStrategy("swap", _ => LoginResult.FallBack("session expired"));
        var autofill = new FakeStrategy("autofill", _ => LoginResult.Success());
        var (orchestrator, account, reported) = Build(swap, autofill);

        var result = await Run(orchestrator, account, reported);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, swap.Calls);
        Assert.Equal(1, autofill.Calls);
    }

    [Fact]
    public async Task A_succeeding_strategy_stops_the_chain()
    {
        var swap = new FakeStrategy("swap", _ => LoginResult.Success());
        var autofill = new FakeStrategy("autofill", _ => LoginResult.Success());
        var (orchestrator, account, reported) = Build(swap, autofill);

        await Run(orchestrator, account, reported);

        Assert.Equal(1, swap.Calls);
        Assert.Equal(0, autofill.Calls);   // typing must not happen once the fast path worked
    }

    [Fact]
    public async Task A_hard_failure_does_not_fall_through_to_typing()
    {
        // A rejected password is not a reason to try again a different way — it would just burn
        // another attempt against Riot's rate limiter.
        var swap = new FakeStrategy("swap", _ => LoginResult.Failed("wrong password"));
        var autofill = new FakeStrategy("autofill", _ => LoginResult.Success());
        var (orchestrator, account, reported) = Build(swap, autofill);

        var result = await Run(orchestrator, account, reported);

        Assert.Equal(LoginOutcome.Failed, result.Outcome);
        Assert.Equal(0, autofill.Calls);
    }

    [Fact]
    public async Task An_aborted_login_stops_immediately()
    {
        var swap = new FakeStrategy("swap", _ => LoginResult.Aborted("focus lost"));
        var autofill = new FakeStrategy("autofill", _ => LoginResult.Success());
        var (orchestrator, account, reported) = Build(swap, autofill);

        var result = await Run(orchestrator, account, reported);

        Assert.Equal(LoginOutcome.Aborted, result.Outcome);
        Assert.Equal(0, autofill.Calls);
    }

    [Fact]
    public async Task A_strategy_that_throws_lets_the_next_one_try()
    {
        var swap = new FakeStrategy("swap", _ => throw new InvalidOperationException("boom"));
        var autofill = new FakeStrategy("autofill", _ => LoginResult.Success());
        var (orchestrator, account, reported) = Build(swap, autofill);

        var result = await Run(orchestrator, account, reported);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, autofill.Calls);
    }

    [Fact]
    public async Task A_captured_session_is_written_back_to_the_account()
    {
        var swap = new FakeStrategy("swap", _ => LoginResult.FallBack("no session yet"));
        var autofill = new FakeStrategy("autofill", context =>
        {
            context.SessionCaptured?.Invoke(new StoredSession { Yaml = new SecretText("rso-authenticator: {}") });
            return LoginResult.Success();
        });
        var (orchestrator, account, reported) = Build(swap, autofill);

        Assert.Null(account.Session);
        await Run(orchestrator, account, reported);

        // This is the bootstrap: typing once leaves behind the session the fast path needs.
        Assert.NotNull(account.Session);
        Assert.False(account.Session!.KnownExpired);
    }

    [Fact]
    public async Task An_observed_identity_is_folded_into_the_account()
    {
        var autofill = new FakeStrategy("autofill", context =>
        {
            context.IdentityObserved?.Invoke(new IdentityReading
            {
                Puuid = "puuid-123",
                GameName = "Caio",
                TagLine = "BR1",
                SummonerLevel = 214,
            });
            return LoginResult.Success();
        });
        var (orchestrator, account, reported) = Build(autofill);

        await Run(orchestrator, account, reported);

        Assert.Equal("puuid-123", account.Identity.RiotAccountId);
        Assert.Equal("Caio", account.Identity.GameName);
        Assert.Equal(214, account.Identity.SummonerLevel);
        Assert.Equal("Caio#BR1", account.DisplayRiotId);
        Assert.Single(account.Identity.NameHistory);
    }

    [Fact]
    public async Task A_successful_login_records_when_and_how_often()
    {
        var autofill = new FakeStrategy("autofill", _ => LoginResult.Success());
        var (orchestrator, account, reported) = Build(autofill);

        await Run(orchestrator, account, reported);

        Assert.NotNull(account.LastUsedUtc);
        Assert.Equal(1, account.LaunchCount);
    }

    [Fact]
    public async Task With_no_applicable_strategy_the_message_says_what_to_do()
    {
        var swap = new FakeStrategy("swap", _ => LoginResult.Success()) { Attemptable = false };
        var (orchestrator, account, reported) = Build(swap);
        account.Password = null;

        var result = await Run(orchestrator, account, reported);

        Assert.Equal(LoginOutcome.Failed, result.Outcome);
        Assert.Contains("add its password", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_running_game_refuses_the_switch_before_any_strategy_runs()
    {
        // Closing the client mid-match costs a leaver penalty, so this must be refused outright
        // rather than attempted and rolled back.
        var swap = new FakeStrategy("swap", _ => LoginResult.Success());
        var account = new AccountEntry { Label = "test", LoginUsername = "user", Password = "pw" };
        var processes = new FakeProcessManager { GameRunning = true };
        var orchestrator = new LoginOrchestrator([swap], processes);

        var result = await orchestrator.LoginAsync(
            account, new AppSettings(), new Progress<LoginProgress>(_ => { }), null, CancellationToken.None);

        Assert.Equal(LoginOutcome.Failed, result.Outcome);
        Assert.Contains("leaver penalty", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, swap.Calls);
        Assert.Equal(0, account.LaunchCount);
    }

    [Fact]
    public async Task Every_fallback_reason_survives_into_the_final_message()
    {
        var swap = new FakeStrategy("swap", _ => LoginResult.FallBack("session expired"));
        var autofill = new FakeStrategy("autofill", _ => LoginResult.FallBack("no password"));
        var (orchestrator, account, reported) = Build(swap, autofill);

        var result = await Run(orchestrator, account, reported);

        Assert.Contains("session expired", result.Message);
        Assert.Contains("no password", result.Message);
    }
}

/// <summary>
/// The rules that decide whether the fast path is even worth trying. Getting these wrong is what
/// would make the app relaunch the client for a session that cannot possibly work.
/// </summary>
public sealed class StoredSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private static StoredSession Session(TimeSpan age, bool expired = false) => new()
    {
        Yaml = new SecretText("rso-authenticator:\n  ssid:\n    value: \"x\""),
        CapturedUtc = Now - age,
        KnownExpired = expired,
    };

    [Fact]
    public void A_fresh_session_is_usable() =>
        Assert.True(Session(TimeSpan.FromDays(1)).IsProbablyUsable(Now));

    [Fact]
    public void A_session_known_to_be_expired_is_not_retried() =>
        Assert.False(Session(TimeSpan.FromDays(1), expired: true).IsProbablyUsable(Now));

    [Fact]
    public void A_session_past_the_staleness_cutoff_is_not_retried() =>
        Assert.False(Session(StoredSession.StaleAfter + TimeSpan.FromDays(1)).IsProbablyUsable(Now));

    [Fact]
    public void An_empty_session_is_never_usable() =>
        Assert.False(new StoredSession { CapturedUtc = Now }.IsProbablyUsable(Now));
}

public sealed class AccountEntryTests
{
    [Fact]
    public void Search_matches_label_tags_riot_id_and_recovery_email()
    {
        var account = new AccountEntry
        {
            Label = "main account",
            LoginUsername = "marc@example.com",
            Tags = { "smurf", "BR" },
            Recovery = new RecoveryInfo { Email = "recovery@example.com" },
        };
        account.Identity.RecordName("Caio", "BR1", DateTimeOffset.UtcNow);

        Assert.True(account.MatchesSearch("main"));
        Assert.True(account.MatchesSearch("SMURF"));       // case-insensitive
        Assert.True(account.MatchesSearch("Caio"));
        Assert.True(account.MatchesSearch("caio#br1"));
        Assert.True(account.MatchesSearch("recovery@"));
        Assert.True(account.MatchesSearch(""));            // empty query matches everything
        Assert.False(account.MatchesSearch("nonexistent"));
    }

    [Fact]
    public void Name_history_only_grows_when_the_riot_id_actually_changes()
    {
        var snapshot = new IdentitySnapshot();
        var t0 = DateTimeOffset.UtcNow;

        Assert.True(snapshot.RecordName("Caio", "BR1", t0));
        Assert.False(snapshot.RecordName("Caio", "BR1", t0.AddDays(1)));   // same name, no entry
        Assert.True(snapshot.RecordName("CaioRenamed", "BR1", t0.AddDays(2)));

        Assert.Equal(2, snapshot.NameHistory.Count);
        Assert.Equal("CaioRenamed", snapshot.GameName);
    }

    [Fact]
    public void Recovery_completeness_reflects_how_much_is_filled_in()
    {
        Assert.Equal(0, new RecoveryInfo().Completeness());

        var full = new RecoveryInfo
        {
            Email = "a@b.com",
            PhoneNumber = "123",
            ApproximateCreated = new DateOnly(2015, 3, 1),
            FirstChampionPurchased = "Ashe",
            FirstPurchaseReference = "order-1",
        };
        Assert.Equal(1.0, full.Completeness());
    }
}
