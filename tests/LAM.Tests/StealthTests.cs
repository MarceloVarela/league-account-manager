using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LAM.Core.Login;
using LAM.Core.Model;
using LAM.Core.Stealth;
using Xunit;

namespace LAM.Tests;

/// <summary>
/// Guards the presence rewriter.
///
/// Every failure here is silent — a stanza that is not rewritten simply goes out as-is, telling the
/// friends list exactly what the user was trying to hide. So these care as much about what must NOT
/// be touched as about what must.
/// </summary>
public class PresenceRewriterTests
{
    private const string Presence =
        "<presence id='p1'>"
        + "<show>chat</show>"
        + "<status>{\"championId\":86,\"gameStatus\":\"inGame\"}</status>"
        + "<games>"
        + "<league_of_legends><st>chat</st><p>eyJ0aWVyIjoiRElBTU9ORCJ9</p><m>1</m></league_of_legends>"
        + "<valorant><st>chat</st></valorant>"
        + "</games>"
        + "</presence>";

    [Fact]
    public void Offline_says_offline_and_drops_every_trace_of_the_game()
    {
        var result = PresenceRewriter.Rewrite(Presence, StealthMode.Offline).Replacement;

        Assert.NotNull(result);
        Assert.Contains("<show>offline</show>", result);

        // The rich-presence blob carries the champion and the game state.
        Assert.DoesNotContain("gameStatus", result);
        Assert.DoesNotContain("championId", result);
        Assert.DoesNotContain("league_of_legends", result);
        Assert.DoesNotContain("valorant", result);
    }

    [Fact]
    public void Mobile_keeps_the_league_node_but_none_of_its_payload()
    {
        var result = PresenceRewriter.Rewrite(Presence, StealthMode.Mobile).Replacement;

        Assert.NotNull(result);
        Assert.Contains("<show>mobile</show>", result);

        // The node itself is what draws the mobile icon, so it stays...
        Assert.Contains("league_of_legends", result);
        Assert.Contains("<st>mobile</st>", result);

        // ...but the payload inside it is the game detail, and must not.
        Assert.DoesNotContain("eyJ0aWVyIjoiRElBTU9ORCJ9", result);
        Assert.DoesNotContain("<m>", result);
        Assert.DoesNotContain("gameStatus", result);
    }

    [Fact]
    public void Online_changes_nothing_at_all()
    {
        // Not "rewrites it back to chat" — it forwards, so the caller sends the original bytes.
        Assert.Equal(PresenceOutcome.Forward, PresenceRewriter.Rewrite(Presence, StealthMode.Online));
    }

    [Fact]
    public void Directed_presence_is_left_alone()
    {
        // A stanza addressed at a room is how the client joins lobby and champion-select chat.
        // Rewriting it breaks both, and the friends list never reads it.
        const string directed =
            "<presence to='bec9a1f6@lol-champ-select.eu1.pvp.net' id='p2'><show>chat</show></presence>";

        Assert.Equal(PresenceOutcome.Forward, PresenceRewriter.Rewrite(directed, StealthMode.Offline));
    }

    [Fact]
    public void A_partial_stanza_is_never_guessed_at()
    {
        // The failure mode that matters: half a stanza must be forwarded untouched, not "fixed up".
        Assert.Equal(PresenceOutcome.Forward,
            PresenceRewriter.Rewrite("<presence id='p1'><show>ch", StealthMode.Offline));
    }

    [Fact]
    public void Turning_lobby_chat_off_drops_directed_presence_rather_than_rewriting_it()
    {
        // Dropped, not rewritten: the room simply never hears the announcement. Rewriting it would
        // put a fake status into the lobby, which is a different and worse thing than silence.
        const string directed =
            "<presence to='bec9a1f6@lol-champ-select.eu1.pvp.net' id='p2'><show>chat</show></presence>";

        var outcome = PresenceRewriter.Rewrite(directed, StealthMode.Offline, lobbyChat: false);

        Assert.True(outcome.Drop);
        Assert.Null(outcome.Replacement);
    }

