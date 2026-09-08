using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Documents;

namespace LAM.App.Views;

/// <summary>
/// Builds the detail window's aligned readouts as coloured runs rather than as one flat string.
///
/// These panes used to be a <c>StringBuilder</c> assigned to a single <c>TextBlock</c>, which meant
/// the label and its value were characters in the same run — nothing to colour separately, so a
/// twenty-row readout arrived as one undifferentiated block and the reader had to hunt for the two
/// facts that matter.
///
/// Three tiers, and only three:
///
///   * <b>Label</b> — muted. It is scaffolding; you read it once to find the row.
///   * <b>Value</b> — ink.
///   * <b>Key value</b> — accent. Reserved for the fields that actually prove ownership of an
///     account: the PUUID, the registered email, when it was created, the first champion and skin,
///     the legacy username and id, the password-change date. That list is short on purpose — the app
///     exists to recover accounts, and highlighting twenty things highlights nothing.
///
/// Alignment still comes from Consolas plus padding, exactly as before, so the columns are unchanged.
/// Brushes are attached with <c>SetResourceReference</c>, which is the DynamicResource equivalent in
/// code: the runs follow a theme swap, and no palette token is resolved into a C# value (which the
/// theme tests forbid, and rightly — a resolved brush freezes against the palette that was live).
/// </summary>
internal sealed class Readout
{
    private readonly TextBlock _target;
    private readonly List<Inline> _inlines = [];

    public Readout(TextBlock target)
    {
        _target = target;
        _target.Inlines.Clear();
    }

    /// <summary>A label/value row, padded to <paramref name="pad"/> like the original.</summary>
    public void Pair(string label, int pad, string? value, bool key = false)
    {
        Add(label.PadRight(pad), "MutedText");
        Add(string.IsNullOrWhiteSpace(value) ? "—" : value, key ? "Accent" : "Ink");
        Break();
    }

    /// <summary>The recovery tab's "you: … / client: …" comparison row.</summary>
    public void Compare(string label, int pad, string? typed, int typedPad, string? seen, bool key = false)
    {
        Add(label.PadRight(pad), "MutedText");

        // The "you: " prefix is its own run, so the VALUE pads to the column width minus that prefix.
        // Getting this arithmetic wrong ran the value straight into "client:" with no gap at all, and
        // left every row starting its second column at a different place.
        const int PrefixWidth = 5;   // "you: "

        Add("you: ", "MutedText");
        Add((string.IsNullOrWhiteSpace(typed) ? "—" : typed).PadRight(Math.Max(1, typedPad - PrefixWidth)),
            key ? "Accent" : "Ink");

        Add("client: ", "MutedText");
        Add(string.IsNullOrWhiteSpace(seen) ? "—" : seen, key ? "Accent" : "Ink");
        Break();
    }

    /// <summary>A heading or a free-standing line, in the label tone.</summary>
    public void Plain(string line, string token = "MutedText")
    {
        Add(line, token);
        Break();
    }

    /// <summary>A line that is its own warning.</summary>
    public void Warn(string line) => Plain(line, "WarnFg");

    public void Blank() => Break();

    private void Add(string run, string token)
    {
        var inline = new Run(run);
        inline.SetResourceReference(TextElement.ForegroundProperty, token);
        _inlines.Add(inline);
    }

    private void Break() => _inlines.Add(new LineBreak());

    /// <summary>Commits everything at once — one layout pass rather than one per row.</summary>
    public void Done()
    {
        foreach (var inline in _inlines) _target.Inlines.Add(inline);
    }
}
