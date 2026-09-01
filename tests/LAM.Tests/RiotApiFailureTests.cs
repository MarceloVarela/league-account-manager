using System.Net;
using LAM.Core.Model;
using LAM.Core.Riot;
using Xunit;

namespace LAM.Tests;

/// <summary>
/// How the API client interprets a refusal.
///
/// A 403 was previously reported as "Riot rejected your API key" no matter what caused it. That
/// swallowed a real bug for two rounds: the rank lookup was falling back to
/// <c>league-v4/entries/by-summoner/</c> with the plain numeric summoner id the League client
/// supplies (1395378), where the endpoint requires an *encrypted* one. Riot answered 403, the app
/// blamed a key that was demonstrably working, and the actual fault stayed hidden.
/// </summary>
public sealed class RiotApiFailureTests
{
    /// <summary>Serves a scripted sequence of status codes so the interpretation can be asserted.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _statuses;

        public ScriptedHandler(params HttpStatusCode[] statuses) => _statuses = new Queue<HttpStatusCode>(statuses);

        /// <summary>A realistic 78-character encrypted PUUID, as account-v1 actually returns.</summary>
        public const string EncryptedPuuid =
            "EXAMPLE_ENCRYPTED_PUUID_0123456789abcdef0123456789abcdef0123456789abcdef012345";

        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.AbsolutePath);

            var status = _statuses.Count > 0 ? _statuses.Dequeue() : HttpStatusCode.OK;

            // account-v1 must hand back a PUUID, because the refresh now resolves one from the Riot
            // ID on every run rather than reusing a stored value.
            var body = status != HttpStatusCode.OK
                ? "{}"
                : request.RequestUri.AbsolutePath.Contains("/riot/account/", StringComparison.Ordinal)
                    ? "{\"puuid\":\"" + EncryptedPuuid + "\",\"gameName\":\"Testplayer\",\"tagLine\":\"00000\"}"
                    : request.RequestUri.AbsolutePath.Contains("/league/", StringComparison.Ordinal)
                        ? "[]"
                        : "{\"summonerLevel\":625}";

            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            return Task.FromResult(response);
        }
    }

    private static AccountEntry Account()
    {
        var account = new AccountEntry { Region = "BR" };
        // A GUID, exactly as the client supplies it. It must never be sent to the API.
        account.Identity.RiotAccountId = "11111111-2222-3333-4444-555555555555";
        account.Identity.RecordName("Testplayer", "00000", DateTimeOffset.UtcNow);
        account.Identity.SummonerId = "1395378";   // the numeric id from the League client
        return account;
    }

    private static RiotApiClient Client(ScriptedHandler handler)
        => new(new SecretText("RGAPI-test"), new HttpClient(handler));

    [Fact]
    public async Task A_key_that_is_refused_from_the_very_first_call_is_reported_as_a_key_problem()
    {
        // An expired key never succeeds at anything, so the first refusal is the right moment to say so.
        var handler = new ScriptedHandler(HttpStatusCode.Forbidden);
        using var client = Client(handler);

        var thrown = await Assert.ThrowsAsync<RiotApiKeyRejectedException>(
            () => client.RefreshAsync(Account(), DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Equal(403, thrown.Status);
        Assert.Contains("403", thrown.Message);
    }

    [Fact]
    public async Task A_refusal_after_a_successful_call_is_not_blamed_on_the_key()
    {
        // The bug in miniature: the account lookup works, proving the key is fine, and then some
        // later endpoint refuses. Blaming the key there is simply wrong.
        var handler = new ScriptedHandler(
            HttpStatusCode.OK,          // account-v1 by-riot-id (resolve)
            HttpStatusCode.OK,          // account-v1 by-puuid
            HttpStatusCode.OK,          // summoner-v4
            HttpStatusCode.Forbidden);  // league-v4

        using var client = Client(handler);

        var outcome = await client.RefreshAsync(Account(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(outcome.Success);   // ranks are simply missing, not a failure of the whole refresh
    }

    [Fact]
    public async Task A_401_is_always_a_key_problem_even_after_a_success()
    {
        // Unlike 403, a 401 means the credential was not accepted at all — never an endpoint quirk.
        var handler = new ScriptedHandler(HttpStatusCode.OK, HttpStatusCode.Unauthorized);   // resolve, then refused
        using var client = Client(handler);

        var thrown = await Assert.ThrowsAsync<RiotApiKeyRejectedException>(
            () => client.RefreshAsync(Account(), DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Equal(401, thrown.Status);
    }

    [Fact]
    public async Task Ranks_are_never_looked_up_by_summoner_id()
    {
        // The endpoint requires an encrypted id; the one we hold is a plain number from the League
        // client, and summoner-v4 no longer returns an encrypted one at all. There is nothing to
        // fall back to, so the call must not be attempted.
        var handler = new ScriptedHandler();   // everything succeeds
        using var client = Client(handler);

        await client.RefreshAsync(Account(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.DoesNotContain(handler.Requested, path => path.Contains("by-summoner", StringComparison.Ordinal));
        Assert.Contains(handler.Requested, path => path.Contains("entries/by-puuid", StringComparison.Ordinal));

        // And the durable GUID is never what gets sent — the encrypted PUUID is.
        Assert.DoesNotContain(handler.Requested, path => path.Contains("11111111", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_rejection_message_shows_the_key_shape_without_the_key()
    {
        var handler = new ScriptedHandler(HttpStatusCode.Forbidden);
        using var client = new RiotApiClient(new SecretText("RGAPI-aaaabbbbccccdddd"), new HttpClient(handler));

        var thrown = await Assert.ThrowsAsync<RiotApiKeyRejectedException>(
            () => client.RefreshAsync(Account(), DateTimeOffset.UtcNow, CancellationToken.None));

        // Enough to spot a truncated paste; never enough to reconstruct the key.
        Assert.Contains("dddd", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("RGAPI-aaaabbbbccccdddd", thrown.Message, StringComparison.Ordinal);
    }
}