    [Fact]
    public void Lobby_chat_off_still_leaves_ordinary_presence_rewritten_not_dropped()
    {
        // The setting is about directed presence only. Dropping the friends-list presence too would
        // leave the user's status frozen at whatever it last was, which looks like the feature
        // working right up until someone checks.
        var outcome = PresenceRewriter.Rewrite(Presence, StealthMode.Offline, lobbyChat: false);

        Assert.False(outcome.Drop);
        Assert.Contains("<show>offline</show>", outcome.Replacement);
    }

    [Fact]
    public void Anything_that_is_not_a_presence_is_left_alone()
    {
        Assert.Equal(PresenceOutcome.Forward,
            PresenceRewriter.Rewrite("<message to='x'><body>hi</body></message>", StealthMode.Offline));
        Assert.Equal(PresenceOutcome.Forward,
            PresenceRewriter.Rewrite("<iq type='result' id='1'/>", StealthMode.Offline));
    }
}

/// <summary>
/// Guards the stanza framing.
///
/// The tool this feature is modelled on has no framing at all — it greps a fixed read buffer for
/// "&lt;presence". These tests exist because that works right up until a stanza straddles two TCP
/// reads, at which point the user's real presence goes out and nothing says so.
/// </summary>
public class StanzaSplitterTests
{
    private static IReadOnlyList<string> Feed(StanzaSplitter splitter, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return splitter.Push(bytes, bytes.Length);
    }

    [Fact]
    public void A_whole_stanza_arrives_whole()
    {
        var splitter = new StanzaSplitter();

        var stanzas = Feed(splitter, "<presence id='1'><show>chat</show></presence>");

        Assert.Single(stanzas);
        Assert.Equal("<presence id='1'><show>chat</show></presence>", stanzas[0]);
    }

    [Fact]
    public void A_stanza_split_across_two_reads_is_held_until_it_is_complete()
    {
        // THE case. Deceive would have forwarded the first half unrewritten.
        var splitter = new StanzaSplitter();

        Assert.Empty(Feed(splitter, "<presence id='1'><show>ch"));

        var stanzas = Feed(splitter, "at</show></presence>");

        Assert.Single(stanzas);
        Assert.Equal("<presence id='1'><show>chat</show></presence>", stanzas[0]);
    }

    [Fact]
    public void A_tag_split_mid_attribute_is_held()
    {
        var splitter = new StanzaSplitter();

        Assert.Empty(Feed(splitter, "<presence id='1"));
        Assert.Single(Feed(splitter, "'><show>chat</show></presence>"));
    }

    [Fact]
    public void A_multi_byte_character_split_across_reads_survives()
    {
        // A UTF-8 continuation byte landing on the read boundary. Decoding each read independently
        // turns the name into replacement characters and corrupts the stanza on the wire.
        var splitter = new StanzaSplitter();
        var whole = Encoding.UTF8.GetBytes("<presence id='1'><status>Ünïcødé</status></presence>");

        // 26 exactly, and it matters: "<presence id='1'><status>" is 25 bytes, so byte 26 lands
        // between the two bytes of "U-umlaut". Splitting at 20 or 25 falls cleanly between characters
        // and proves nothing — the first version of this test did, and passed against a decoder that
        // could not have worked.
        const int half = 26;
        Assert.Empty(splitter.Push(whole[..half], half));

        var rest = whole[half..];
        var stanzas = splitter.Push(rest, rest.Length);

        Assert.Single(stanzas);
        Assert.Contains("Ünïcødé", stanzas[0]);
    }

    [Fact]
    public void Several_stanzas_in_one_read_all_come_out()
    {
        var splitter = new StanzaSplitter();

        var stanzas = Feed(splitter,
            "<presence id='1'/><message to='x'><body>hi</body></message><iq id='2'/>");

        Assert.Equal(3, stanzas.Count);
        Assert.Equal("<presence id='1'/>", stanzas[0]);
        Assert.Equal("<iq id='2'/>", stanzas[2]);
    }

