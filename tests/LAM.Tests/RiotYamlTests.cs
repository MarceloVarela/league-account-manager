using LAM.Core.Riot;
using Xunit;

namespace LAM.Tests;

/// <summary>
/// The safety net around Riot's config files. Writing region into RiotClientSettings.yaml means
/// rewriting a file that holds a lot of state we do not own, so these tests are mostly about what
/// must survive being written through, not about what we change.
/// </summary>
public sealed class RiotYamlTests
{
    /// <summary>
    /// Shaped after the real file: nested mappings, quoted and plain scalars, nulls, integers,
    /// booleans, a sequence, and a key containing a colon — all of which the emitter has to preserve.
    /// </summary>
    private const string ClientSettingsFixture = """
        install:
            auto-update:
                league_of_legends.live: true
            background_running_notification_displayed: true
            cohorts:
                RC_15.new_lifecycle: "globalEnable"
                riot_client_default_groups: "global_100"
            first-install-timestamp: null
            globals:
                locale: "en_US"
                region: "BR"
            last-session-timestamp:
                league_of_legends.live: 1787352741
            localization:
                locale: "en_US"
                region: "BR"
            patch-notes:
                league_of_legends.live:
                    patchNote:
                    - "34d5874722646bd0d3d89cb714b060b3daccbe25bebbbc16153d275fbc276bd9"
            player-affinity:
                product:
                    bacon:
                        live: "americas"
                service:
                    chat: "br1"
            riotgamesapi:
                sampling:
                    dice_roll: 733115
                telemetry: null
        """;

    [Fact]
    public void Changing_region_preserves_every_unrelated_key()
    {
        var root = RiotYamlService.ParseMapping(ClientSettingsFixture)!;

        RiotYamlService.SetScalar(root, "NA", "install", "globals", "region");
        RiotYamlService.SetScalar(root, "en_GB", "install", "globals", "locale");

        var written = RiotYamlService.Serialise(root);
        var reparsed = RiotYamlService.ParseMapping(written)!;

        // What we meant to change.
        Assert.Equal("NA", RiotYamlService.GetScalar(reparsed, "install", "globals", "region"));
        Assert.Equal("en_GB", RiotYamlService.GetScalar(reparsed, "install", "globals", "locale"));

        // Everything else, including the deeply nested and oddly named keys.
        Assert.Equal("globalEnable",
            RiotYamlService.GetScalar(reparsed, "install", "cohorts", "RC_15.new_lifecycle"));
        Assert.Equal("1787352741",
            RiotYamlService.GetScalar(reparsed, "install", "last-session-timestamp", "league_of_legends.live"));
        Assert.Equal("true",
            RiotYamlService.GetScalar(reparsed, "install", "auto-update", "league_of_legends.live"));
        Assert.Equal("americas",
            RiotYamlService.GetScalar(reparsed, "install", "player-affinity", "product", "bacon", "live"));
        Assert.Equal("733115",
            RiotYamlService.GetScalar(reparsed, "install", "riotgamesapi", "sampling", "dice_roll"));

        // The sequence must still be a sequence, not flattened into a scalar.
        Assert.Contains("34d5874722646bd0d3d89cb714b060b3daccbe25bebbbc16153d275fbc276bd9", written);
    }

    [Fact]
    public void Round_tripping_without_changes_preserves_the_key_set()
    {
        var original = RiotYamlService.ParseMapping(ClientSettingsFixture)!;
        var reparsed = RiotYamlService.ParseMapping(RiotYamlService.Serialise(original))!;

        Assert.Equal(Keys(original).Count, Keys(reparsed).Count);
        Assert.Equal(Keys(original), Keys(reparsed));
    }

    private static List<string> Keys(YamlDotNet.RepresentationModel.YamlMappingNode node)
    {
        var results = new List<string>();

        void Walk(YamlDotNet.RepresentationModel.YamlNode current, string prefix)
        {
            if (current is not YamlDotNet.RepresentationModel.YamlMappingNode mapping) return;
            foreach (var pair in mapping.Children)
            {
                var key = prefix + "/" + ((YamlDotNet.RepresentationModel.YamlScalarNode)pair.Key).Value;
                results.Add(key);
                Walk(pair.Value, key);
            }
        }

        Walk(node, string.Empty);
        results.Sort(StringComparer.Ordinal);
        return results;
    }

    [Fact]
    public void SetScalar_creates_missing_intermediate_levels()
    {
        var root = new YamlDotNet.RepresentationModel.YamlMappingNode();
        RiotYamlService.SetScalar(root, "BR", "install", "globals", "region");

        Assert.Equal("BR", RiotYamlService.GetScalar(root, "install", "globals", "region"));
    }

