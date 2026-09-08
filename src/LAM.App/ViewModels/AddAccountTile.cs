namespace LAM.App.ViewModels;

/// <summary>
/// The dashed tile that closes the accounts grid.
///
/// A type rather than a flag so the grid can hold one collection and let WPF pick the template by
/// data type. A singleton because it carries no state — there is only ever one, and it is always
/// last.
/// </summary>
public sealed class AddAccountTile
{
    public static AddAccountTile Instance { get; } = new();

    private AddAccountTile() { }
}
