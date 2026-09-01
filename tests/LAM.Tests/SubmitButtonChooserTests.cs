using LAM.Core.Login;
using Xunit;

namespace LAM.Tests;

/// <summary>
/// Which control gets clicked to submit the login form.
///
/// The highest-consequence guess in the app: it has already opened the client's version dialog, and
/// the same mistake one row higher would have started an OAuth flow with an identity provider.
///
/// Every case below uses the page's **measured** layout, captured from a live control dump rather
/// than invented, so these tests fail if the real-world discriminators stop holding:
///
/// <code>
///   USERNAME  edit          1010,546  284x45
///   PASSWORD  edit          1010,610  284x45
///   chrome    'Minimize window' 2424,288  32x24
///   chrome    'Close window'    2456,288  32x24
///   chrome    'animation'       2439,335  18x18
///   social    (unnamed) x5   1008..1244,684  ~52x33
///   ARROW     (unnamed)      1120,938  64x64   ← disabled until both fields have text
///   version   'v137.0.3'     1248,1084 48x14
/// </code>
/// </summary>
public sealed class SubmitButtonChooserTests
{
    private const double PasswordTop = 610;
    private const double SocialRowTop = 684;
    private const double ArrowTop = 938;
    private const double VersionTop = 1084;

    private static readonly LoginUiProfile Profile = new();

    private static ButtonCandidate Button(
        string? name, double top, double area, bool enabled = true, string? id = null)
        => new(name, id, null, enabled, top, area);

    /// <summary>The five unnamed social logins, exactly as the client exposes them.</summary>
    private static ButtonCandidate[] SocialRow() =>
    [
        Button(null, SocialRowTop, 52 * 33),
        Button(null, SocialRowTop, 52 * 33),
        Button(null, SocialRowTop, 52 * 33),
        Button(null, SocialRowTop, 52 * 33),
        Button(null, SocialRowTop, 53 * 33),
    ];

    private static ButtonCandidate[] WindowChrome() =>
    [
        Button("Minimize window", 288, 32 * 24),
        Button("Close window", 288, 32 * 24),
        Button("animation", 335, 18 * 18),
    ];

    private static ButtonCandidate Arrow(bool enabled = true)
        => Button(null, ArrowTop, 64 * 64, enabled);

    private static ButtonCandidate Version()
        => Button("v137.0.3", VersionTop, 48 * 14);

    private static int? Choose(params ButtonCandidate[] candidates)
        => SubmitButtonChooser.Choose(candidates, PasswordTop, Profile);

    /// <summary>The whole page, in tree order, as dumped from the running client.</summary>
    private static ButtonCandidate[] RealPage(bool arrowEnabled = true)
        => [.. WindowChrome(), .. SocialRow(), Arrow(arrowEnabled), Version()];

    [Fact]
    public void On_the_real_page_the_arrow_is_chosen()
    {
        var page = RealPage();
        var chosen = Choose(page);

        Assert.NotNull(chosen);
        Assert.Equal(64 * 64, page[chosen!.Value].Area);
        Assert.Equal(ArrowTop, page[chosen.Value].Top);
    }

    [Fact]
    public void The_version_label_is_never_chosen()
    {
        // What actually got clicked: it is enabled, below the password field, and lower than the
        // arrow, so a "lowest wins" rule picked it and opened the client's version dialog.
        var page = RealPage();
        var chosen = Choose(page);

        Assert.NotEqual("v137.0.3", page[chosen!.Value].Name);
    }

    [Fact]
    public void A_version_label_is_matched_by_shape_not_by_a_hardcoded_string()
    {
        // The literal text changes with every client update, so the rule has to be a pattern.
        foreach (var version in new[] { "v137.0.3", "v2.0.1", "14.22.1", "v1.2.3.4" })
            Assert.Null(Choose(Button(version, VersionTop, 48 * 14)));
    }

    [Fact]
    public void With_the_arrow_disabled_nothing_is_chosen()
    {
        // The exact bug. Before typing, the client keeps its submit button disabled; the chooser
        // must then decline rather than fall through to whatever else is on the page.
        Assert.Null(Choose(RealPage(arrowEnabled: false)));
    }