    // ---- session detection -------------------------------------------------

    /// <summary>
    /// The signed-out shape observed on a real machine: a device cookie and nothing else. Reading
    /// this as a usable session is the single most damaging mistake the swap strategy could make,
    /// because it would launch an unauthenticated client and report success.
    /// </summary>
    private const string SignedOutPrivateSettings = """
        psl:
            authorization:
                riot-client: null
        riot-login:
            persist: null
        rso-authenticator:
            tdid:
                domain: "riotgames.com"
                expiryTime: 1818888888
                hostOnly: false
                httpOnly: true
                name: "tdid"
                path: "/"
                persistent: true
                secureOnly: true
                value: "a-device-identifier-not-a-session"
        """;

    private const string SignedInPrivateSettings = """
        psl:
            authorization:
                riot-client: null
        riot-login:
            persist: true
        rso-authenticator:
            ssid:
                name: "ssid"
                value: "the-actual-session-cookie"
            clid:
                name: "clid"
                value: "eu"
            tdid:
                name: "tdid"
                value: "a-device-identifier-not-a-session"
        """;

    [Fact]
    public void A_device_cookie_alone_is_not_a_session()
    {
        Assert.False(RiotYamlService.ContainsSession(SignedOutPrivateSettings));
        Assert.Equal(new[] { "tdid" }, RiotYamlService.DescribeCookies(SignedOutPrivateSettings));
    }

    [Fact]
    public void A_session_cookie_is_recognised()
    {
        Assert.True(RiotYamlService.ContainsSession(SignedInPrivateSettings));
        Assert.Contains("ssid", RiotYamlService.DescribeCookies(SignedInPrivateSettings));
    }

    [Fact]
    public void An_empty_or_broken_document_is_not_a_session()
    {
        Assert.False(RiotYamlService.ContainsSession(null));
        Assert.False(RiotYamlService.ContainsSession(""));
        Assert.False(RiotYamlService.ContainsSession("this: [is, not, the, right, shape"));
        Assert.False(RiotYamlService.ContainsSession("rso-authenticator:\n    ssid:\n        value: \"\""));
    }

    // ---- against the real machine, when there is one -----------------------

    /// <summary>
    /// Round-trips the actual RiotClientSettings.yaml on this machine, in memory only. Skipped on a
    /// box with no Riot install. A fixture can only prove the emitter handles the shapes I thought
    /// to write down; this proves it handles the file we are really going to overwrite.
    /// </summary>
    [Fact]
    public void The_real_client_settings_file_round_trips()
    {
        var paths = RiotPaths.Discover();
        var path = paths.ClientSettingsFile;
        if (!File.Exists(path)) return;   // no Riot install here; nothing to prove

        var original = RiotYamlService.LoadMapping(path);
        Assert.NotNull(original);

        var before = Keys(original!);
        var reparsed = RiotYamlService.ParseMapping(RiotYamlService.Serialise(original!));
        Assert.NotNull(reparsed);

        Assert.Equal(before, Keys(reparsed!));
    }
}

public sealed class LockfileTests
{
    [Fact]
    public void Parses_the_documented_format()
    {
        Assert.True(Lockfile.TryParse("Riot Client:12345:54321:s3cr3t:https", out var lockfile));

        Assert.Equal("Riot Client", lockfile!.Name);
        Assert.Equal(12345, lockfile.ProcessId);
        Assert.Equal(54321, lockfile.Port);
        Assert.Equal("s3cr3t", lockfile.Password);
        Assert.Equal("https", lockfile.Protocol);
        Assert.Equal(new Uri("https://127.0.0.1:54321"), lockfile.BaseAddress);
    }

    [Fact]
    public void Basic_auth_uses_the_literal_riot_user()
    {
        Lockfile.TryParse("x:1:2:pw:https", out var lockfile);
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(lockfile!.BasicAuthParameter));
        Assert.Equal("riot:pw", decoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too:few:fields")]
    [InlineData("a:b:c:d:e")]              // non-numeric pid and port
    [InlineData("x:1:notaport:pw:https")]
    [InlineData("x:1:99999:pw:https")]     // port out of range
    [InlineData("x:1:2::https")]           // empty password
    [InlineData("x:1:2:pw:ftp")]           // unexpected protocol
    public void Rejects_malformed_input(string? contents)
    {
        Assert.False(Lockfile.TryParse(contents, out var lockfile));
        Assert.Null(lockfile);
    }

    [Fact]
    public void Trailing_whitespace_is_tolerated()
    {
        Assert.True(Lockfile.TryParse("Riot Client:1:2:pw:https\r\n", out var lockfile));
        Assert.Equal(2, lockfile!.Port);
    }
}