    [Fact]
    public void Nested_elements_do_not_end_the_stanza_early()
    {
        var splitter = new StanzaSplitter();

        var stanzas = Feed(splitter,
            "<presence id='1'><games><league_of_legends><st>chat</st></league_of_legends></games></presence>");

        Assert.Single(stanzas);
        Assert.EndsWith("</presence>", stanzas[0]);
    }

    [Fact]
    public void The_stream_header_is_emitted_rather_than_waited_on()
    {
        // <stream:stream> closes only when the session ends, so buffering until its close tag would
        // stall the connection forever before authentication even starts.
        var splitter = new StanzaSplitter();

        var stanzas = Feed(splitter,
            "<?xml version='1.0'?><stream:stream to='eu1.pvp.net' xmlns:stream='x'><presence id='1'/>");

        Assert.Equal(3, stanzas.Count);
        Assert.Equal("<presence id='1'/>", stanzas[2]);
    }
}

/// <summary>
/// Guards the config rewrite — the redirection half of stealth.
///
/// The failure that matters most is silent in the same way the rewriter's is: if the real chat host
/// is read out wrong, or read AFTER being overwritten, the proxy has nowhere to relay to and the user
/// simply loses chat with no explanation.
/// </summary>
public class ClientConfigPatchTests
{
    private const string Config = """
    {
      "chat.host": "euw1.chat.si.riotgames.com",
      "chat.port": 5223,
      "chat.affinity.enabled": true,
      "chat.affinities": {
        "euw1": "euw1.chat.si.riotgames.com",
        "na1": "na1.chat.si.riotgames.com",
        "br1": "br1.chat.si.riotgames.com"
      },
      "keystone.something.unrelated": "left alone"
    }
    """;

    [Fact]
    public void The_real_host_is_read_before_it_is_overwritten()
    {
        // The whole point. Overwrite first and there is nothing left to relay to.
        var result = ClientConfigPatch.Patch(Config, "br1", "localhost.example.com", 41234);

        Assert.NotNull(result);
        Assert.Equal("br1.chat.si.riotgames.com", result!.Value.Upstream!.Host);
        Assert.Equal(5223, result.Value.Upstream.Port);
    }

    [Fact]
    public void Every_region_in_the_map_is_pointed_at_us_not_just_ours()
    {
        // One surviving real host is one connection that never reaches the proxy.
        var result = ClientConfigPatch.Patch(Config, "br1", "localhost.example.com", 41234);

        var patched = JsonNode.Parse(result!.Value.Json)!.AsObject();
        var affinities = patched["chat.affinities"]!.AsObject();

        Assert.All(affinities, pair =>
            Assert.Equal("localhost.example.com", pair.Value!.GetValue<string>()));

        Assert.Equal("localhost.example.com", patched["chat.host"]!.GetValue<string>());
        Assert.Equal(41234, patched["chat.port"]!.GetValue<int>());
    }

    [Fact]
    public void Unrelated_keys_survive_untouched()
    {
        var result = ClientConfigPatch.Patch(Config, "br1", "localhost.example.com", 41234);

        var patched = JsonNode.Parse(result!.Value.Json)!.AsObject();

        Assert.Equal("left alone", patched["keystone.something.unrelated"]!.GetValue<string>());
    }

    [Fact]
    public void With_affinities_disabled_the_plain_host_is_authoritative()
    {
        // The client picks its host differently depending on this flag, so reading the wrong one
        // sends the relay to a server the client was never going to use.
        const string flat = """
        {
          "chat.host": "sea.chat.si.riotgames.com",
          "chat.port": 5223,
          "chat.affinity.enabled": false,
          "chat.affinities": { "euw1": "euw1.chat.si.riotgames.com" }
        }
        """;

        var result = ClientConfigPatch.Patch(flat, "euw1", "localhost.example.com", 1);

        Assert.Equal("sea.chat.si.riotgames.com", result!.Value.Upstream!.Host);
    }

    [Fact]
    public void An_unknown_affinity_falls_back_to_the_plain_host()
    {
        var result = ClientConfigPatch.Patch(Config, "unknown9", "localhost.example.com", 1);

        Assert.Equal("euw1.chat.si.riotgames.com", result!.Value.Upstream!.Host);
    }

