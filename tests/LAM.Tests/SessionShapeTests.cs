using LAM.Core.Login;
using LAM.Core.Riot;
using Xunit;

namespace LAM.Tests;

/// <summary>
/// Where this client actually keeps a session.
///
/// The original detector looked for <c>ssid</c>/<c>clid</c> cookies under <c>rso-authenticator</c>.
/// That was a guess made while the machine was signed out, and a signed-out file shows
/// <c>psl.authorization.riot-client: null</c> — indistinguishable from "this client does not persist
/// sessions at all". A real sign-in settled it: the file grew from 491 to 3822 bytes and gained an
/// OAuth refresh token, while the cookie section still held only the device id.
///
/// The fixtures below are the real shapes, with the token values replaced.
/// </summary>
public sealed class SessionShapeTests
{
    /// <summary>Exactly what the file looks like with nobody signed in.</summary>
    private const string SignedOut = """
        psl:
            authorization:
                riot-client: null
        riot-login:
            persist: null
        rso-authenticator:
            tdid:
                name: "tdid"
                value: "a-device-identifier-not-a-session"
                persistent: true
        """;

    /// <summary>The shape after a successful sign-in with "Stay signed in" ticked.</summary>
    private const string SignedIn = """
        psl:
            authorization:
                riot-client:
                    claims: {}
                    id_token: "an-id-token"
                    is_dpop_bound: false
                    last_token_creation_time: 1787352741
                    refresh_token: "the-refresh-token-that-is-the-session"
                    refresh_tokens_session_id: "a-session-id"
                    scopes:
                    - "openid"
                    - "offline_access"
        riot-login:
            persist: true
        rso-authenticator:
            tdid:
                name: "tdid"
                value: "a-device-identifier-not-a-session"
                persistent: true
        """;

    /// <summary>An older client that stored browser cookies instead.</summary>
    private const string LegacyCookies = """
        rso-authenticator:
            ssid:
                name: "ssid"
                value: "the-legacy-session-cookie"
            tdid:
                name: "tdid"
                value: "a-device-identifier-not-a-session"
        """;

    [Fact]
    public void A_refresh_token_is_a_session()
        => Assert.True(RiotYamlService.ContainsSession(SignedIn));

    [Fact]
    public void The_signed_out_shape_is_not_a_session()
    {
        // The bug: a device cookie plus a null authorisation block was reported as "no session",
        // which was right, but so was a *real* session — because nothing looked at the token.
        Assert.False(RiotYamlService.ContainsSession(SignedOut));
    }

    [Fact]
    public void Legacy_cookies_are_still_recognised()
        => Assert.True(RiotYamlService.ContainsSession(LegacyCookies));

    [Fact]
    public void A_device_cookie_alone_is_never_a_session()
    {
        // Restoring this would launch an unauthenticated client and report success.
        Assert.False(RiotYamlService.ContainsSession("""
            rso-authenticator:
                tdid:
                    name: "tdid"
                    value: "just-the-device"
            """));
    }

    [Fact]
    public void An_empty_or_broken_document_is_not_a_session()
    {
        Assert.False(RiotYamlService.ContainsSession(null));
        Assert.False(RiotYamlService.ContainsSession(""));
        Assert.False(RiotYamlService.ContainsSession("this: [is, not, the, right, shape"));
    }

    [Fact]
    public void An_empty_refresh_token_is_not_a_session()
        => Assert.False(RiotYamlService.ContainsSession("""
            psl:
                authorization:
                    riot-client:
                        refresh_token: ""
            """));

    [Fact]
    public void A_device_bound_token_is_flagged_so_the_swap_can_decline()
    {
        // Observed as false on this client. If it ever becomes true the token is tied to a key held
        // by the client that obtained it, and restoring it elsewhere silently cannot work.
        Assert.False(RiotYamlService.IsDeviceBound(SignedIn));

        Assert.True(RiotYamlService.IsDeviceBound("""
            psl:
                authorization:
                    riot-client:
                        refresh_token: "t"
                        is_dpop_bound: true
            """));
    }

    [Fact]
    public void The_description_names_what_is_actually_stored()
    {
        var described = RiotYamlService.DescribeCookies(SignedIn);

        Assert.Contains("refresh_token", described);
        Assert.Contains("id_token", described);
        Assert.Contains("tdid", described);

        Assert.DoesNotContain("refresh_token", RiotYamlService.DescribeCookies(SignedOut));
    }

    [Fact]
    public void Clearing_for_autofill_removes_the_token_but_keeps_the_device()
    {
        // Both halves matter. Leaving the token behind sends the client to the lobby with no form to
        // type into; dropping the device id makes Riot treat the sign-in as a new machine and email
        // a verification code.
        var root = RiotYamlService.ParseMapping(SignedIn)!;

        RiotYamlService.SetPlain(root, null, "psl", "authorization", "riot-client");
        var cleared = RiotYamlService.Serialise(root);

        Assert.False(RiotYamlService.ContainsSession(cleared));
        Assert.Contains("tdid", cleared, StringComparison.Ordinal);
        Assert.Contains("a-device-identifier-not-a-session", cleared, StringComparison.Ordinal);
    }
}

/// <summary>
/// Which control gets clicked to start the game.
///
/// The home page is busier than the login form, and the friends list puts *user-chosen names* into
/// the automation tree — a live capture from this machine contained a friend called "siege player".
/// </summary>
public sealed class PlayButtonChooserTests
{
    private static ButtonCandidate Button(string? name, double area = 160 * 56, bool enabled = true)
        => new(name, null, null, enabled, 300, area);

    private static int? Choose(params ButtonCandidate[] candidates)
        => PlayButtonChooser.Choose(candidates);

    [Fact]
    public void The_play_button_is_chosen()
        => Assert.Equal(0, Choose(Button("Play"), Button("Watch Now"), Button("Gifts")));

    [Fact]
    public void A_friend_called_siege_player_is_never_chosen()
    {
        // The real reason matching is exact rather than substring.
        Assert.Null(Choose(Button("siege player")));
        Assert.Null(Choose(Button("Queencorvinata"), Button("siege player"), Button("dambledour")));
    }

    [Theory]
    [InlineData("Replay")]
    [InlineData("Replays")]
    [InlineData("Playlist")]
    [InlineData("PlayStation")]
    [InlineData("Playing now")]
    public void Near_misses_are_never_chosen(string label)
        => Assert.Null(Choose(Button(label)));

    [Fact]
    public void The_game_mode_chevron_beside_play_is_too_small_to_be_chosen()
    {
        // It opens a menu rather than launching, and it carries no useful name anyway.
        Assert.Null(Choose(Button(null, area: 40 * 40)));
    }

    [Fact]
    public void A_disabled_play_button_is_not_clicked()
        => Assert.Null(Choose(Button("Play", enabled: false)));

    [Theory]
    [InlineData("Jogar")]
    [InlineData("Jugar")]
    [InlineData("Jouer")]
    [InlineData("Spielen")]
    public void Other_client_languages_are_recognised(string label)
    {
        // The app writes a per-account client language, so a BR account really does show "Jogar".
        Assert.Equal(0, Choose(Button(label)));
    }

    [Fact]
    public void Two_things_called_play_are_too_ambiguous_to_pick()
        => Assert.Null(Choose(Button("Play"), Button("Play")));

    [Fact]
    public void An_empty_page_chooses_nothing()
        => Assert.Null(PlayButtonChooser.Choose([]));

    [Fact]
    public void Matching_ignores_case_and_surrounding_space()
        => Assert.Equal(0, Choose(Button("  PLAY  ")));
}