    [Fact]
    public void With_only_the_unnamed_social_row_nothing_is_chosen()
    {
        // These carry no name and no id, so hint matching cannot exclude them — only the
        // comparable-size rule stands between us and an OAuth flow.
        Assert.Null(Choose(SocialRow()));
    }

    [Fact]
    public void Window_chrome_is_never_chosen()
    {
        Assert.Null(Choose(WindowChrome()));

        // Nor when it is the only enabled thing alongside a disabled arrow.
        Assert.Null(Choose([.. WindowChrome(), Arrow(enabled: false)]));
    }

    [Fact]
    public void A_named_submit_button_wins_even_if_something_else_is_larger()
    {
        var candidates = new[]
        {
            Button(null, ArrowTop, 99999),              // a big unnamed panel
            Button("Sign in", ArrowTop, 64 * 64),
        };

        Assert.Equal(1, Choose(candidates));
    }

    [Fact]
    public void Buttons_above_the_password_field_are_never_chosen()
    {
        // The page's "Sign-in" tab matches the submit hints by name and sits at the top.
        Assert.Null(Choose(
            Button("Sign-in", 190, 90 * 30),
            Button("QR Code", 190, 90 * 30)));
    }

    [Fact]
    public void Two_similarly_sized_buttons_are_too_ambiguous_to_pick()
    {
        Assert.Null(Choose(
            Button(null, ArrowTop, 4096),
            Button(null, ArrowTop + 40, 3600)));   // only a 1.14x lead
    }

    [Fact]
    public void A_clear_size_lead_is_enough_to_decide()
    {
        // 4096 vs 1716 — the real arrow against a real social button.
        var chosen = Choose(
            Button(null, SocialRowTop, 52 * 33),
            Button(null, ArrowTop, 64 * 64));

        Assert.Equal(1, chosen);
    }

    [Fact]
    public void An_empty_page_chooses_nothing()
        => Assert.Null(SubmitButtonChooser.Choose([], PasswordTop, Profile));
}

/// <summary>
/// Captcha detection has to survive the login page's permanent "protected by hCaptcha" footer, which
/// previously matched on every run and reported a security check that was never shown.
/// </summary>
public sealed class CaptchaDetectionTests
{
    private static readonly LoginUiProfile Profile = new();

    private const string Footer =
        "CAN'T SIGN IN? │ CREATE ACCOUNT │ v137.0.3 │ " +
        "THIS APP IS PROTECTED BY HCAPTCHA AND ITS PRIVACY POLICY AND TERMS OF SERVICE APPLY.";

    [Fact]
    public void The_permanent_hcaptcha_footer_is_not_a_challenge()
    {
        // Two guards, either of which is sufficient: the phrase list no longer contains bare product
        // names, and unchanged text is diffed away entirely.
        Assert.False(Profile.MatchesAny(Footer, Profile.CaptchaPhrases));

        var added = AutofillStrategy.NewSince(Footer, Footer);
        Assert.False(Profile.MatchesAny(added, Profile.CaptchaPhrases));
    }

    [Fact]
    public void A_challenge_that_appears_after_submitting_is_detected()
    {
        var after = Footer + " │ Verify you are human │ Select all images with a bicycle";

        var added = AutofillStrategy.NewSince(Footer, after);

        Assert.True(Profile.MatchesAny(added, Profile.CaptchaPhrases));
        Assert.DoesNotContain("HCAPTCHA", added, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unchanged_boilerplate_is_diffed_away_even_when_reordered()
    {
        var reordered = "THIS APP IS PROTECTED BY HCAPTCHA AND ITS PRIVACY POLICY AND TERMS OF SERVICE APPLY. │ " +
                        "CAN'T SIGN IN? │ CREATE ACCOUNT │ v137.0.3";

        Assert.Equal(string.Empty, AutofillStrategy.NewSince(Footer, reordered));
    }

    [Fact]
    public void With_no_baseline_everything_counts_as_new()
    {
        // First read, nothing to compare against — do not silently swallow a real challenge.
        Assert.Equal("Verify you are human", AutofillStrategy.NewSince(string.Empty, "Verify you are human"));
    }
}