    [Fact]
    public void A_response_with_no_chat_keys_is_not_ours_to_touch()
    {
        // Most requests through the proxy are something else entirely and must pass straight through.
        Assert.Null(ClientConfigPatch.Patch("""{"some.other.key": 1}""", "euw1", "x", 1));
        Assert.Null(ClientConfigPatch.Patch("not json at all", "euw1", "x", 1));
        Assert.Null(ClientConfigPatch.Patch("[]", "euw1", "x", 1));
    }

    [Fact]
    public void The_affinity_claim_is_read_out_of_the_pas_token()
    {
        var payload = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("""{"affinity":"br1","sub":"abc"}"""))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Equal("br1", ClientConfigPatch.ReadAffinity("header." + payload + ".signature"));
    }

    [Fact]
    public void A_token_that_is_not_a_token_yields_nothing_rather_than_throwing()
    {
        // The PAS call is best-effort; a failure here must degrade to the plain host, not crash a
        // sign-in.
        Assert.Null(ClientConfigPatch.ReadAffinity(null));
        Assert.Null(ClientConfigPatch.ReadAffinity(""));
        Assert.Null(ClientConfigPatch.ReadAffinity("not-a-jwt"));
        Assert.Null(ClientConfigPatch.ReadAffinity("a.!!!not-base64!!!.c"));
    }
}

/// <summary>
/// Guards the two promises stealth makes beyond appearing offline.
///
/// Both are invisible when broken, which is why they are pinned here rather than left to review: a
/// sign-in that fails because of an optional feature looks like a broken app, and a token in a log
/// file looks like nothing at all until someone reads it.
/// </summary>
public class StealthSafetyTests
{
    [Fact]
    public async Task Stealth_that_cannot_start_does_not_fail_the_sign_in()
    {
        // Fail-open is the whole contract. Once the client has been handed a rewritten config it no
        // longer knows where chat lives, so "half on" is not a state that can exist — either it is
        // fully up before launch, or the client launches untouched.
        var settings = new AppSettings { StealthLogin = true };

        var context = new LoginContext
        {
            Account = new AccountEntry { Label = "main" },
            Settings = settings,
            Progress = new Progress<LoginProgress>(_ => { }),

            // Exactly what StealthSession returns when DNS, the certificate or a port is not right.
            BeginStealth = _ => Task.FromResult(string.Empty),
        };

        var argument = await context.BeginStealth!(CancellationToken.None);

        // An empty argument means the launcher is invoked exactly as it always was.
        Assert.Equal(string.Empty, argument);
    }

    [Fact]
    public void The_hostname_and_certificate_url_agree_with_each_other()
    {
        // The certificate is issued FOR the hostname, so the two are a pair. Changing one without
        // the other produces a certificate the client rejects, and the only symptom is an empty
        // friends list.
        Assert.True(StealthEndpoints.Configured);

        // Never a wildcard: the private key ships with the app, so it must only ever be able to
        // vouch for one name, and that name must resolve to the user's own machine.
        Assert.DoesNotContain("*", StealthEndpoints.Host);
        Assert.StartsWith("localhost.", StealthEndpoints.Host);

        // The refresh URL has to be fetchable over TLS, or a renewal cannot reach anyone.
        Assert.StartsWith("https://", StealthEndpoints.CertificateUrl);
        Assert.EndsWith(".pfx", StealthEndpoints.CertificateUrl);
    }

    [Fact]
    public void The_launch_argument_carries_nothing_but_a_port()
    {
        // The launcher builds its command line by string concatenation under ShellExecute, which is
        // only safe while nothing user-supplied reaches it. If this ever has to carry a path, it
        // needs quoting first.
        var options = new StealthOptions("localhost.example", "https://example.invalid/x.pfx", "c:/tmp/x.pfx", null);

        Assert.DoesNotContain(" ", options.Host);
        Assert.DoesNotContain("\"", options.Host);
    }

