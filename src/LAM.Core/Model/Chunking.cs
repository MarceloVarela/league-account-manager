namespace LAM.Core.Model;

/// <summary>
/// Splitting a sequence into fixed-size rows.
///
/// Lives here rather than in the UI because it is pure logic worth testing on its own — and because
/// the reason it exists is easy to lose: WPF's wrapping panel does not virtualize, so a collection of
/// fifteen hundred tiles has to be grouped into rows for the virtualizing list to handle it. Getting
/// the remainder wrong silently drops items off the end of a collection.
/// </summary>
public static class Chunking
{
    /// <summary>Groups <paramref name="items"/> into rows of at most <paramref name="perRow"/>.</summary>
    public static IReadOnlyList<IReadOnlyList<T>> IntoRows<T>(IReadOnlyList<T> items, int perRow)
    {
        if (perRow < 1) perRow = 1;

        var rows = new List<IReadOnlyList<T>>((items.Count + perRow - 1) / perRow);

        for (var start = 0; start < items.Count; start += perRow)
        {
            var length = Math.Min(perRow, items.Count - start);
            var row = new T[length];

            for (var i = 0; i < length; i++) row[i] = items[start + i];
            rows.Add(row);
        }

        return rows;
    }
}
