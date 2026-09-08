using System.Xml.Linq;

namespace LAM.Core.Stealth;

/// <summary>How the account should appear to its friends list.</summary>
public enum StealthMode
{
    /// <summary>Normal. Presence passes through untouched.</summary>
    Online,

    /// <summary>Shown as offline while the session stays fully connected.</summary>
    Offline,

    /// <summary>Shown on the mobile/companion icon, with no game detail.</summary>
    Mobile,
}

/// <summary>
/// Rewrites the XMPP presence the client sends, so the friends list is told something other than
/// what the client meant to say.
///
/// This is the whole feature. The session stays genuinely authenticated and connected — nothing here
/// touches authentication, which passes through untouched — and only the presence stanza is edited on
/// its way out. Riot's own client renders <c>show=offline</c> as offline, so telling it that is
/// enough; there is no need to drop the connection or fake anything about the account.
///
/// Pure and static so it can be tested against captured stanzas without a socket, which matters:
/// every failure mode here is silent. A stanza this refuses to handle is forwarded unchanged, which
/// means the real presence goes out — so the tests care as much about what is NOT rewritten.
/// </summary>
public static class PresenceRewriter
{
    /// <summary>The game nodes stripped alongside League, so nothing else leaks a live session.</summary>
    private static readonly string[] OtherGames =
        ["valorant", "bacon", "lion", "keystone", "teamfighttactics", "riot_client"];

    /// <summary>
    /// Decides what to do with one presence stanza.
    ///
    /// Three answers, and the default is the conservative one: <see cref="PresenceOutcome.Forward"/>
    /// means "not ours to change", and the caller then forwards the ORIGINAL bytes rather than a
    /// re-serialised copy — so anything this does not understand crosses the proxy byte-for-byte.
    /// </summary>
    /// <param name="lobbyChat">
    /// Whether presence addressed at a room may pass. That presence is what makes lobby and
    /// champion-select chat work; it can also carry a live status into a lobby while the friends list
    /// is being told otherwise. Letting it through is the default because chat silently breaking is
    /// the more confusing of the two failures.
    /// </param>
    public static PresenceOutcome Rewrite(string stanza, StealthMode mode, bool lobbyChat = true)
    {
        if (mode == StealthMode.Online) return PresenceOutcome.Forward;
        if (string.IsNullOrWhiteSpace(stanza)) return PresenceOutcome.Forward;

        XElement presence;

        try
        {
            presence = XElement.Parse(stanza, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            // Not a whole, well-formed stanza. Forwarding it unchanged is the only safe answer:
            // guessing at a partial stanza is how a rewriter leaks the presence it meant to hide.
            return PresenceOutcome.Forward;
        }

        if (presence.Name.LocalName != "presence") return PresenceOutcome.Forward;

        // Directed presence — a stanza addressed at a specific room or user — is how the client joins
        // lobby and champion-select chat, and it is not what the friends list reads. Rewriting it is
        // never right: either it passes untouched, or it is dropped entirely.
        if (presence.Attribute("to") is not null)
            return lobbyChat ? PresenceOutcome.Forward : PresenceOutcome.Dropped;

        var target = mode == StealthMode.Mobile ? "mobile" : "offline";

        presence.Element("show")?.ReplaceNodes(target);

        var league = presence.Element("games")?.Element("league_of_legends");
        league?.Element("st")?.ReplaceNodes(target);

        // The rich-presence blob: queue, tier, champion, game state. It is the detail the mode is
        // meant to hide, and it is simpler and safer to drop it wholesale than to edit its contents.
        presence.Element("status")?.Remove();

        var games = presence.Element("games");

        if (games is not null)
        {
            foreach (var name in OtherGames) games.Element(name)?.Remove();
        }

        if (mode == StealthMode.Mobile)
        {
            // Keep the League node so the mobile icon shows, but strip the payload inside it.
            league?.Element("p")?.Remove();
            league?.Element("m")?.Remove();
        }
        else
        {
            league?.Remove();
        }

        return PresenceOutcome.Replaced(presence.ToString(SaveOptions.DisableFormatting));
    }
}

/// <summary>
/// What the proxy should do with a stanza.
///
/// A type rather than a nullable string because there are genuinely three answers, and conflating
/// "leave it alone" with "remove it" would either leak a status or silently break lobby chat.
/// </summary>
public readonly record struct PresenceOutcome(bool Drop, string? Replacement)
{
    /// <summary>Send the original bytes on, untouched.</summary>
    public static readonly PresenceOutcome Forward = new(false, null);

    /// <summary>Send nothing at all.</summary>
    public static readonly PresenceOutcome Dropped = new(true, null);

    public static PresenceOutcome Replaced(string stanza) => new(false, stanza);
}