    [Fact]
    public void No_stealth_code_writes_a_stanza_or_a_header_to_the_trace()
    {
        // The proxy carries the account's authentication token in plain text. LoginTrace has no
        // redaction of its own — the rule is call-site discipline — so this asserts the discipline
        // held, by reading the source rather than trusting review.
        var directory = Path.Combine(Root(), "src", "LAM.Core", "Stealth");

        var offenders = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.GetFiles(directory, "*.cs"))
        {
            foreach (var line in File.ReadAllLines(file))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                if (!trimmed.Contains("_trace", StringComparison.Ordinal)) continue;

                // Anything that could carry a payload into a log file.
                foreach (var banned in new[] { "stanza", "body", "content", "authorization", "token", "Json" })
                {
                    if (line.Contains(banned, StringComparison.OrdinalIgnoreCase))
                        offenders.Add(Path.GetFileName(file) + ": " + trimmed);
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These trace calls could write account credentials into an unencrypted log file: "
            + string.Join(" | ", offenders));
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LeagueAccountManager.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the repository root.");
    }
}

/// <summary>
/// Guards the shipped certificate.
///
/// It expires roughly every ninety days, and when it does stealth stops working with no error the
/// user can see — the friends list simply shows them online. A failing test is a far better way to
/// find that out than a bug report.
/// </summary>
public class StealthCertificateTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LeagueAccountManager.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the repository root.");
    }

    /// <summary>
    /// The shipped certificate.
    ///
    /// Unconditional: every caller is a <see cref="CertificateFactAttribute"/>, which has already
    /// skipped the test if the file is absent. So reaching here without one is a real failure, not
    /// a checkout without the certificate.
    /// </summary>
    private static byte[] Shipped() => File.ReadAllBytes(StealthCertificatePath.Value);

    [CertificateFact]
    public void The_shipped_certificate_is_for_the_configured_host()
    {
        // The hostname and the certificate are a pair: the client asks for one name and checks the
        // certificate presented matches it. Changing StealthEndpoints.Host without reissuing gives a
        // certificate the client rejects, and the only symptom is an empty friends list.
        var certificates = X509CertificateLoader.LoadPkcs12Collection(
            Shipped(), password: null, X509KeyStorageFlags.DefaultKeySet);

        var leaf = certificates.FirstOrDefault(c => c.HasPrivateKey);
        Assert.NotNull(leaf);

        var names = leaf!.Extensions
            .OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(e => e.EnumerateDnsNames())
            .ToList();

        Assert.Contains(StealthEndpoints.Host, names);

        // A wildcard would let this key vouch for names that do NOT resolve to the user's machine,
        // and the key ships with the app where anyone can extract it.
        Assert.DoesNotContain(names, n => n.StartsWith("*", StringComparison.Ordinal));
    }

    [CertificateFact]
    public void The_shipped_certificate_has_not_expired()
    {
        var certificates = X509CertificateLoader.LoadPkcs12Collection(
            Shipped(), password: null, X509KeyStorageFlags.DefaultKeySet);

        var leaf = certificates.First(c => c.HasPrivateKey);

        Assert.True(leaf.NotAfter > DateTime.Now,
            "The shipped stealth certificate expired on " + leaf.NotAfter.ToString("d MMM yyyy")
            + ". Reissue it, replace src/LAM.App/Assets/stealth.pfx, and publish the same file at "
            + StealthEndpoints.CertificateUrl);

        // Warn well before the cliff. The app refreshes from the URL inside 20 days, but a build
        // shipped with an already-stale certificate leaves anyone offline without stealth.
        Assert.True(leaf.NotAfter > DateTime.Now.AddDays(20),
            "The shipped stealth certificate expires on " + leaf.NotAfter.ToString("d MMM yyyy")
            + ", which is inside the refresh window. Reissue it before shipping this build.");
    }

    [CertificateFact]
    public void The_chain_ships_with_it()
    {
        // Only the leaf would leave the client to build the path itself, which is slower and fails
        // outright on a machine that cannot fetch the issuer.
        var certificates = X509CertificateLoader.LoadPkcs12Collection(
            Shipped(), password: null, X509KeyStorageFlags.DefaultKeySet);

        Assert.True(certificates.Count > 1,
            "Only the leaf certificate is present. Re-export with -certfile ca.cer so the "
            + "intermediate travels with it.");
    }
}

