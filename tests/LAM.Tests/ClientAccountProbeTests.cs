using System.Text;
using System.Text.Json;
using LAM.Core.Model;
using LAM.Core.Riot;
using Xunit;

namespace LAM.Tests;

/// <summary>
/// Reading the recovery dossier out of the client instead of asking the user to type it.
///
/// The original conclusion — that Riot exposes no email, no creation date and no security settings —
/// was true of the *public API* and wrong as a reason to leave every field blank. The client's own
/// id_token carries most of it, and the claims below are the real ones observed on a live machine.
/// </summary>
public sealed class ClientAccountProbeTests
{
    /// <summary>Builds a private-settings document around a token with the given claims.</summary>
    private static string SettingsWithClaims(object claims)
    {
        var json = JsonSerializer.Serialize(claims);
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // Header and signature are never inspected; only the payload is read.
        var token = "eyJhbGciOiJSUzI1NiJ9." + payload + ".not-a-real-signature";

        return "psl:\n" +
               "    authorization:\n" +
               "        riot-client:\n" +
               "            id_token: \"" + token + "\"\n" +
               "            refresh_token: \"a-refresh-token\"\n";
    }

    /// <summary>The claim set actually observed on the live account.</summary>
    private static string RealWorldSettings() => SettingsWithClaims(new
    {
        sub = "11111111-2222-3333-4444-555555555555",
        acct = new { game_name = "Testplayer", tag_line = "00000", state = "ENABLED" },
        account_verified = true,
        phone_number_verified = true,
        amr = new[] { "password", "mfa" },
        country = "bra",
        country_at = 1611017707000L,
        lol = new[] { new { uid = 1234567, uname = "legacylogin", cpid = "BR1" } },
        lol_region = new[]
        {
            new { cpid = "BR1", active = true },
            new { cpid = "NA1", active = false },
        },
    });

    [Fact]
    public void The_durable_account_id_comes_from_the_token()
    {
        var facts = ClientAccountProbe.ReadIdTokenClaims(RealWorldSettings());

        Assert.NotNull(facts);
        Assert.Equal("11111111-2222-3333-4444-555555555555", facts!.RiotAccountId);
        Assert.Equal("Testplayer", facts.GameName);
        Assert.Equal("00000", facts.TagLine);
    }

    [Fact]
    public void Two_factor_is_detected_from_the_authentication_methods()
    {
        // amr lists how the session was authenticated. "mfa" being present is a fact about the
        // account that the user would otherwise have to remember to tick.
        Assert.True(ClientAccountProbe.ReadIdTokenClaims(RealWorldSettings())!.MfaEnabled);

        var noMfa = SettingsWithClaims(new { sub = "x", amr = new[] { "password" } });
        Assert.False(ClientAccountProbe.ReadIdTokenClaims(noMfa)!.MfaEnabled);
    }

    [Fact]
    public void A_phone_and_a_verified_email_are_reported()
    {
        var facts = ClientAccountProbe.ReadIdTokenClaims(RealWorldSettings())!;

        Assert.True(facts.PhoneOnFile);
        Assert.True(facts.EmailVerified);
    }

    [Fact]
    public void The_country_timestamp_becomes_a_usable_date()
    {
        // 1611017707000 ms — the closest thing to a creation date the client offers, and Riot
        // Support asks for an approximate one.
        var facts = ClientAccountProbe.ReadIdTokenClaims(RealWorldSettings())!;

        Assert.NotNull(facts.CountrySetUtc);
        Assert.Equal(2021, facts.CountrySetUtc!.Value.Year);
        Assert.Equal(1, facts.CountrySetUtc.Value.Month);
        Assert.Equal("bra", facts.Country);
    }

