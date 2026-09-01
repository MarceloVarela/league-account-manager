namespace LAM.Core.Model;

/// <summary>The decrypted contents of the vault.</summary>
public sealed class VaultDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<AccountEntry> Accounts { get; set; } = [];

    public AppSettings Settings { get; set; } = new();

    public DateTimeOffset LastSavedUtc { get; set; } = DateTimeOffset.UtcNow;

    public AccountEntry? FindById(Guid id) => Accounts.FirstOrDefault(a => a.Id == id);

    /// <summary>
    /// The accounts to show. Trashed ones are excluded everywhere by default — a restore has to be
    /// deliberate, and a deleted account turning up in a search result would defeat the point.
    /// </summary>
    public IEnumerable<AccountEntry> Live => Accounts.Where(a => !a.IsDeleted);

    public IEnumerable<AccountEntry> Trashed => Accounts.Where(a => a.IsDeleted);

    public IEnumerable<AccountEntry> Search(string? query)
        => string.IsNullOrWhiteSpace(query) ? Live : Live.Where(a => a.MatchesSearch(query));

    /// <summary>Every tag in use, de-duplicated case-insensitively, for the filter bar.</summary>
    public IReadOnlyList<string> AllTags()
        => Live.SelectMany(a => a.Tags)
                   .Where(t => !string.IsNullOrWhiteSpace(t))
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                   .ToList();
}