/// <summary>
/// Guards the fake friend.
///
/// The roster it edits is the user's REAL friends list, and the reference implementation's most
/// reported failure is not a missing fake entry — it is the whole friends list disappearing. So most
/// of these are about what must survive untouched, not about what gets added.
/// </summary>
public class FakeFriendTests
{
    private const string Roster =
        "<iq type='result' id='2'><query xmlns='jabber:iq:riotgames:roster'>"
        + "<item jid='real-one@eu1.pvp.net' name='Zoé &amp; Friends' subscription='both'>"
        + "<group priority='1'>Duo</group></item>"
        + "<item jid='real-two@eu1.pvp.net' name='ADC Diff' subscription='both'/>"
        + "</query></iq>";

    [Fact]
    public void The_friend_is_added_as_the_first_entry()
    {
        var injected = new FakeFriend().Inject(Roster);

        Assert.NotNull(injected);

        var ours = injected!.IndexOf("Hextech Manager", StringComparison.Ordinal);
        var theirs = injected.IndexOf("real-one@eu1.pvp.net", StringComparison.Ordinal);

        Assert.True(ours > 0 && ours < theirs, "The fake friend must be inserted before real ones.");
    }

    [Fact]
    public void Every_real_friend_survives_byte_for_byte()
    {
        // THE test. Splicing rather than parsing means the rest of the document is carried across
        // untouched — including a name with an ampersand and a non-ASCII character, which is exactly
        // what a parse-and-re-serialise would quietly mangle.
        var injected = new FakeFriend().Inject(Roster)!;

        Assert.Contains("<item jid='real-one@eu1.pvp.net' name='Zoé &amp; Friends' subscription='both'>"
                        + "<group priority='1'>Duo</group></item>", injected);
        Assert.Contains("<item jid='real-two@eu1.pvp.net' name='ADC Diff' subscription='both'/>", injected);
        Assert.EndsWith("</query></iq>", injected);
    }

    [Fact]
    public void The_result_is_still_well_formed_xml()
    {
        // A malformed roster does not degrade the feature; it breaks the client's XML stream.
        var injected = new FakeFriend().Inject(Roster)!;

        var document = XDocument.Parse(injected);

        // By local name: <query> declares a namespace, so its children are not in the default one
        // and Descendants("item") would find nothing and prove nothing.
        Assert.Equal(3, document.Descendants().Count(e => e.Name.LocalName == "item"));
    }

    [Fact]
    public void It_is_added_once_and_not_again()
    {
        var friend = new FakeFriend();

        Assert.NotNull(friend.Inject(Roster));
        Assert.Null(friend.Inject(Roster));
    }

    [Fact]
    public void Anything_that_is_not_a_roster_is_left_alone()
    {
        var friend = new FakeFriend();

        Assert.Null(friend.Inject("<presence id='1'><show>chat</show></presence>"));
        Assert.Null(friend.Inject("<iq type='result'><query xmlns='jabber:iq:something:else'/></iq>"));
        Assert.False(friend.IsPresent);
    }

    [Fact]
    public void Presence_and_messages_are_well_formed()
    {
        var friend = new FakeFriend();
        friend.Inject(Roster);

        XDocument.Parse(friend.Presence());
        XDocument.Parse(friend.Say("hello"));
    }

    [Fact]
    public void Message_text_is_escaped()
    {
        // An unescaped ampersand here is not a wrong message — it is malformed XML, and the client
        // drops the entire connection. The reference implementation interpolates raw.
        var body = new FakeFriend().Say("you & <them>");

        XDocument.Parse(body);
        Assert.DoesNotContain("& <them>", body);
    }

