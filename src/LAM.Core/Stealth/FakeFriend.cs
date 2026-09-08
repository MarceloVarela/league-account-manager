using System.Globalization;
using System.Security;
using System.Xml.Linq;

namespace LAM.Core.Stealth;

/// <summary>
/// A synthetic friend, so stealth can be controlled from inside the game.
///
/// It is added to the friends list the client receives, and messages sent to it never leave this
/// machine — they are commands. That means the mode can be changed mid-game without alt-tabbing,
/// which is the only reason the feature exists.
///
/// ⚠ This edits the user's REAL friends list on its way past. The reference implementation's most
/// commonly reported failure is not "the fake friend is missing" but "my friends list is gone", so
/// two rules hold throughout:
///
///   1. The roster is SPLICED, never parsed and re-serialised. One insertion at a known offset, and
///      the rest of the document is untouched bytes. A DOM round-trip risks re-encoding every real
///      friend's name, and a non-ASCII name is exactly the kind of thing that breaks.
///   2. Anything unexpected returns the input unchanged. A missing fake friend is a disappointment;
///      a mangled roster is damage.
/// </summary>
public sealed class FakeFriend
{
    /// <summary>
    /// The identity the friend appears under.
    ///
    /// Region-hardcoded, as the reference implementation does: the client does not check that a
    /// roster entry lives on the user's own shard, so one value works everywhere. A GUID of our own
    /// so the two tools never collide in the same friends list.
    /// </summary>
    private const string Jid = "8f2c31d6-4a7b-4c19-9e83-1d5a7b0c4e21@eu1.pvp.net";

    private const string Resource = "/RC-Hextech";

    /// <summary>Leading tab sorts it above every real friend, which is the point.</summary>
    private const string DisplayName = "	Hextech Manager";

    /// <summary>The opening tag of the roster payload, matched without depending on its attributes.</summary>
    private const string RosterOpen = "<query xmlns='jabber:iq:riotgames:roster'>";

    private bool _inserted;

    /// <summary>Whether the friend is in the roster yet. Nothing may be sent to it before it is.</summary>
    public bool IsPresent => _inserted;

    /// <summary>
    /// Adds the friend to a roster push, if this stanza is one.
    ///
    /// Returns null when there is nothing to do, and the caller then forwards the original bytes.
    /// Inserted as the FIRST item, immediately after the opening tag, which means the rest of the
    /// roster — however large, however many real friends — is carried across verbatim.
    /// </summary>
    public string? Inject(string stanza)
    {
        if (_inserted) return null;

        var at = stanza.IndexOf(RosterOpen, StringComparison.Ordinal);
        if (at < 0) return null;

        var item =
            "<item jid='" + Jid + "' name='" + Escape(DisplayName) + "' subscription='both' puuid='"
            + Jid.Split('@')[0] + "'>"
            + "<group priority='9999'>Hextech</group>"
            + "<state>online</state>"
            + "<id name='" + Escape(DisplayName) + "' tagline=''/>"
            + "<lol name='" + Escape(DisplayName) + "'/>"
            + "</item>";

        _inserted = true;

        return stanza.Insert(at + RosterOpen.Length, item);
    }

    /// <summary>
    /// Presence for the friend, so it shows as online rather than as a greyed-out entry.
    ///
    /// Must be sent AFTER the roster item: the client discards presence from a JID it does not yet
    /// know about, and there is no retry.
    /// </summary>
    public string Presence()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

        return "<presence from='" + Jid + Resource + "' id='b-" + Guid.NewGuid() + "'>"
               + "<games>"
               + "<league_of_legends><st>chat</st><s.t>" + now + "</s.t>"
               + "<s.p>league_of_legends</s.p><s.c>live</s.c><p>{&quot;pty&quot;:true}</p>"
               + "</league_of_legends>"
               + "</games>"
               + "<show>chat</show>"
               + "<platform>riot</platform>"
               + "<status/>"
               + "</presence>";
    }

    /// <summary>A message that appears in that friend's chat window.</summary>
    public string Say(string text)
    {
        // A second ahead, deliberately: the client orders the chat log by this stamp, and a message
        // stamped in the past can be sorted above existing history or dropped outright.
        var stamp = DateTime.UtcNow.AddSeconds(1).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

        return "<message from='" + Jid + Resource + "' stamp='" + stamp + "' id='hx-"
               + Guid.NewGuid().ToString("N")[..8] + "' type='chat'>"
               + "<body>" + Escape(text) + "</body></message>";
    }

    /// <summary>Whether a stanza is addressed to the friend, and so must never reach Riot.</summary>
    public static bool IsForUs(string stanza)
        => stanza.Contains(Jid, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a command out of a message.
    ///
    /// Matched against the message BODY, not the whole stanza. The reference implementation substring-
    /// matches the entire raw buffer, so "I'm going offline brb" silently flips your status and a
    /// friend's name containing "online" could too. Reading the body and trimming it costs nothing and
    /// removes both.
    /// </summary>
    public static StealthCommand Read(string stanza)
    {
        string body;

        try
        {
            body = XElement.Parse(stanza).Element("body")?.Value ?? string.Empty;
        }
        catch (System.Xml.XmlException)
        {
            return StealthCommand.None;
        }

        var text = body.Trim();

        // No body at all means this was never a message the user typed: the client also sends typing
        // indicators, chat-state notifications and roster traffic to this JID. Answering those with
        // "I did not understand" produced replies to things nobody said, which is worse than useless
        // — it makes the friend look broken before you have typed a word.
        if (text.Length == 0) return StealthCommand.None;

        return text.ToLowerInvariant() switch
        {
            "offline" => StealthCommand.Offline,
            "mobile" => StealthCommand.Mobile,
            "online" => StealthCommand.Online,
            "status" => StealthCommand.Status,
            "help" or "?" => StealthCommand.Help,
            _ => StealthCommand.Unknown,
        };
    }

    /// <summary>
    /// XML-escapes text going into an attribute or body.
    ///
    /// The reference implementation interpolates raw. Its own strings are safe, but an unescaped
    /// ampersand anywhere in this stream is not a wrong message — it is malformed XML, and the client
    /// drops the whole connection.
    /// </summary>
    private static string Escape(string value) => SecurityElement.Escape(value) ?? value;
}

public enum StealthCommand
{
    None,
    Unknown,
    Offline,
    Mobile,
    Online,
    Status,
    Help,
}
