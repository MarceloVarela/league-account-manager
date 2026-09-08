using System.Text;

namespace LAM.Core.Stealth;

/// <summary>
/// Splits a TCP byte stream into whole XMPP stanzas.
///
/// This exists because the obvious approach is wrong in a way that fails silently. Deceive — the tool
/// this feature is modelled on — reads into a fixed buffer, decodes it as UTF-8, and decides what to
/// do with <c>content.Contains("&lt;presence")</c>. There is no accumulator and no framing, so a
/// stanza split across two reads is either mangled or forwarded unrewritten. The second case is the
/// dangerous one: the user believes they are invisible and their real presence has just gone out.
///
/// It works most of the time only because Riot's stanzas are small and usually arrive whole. "Usually"
/// is not a property worth shipping when the failure is invisible.
///
/// So: bytes accumulate until a stanza is complete, and only complete stanzas are handed on. Two
/// details matter and are easy to get wrong —
///
///   * A multi-byte UTF-8 character can be split across reads, so decoding must be incremental. A
///     <see cref="Decoder"/> holds the partial character across calls; <c>Encoding.UTF8.GetString</c>
///     on each read would corrupt it into a replacement character.
///   * Depth has to be tracked, because a stanza contains nested elements. Self-closing elements
///     (<c>&lt;x/&gt;</c>) open and close in one tag, and the stream preamble (<c>&lt;?xml ...?&gt;</c>)
///     and the opening <c>&lt;stream:stream&gt;</c> never close at all.
/// </summary>
public sealed class StanzaSplitter
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _buffer = new();

    /// <summary>
    /// Adds bytes and returns whatever complete top-level stanzas they finished.
    ///
    /// Anything not yet complete stays buffered. The caller must not forward buffered bytes itself —
    /// that would emit the stanza twice.
    /// </summary>
    public IReadOnlyList<string> Push(byte[] bytes, int count)
    {
        var chars = new char[_decoder.GetCharCount(bytes, 0, count, flush: false)];
        var written = _decoder.GetChars(bytes, 0, count, chars, 0, flush: false);

        _buffer.Append(chars, 0, written);

        return Drain();
    }

    private List<string> Drain()
    {
        var complete = new List<string>();
        var text = _buffer.ToString();

        var depth = 0;
        var start = -1;
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] != '<') { i++; continue; }

            var close = text.IndexOf('>', i);

            // The tag itself is torn across reads; wait for the rest of it.
            if (close < 0) break;

            var tag = text[i..(close + 1)];

            if (tag.StartsWith("<?", StringComparison.Ordinal)
                || tag.StartsWith("<!", StringComparison.Ordinal))
            {
                // The XML preamble stands alone and belongs to nobody.
                if (depth == 0) { complete.Add(tag); start = -1; }
                i = close + 1;
                continue;
            }

            if (tag.StartsWith("</", StringComparison.Ordinal))
            {
                depth--;

                if (depth <= 0 && start >= 0)
                {
                    complete.Add(text[start..(close + 1)]);
                    start = -1;
                    depth = 0;
                }

                i = close + 1;
                continue;
            }

            var selfClosing = tag.EndsWith("/>", StringComparison.Ordinal);

            if (depth == 0)
            {
                start = i;

                // <stream:stream ...> opens the session and is closed only at the very end, so it can
                // never be waited on. Emit it immediately and stay at depth 0.
                if (!selfClosing && tag.StartsWith("<stream:stream", StringComparison.Ordinal))
                {
                    complete.Add(tag);
                    start = -1;
                    i = close + 1;
                    continue;
                }
            }

            if (selfClosing)
            {
                if (depth == 0 && start >= 0)
                {
                    complete.Add(text[start..(close + 1)]);
                    start = -1;
                }
            }
            else
            {
                depth++;
            }

            i = close + 1;
        }

        // Keep whatever is still open, plus any tail after the last complete stanza.
        var consumed = start >= 0 ? start : LastConsumed(text, i);

        _buffer.Clear();
        _buffer.Append(text[consumed..]);

        return complete;
    }

    /// <summary>Where the scan stopped, so a half-written tag is not lost.</summary>
    private static int LastConsumed(string text, int scanned)
    {
        if (scanned >= text.Length) return text.Length;

        // The scan halted on an unterminated tag; keep from its opening angle bracket.
        var open = text.LastIndexOf('<', Math.Max(0, Math.Min(scanned, text.Length - 1)));
        return open < 0 ? text.Length : open;
    }
}