    [Theory]
    [InlineData("offline", StealthCommand.Offline)]
    [InlineData("  Offline  ", StealthCommand.Offline)]
    [InlineData("MOBILE", StealthCommand.Mobile)]
    [InlineData("online", StealthCommand.Online)]
    [InlineData("status", StealthCommand.Status)]
    [InlineData("help", StealthCommand.Help)]
    public void Commands_are_read_from_the_body(string text, StealthCommand expected)
    {
        var stanza = "<message to='x' type='chat'><body>" + text + "</body></message>";

        Assert.Equal(expected, FakeFriend.Read(stanza));
    }

    [Theory]
    // A typing indicator. The client sends these constantly while a chat window is open.
    [InlineData("<message to='x' type='chat'><composing xmlns='http://jabber.org/protocol/chatstates'/></message>")]
    // Sent alongside every real message, which is why a reply arrived after each command too.
    [InlineData("<message to='x' type='chat'><active xmlns='http://jabber.org/protocol/chatstates'/></message>")]
    [InlineData("<message to='x' type='chat'><body></body></message>")]
    [InlineData("<message to='x' type='chat'><body>   </body></message>")]
    [InlineData("<iq type='set' to='x'><query xmlns='jabber:iq:riotgames:roster'/></iq>")]
    public void Anything_without_typed_text_gets_no_reply_at_all(string stanza)
    {
        // These arrive unprompted. Treating them as failed commands meant the friend answered
        // "I did not understand that" to things nobody said — repeatedly, before a word was typed.
        // Silence is the only correct response; they still must not reach Riot.
        Assert.Equal(StealthCommand.None, FakeFriend.Read(stanza));
    }

    [Fact]
    public void A_sentence_containing_a_command_word_is_not_a_command()
    {
        // The reference implementation substring-matches the whole raw buffer, so "I'm going offline
        // brb" silently flips your status. Reading the body and requiring the whole of it removes an
        // entire class of accident.
        var stanza = "<message to='x' type='chat'><body>im going offline brb</body></message>";

        Assert.Equal(StealthCommand.Unknown, FakeFriend.Read(stanza));
    }

    [Fact]
    public void A_stanza_addressed_to_the_friend_is_recognised_whatever_it_is()
    {
        // Not just chat: typing indicators, roster edits and subscription changes are all aimed at a
        // JID Riot has never heard of, and every one of them has to be swallowed.
        var jid = new FakeFriend().Presence();
        var id = jid.Split('\'')[1].Split('/')[0];

        Assert.True(FakeFriend.IsForUs("<message to='" + id + "'><body>x</body></message>"));
        Assert.True(FakeFriend.IsForUs("<iq type='set' to='" + id + "'/>"));
        Assert.False(FakeFriend.IsForUs("<message to='someone@eu1.pvp.net'><body>x</body></message>"));
    }
}

/// <summary>
/// Guards the introduction.
///
/// It is sent when the fake friend is added to the roster, and the client rebuilds its roster on
/// every chat reconnect — so a greeting scoped to the connection arrives over and over for something
/// the user already knows. Once, ever.
/// </summary>
public class StealthGreetingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lam-greeting", Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_first_run_greets_and_no_run_after_it_does()
    {
        Assert.True(StealthGreeting.ClaimFirstRun(_root));

        // Every subsequent connection, reconnect, client restart and app restart.
        Assert.False(StealthGreeting.ClaimFirstRun(_root));
        Assert.False(StealthGreeting.ClaimFirstRun(_root));
        Assert.False(StealthGreeting.ClaimFirstRun(_root));
    }

    [Fact]
    public void Deleting_the_marker_brings_it_back()
    {
        // The obvious way to see the introduction again, and the reason it is a file rather than a
        // flag in the encrypted vault.
        StealthGreeting.ClaimFirstRun(_root);

        File.Delete(Path.Combine(_root, "stealth-greeted"));

        Assert.True(StealthGreeting.ClaimFirstRun(_root));
    }

    [Fact]
    public void An_unwritable_location_stays_quiet_rather_than_greeting_every_time()
    {
        // If the marker cannot be written, greeting would repeat forever — which is the bug being
        // fixed. Silence is the better failure.
        Assert.False(StealthGreeting.ClaimFirstRun("\0:/nope"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