    [Fact]
    public void Region_history_is_captured_with_the_active_one_first()
    {
        // An inactive NA entry beside an active BR one means the account was transferred — exactly
        // the sort of history a support ticket turns on.
        var facts = ClientAccountProbe.ReadIdTokenClaims(RealWorldSettings())!;

        Assert.Equal(2, facts.Regions.Count);
        Assert.Equal("BR1", facts.Regions[0].Platform);
        Assert.True(facts.Regions[0].Active);
        Assert.Equal("NA1", facts.Regions[1].Platform);
        Assert.False(facts.Regions[1].Active);
    }

    [Fact]
    public void A_signed_out_or_malformed_document_yields_nothing()
    {
        Assert.Null(ClientAccountProbe.ReadIdTokenClaims(null));
        Assert.Null(ClientAccountProbe.ReadIdTokenClaims("psl:\n    authorization:\n        riot-client: null\n"));
        Assert.Null(ClientAccountProbe.ReadIdTokenClaims("not: [valid"));
    }

    [Fact]
    public void A_token_that_is_not_decodable_is_ignored_rather_than_throwing()
    {
        var broken = "psl:\n    authorization:\n        riot-client:\n            id_token: \"garbage\"\n";
        Assert.Null(ClientAccountProbe.ReadIdTokenClaims(broken));
    }

    // ---- merging into the account ------------------------------------------

    [Fact]
    public void Observations_never_overwrite_something_typed()
    {
        // What the user entered is a statement about the account; what the client reports is a
        // statement about this session. Silently replacing the former would destroy real work.
        var account = new AccountEntry
        {
            Recovery = new RecoveryInfo { ApproximateCreated = new DateOnly(2013, 5, 1) },
        };

        ClientAccountProbe.ReadIdTokenClaims(RealWorldSettings())!.ApplyTo(account, DateTimeOffset.UtcNow);

        Assert.Equal(new DateOnly(2013, 5, 1), account.Recovery.ApproximateCreated);
    }

    [Fact]
    public void An_empty_field_is_filled_in_from_the_observation()
    {
        var account = new AccountEntry();

        ClientAccountProbe.ReadIdTokenClaims(RealWorldSettings())!.ApplyTo(account, DateTimeOffset.UtcNow);

        Assert.Equal(2021, account.Recovery.ApproximateCreated!.Value.Year);
        Assert.Equal("11111111-2222-3333-4444-555555555555", account.Identity.RiotAccountId);
        Assert.NotNull(account.Identity.Observed);

        // Two-factor is observed, not asserted. Nothing writes into the typed field any more: it
        // used to be latched true and could then never be cleared by a later observation, so turning
        // 2FA off on the Riot account left this permanently and silently wrong. The observation is
        // kept beside it and the two are combined only for display.
        Assert.False(account.Recovery.MfaEnabled);
        Assert.True(account.Identity.Observed!.MfaEnabled);
        Assert.True(account.Recovery.MfaLooksEnabled(account.Identity.Observed));
    }

    // ---- the masked email check --------------------------------------------

    [Theory]
    [InlineData("mailbox@example.com", true)]    // prefix and suffix both line up
    [InlineData("marcus@gmail.com", true)]        // the mask genuinely cannot rule this out
    [InlineData("different@gmail.com", false)]    // wrong prefix
    [InlineData("mailbox@example.net", false)]   // wrong suffix
    public void A_typed_email_is_checked_against_the_mask(string typed, bool expected)
    {
        // Catching an email saved against the wrong account is the whole point of holding the mask —
        // it is otherwise invisible until the day it matters.
        var facts = new ClientAccountFacts { MaskedEmail = "ma***@*****.com" };

        Assert.Equal(expected, facts.EmailLooksConsistentWith(typed));
    }

    [Fact]
    public void With_nothing_to_compare_the_check_is_inconclusive_rather_than_false()
    {
        // A blank answer must not be reported as a mismatch.
        Assert.Null(new ClientAccountFacts { MaskedEmail = "ma***@*****.com" }.EmailLooksConsistentWith(null));
        Assert.Null(new ClientAccountFacts().EmailLooksConsistentWith("someone@example.com"));
    }
}
