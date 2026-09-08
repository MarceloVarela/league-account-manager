using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LAM.Core.Stealth;

/// <summary>Where the genuine chat server lives, learnt from the client's own configuration.</summary>
public sealed record ChatServer(string Host, int Port);

/// <summary>
/// Rewrites Riot's client configuration so the League client connects to our proxy instead of Riot's
/// chat server.
///
/// This is the redirection half of the feature. Riot serves the client a large flat JSON document of
/// dotted keys; three of them say where chat lives, and changing those is all it takes — the client
/// simply believes what it is told. Nothing here is a protocol exploit: it is the client's own
/// configuration mechanism, pointed somewhere else.
///
/// Pure and static so the parsing can be tested against captured payloads without a listening socket.
/// </summary>
public static class ClientConfigPatch
{
    private const string HostKey = "chat.host";
    private const string PortKey = "chat.port";
    private const string AffinitiesKey = "chat.affinities";
    private const string AffinityEnabledKey = "chat.affinity.enabled";

    /// <summary>
    /// Reads where chat really is, then points it at us.
    ///
    /// Order matters: the genuine host has to be read out BEFORE it is overwritten, because it is the
    /// only place we learn where to relay to. Which of the two sources is authoritative depends on
    /// <c>chat.affinity.enabled</c> — with affinities on, the client picks a host out of the map by
    /// its region and ignores <c>chat.host</c> entirely.
    /// </summary>
    /// <param name="json">The configuration document exactly as Riot served it.</param>
    /// <param name="affinity">The player's region, from the PAS token. Null when it could not be read.</param>
    /// <param name="localHost">The hostname our certificate is valid for.</param>
    /// <param name="localPort">Our TLS listener.</param>
    /// <returns>The rewritten document, and the real chat server — or null if there was nothing to patch.</returns>
    public static (string Json, ChatServer? Upstream)? Patch(
        string json, string? affinity, string localHost, int localPort)
    {
        JsonNode? root;

        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is not JsonObject config) return null;

        // Not the config response we care about — most requests through the proxy are something else
        // entirely, and they must pass through untouched.
        if (!config.ContainsKey(HostKey) && !config.ContainsKey(AffinitiesKey)) return null;

        var upstream = ReadUpstream(config, affinity);

        if (config.ContainsKey(HostKey)) config[HostKey] = localHost;
        if (config.ContainsKey(PortKey)) config[PortKey] = localPort;

        // Every region has to point at us, not just the player's own: the client may consult the map
        // by a key we did not predict, and one surviving real host means one un-proxied connection.
        if (config[AffinitiesKey] is JsonObject affinities)
        {
            foreach (var region in affinities.Select(pair => pair.Key).ToList())
            {
                affinities[region] = localHost;
            }
        }

        return (config.ToJsonString(), upstream);
    }

    private static ChatServer? ReadUpstream(JsonObject config, string? affinity)
    {
        var port = config[PortKey]?.GetValue<int>() ?? 5223;

        var affinityEnabled = config[AffinityEnabledKey] switch
        {
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            _ => false,
        };

        if (affinityEnabled
            && affinity is not null
            && config[AffinitiesKey] is JsonObject affinities
            && affinities[affinity]?.GetValue<string>() is { Length: > 0 } regional)
        {
            return new ChatServer(regional, port);
        }

        return config[HostKey]?.GetValue<string>() is { Length: > 0 } host
            ? new ChatServer(host, port)
            : null;
    }

    /// <summary>
    /// Pulls the region out of the PAS token.
    ///
    /// The token is a JWT, and we read one claim from its payload without verifying the signature —
    /// deliberately. We are not authenticating anything with it; it is Riot's own answer to Riot's own
    /// question, relayed through us, and a forged one could only misdirect the user's own chat.
    /// </summary>
    public static string? ReadAffinity(string? pasToken)
    {
        if (string.IsNullOrWhiteSpace(pasToken)) return null;

        var parts = pasToken.Split('.');
        if (parts.Length < 2) return null;

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));

            return document.RootElement.TryGetProperty("affinity", out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or DecoderFallbackException)
        {
            return null;
        }
    }
}
