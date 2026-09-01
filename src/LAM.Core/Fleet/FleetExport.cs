using System.Globalization;
using System.Text;
using LAM.Core.Model;
using LAM.Core.Riot;

namespace LAM.Core.Fleet;

/// <summary>
/// Writes the fleet out as CSV, for a spreadsheet or an offline copy.
///
/// An export is the one artefact that leaves the vault, so it never contains a password, a session
/// token or a recovery secret — not as an option, not behind a flag. Everything here is the sort of
/// thing you would happily show someone: names, regions, ranks, counts and dates.
/// </summary>
public static class FleetExport
{
    private static readonly string[] Header =
    [
        "Label", "Riot ID", "Region", "Level", "Solo rank", "Flex rank",
        "Champions", "Skins", "Legacy skins", "RP at listed prices",
        "Blue essence", "Riot points", "Created", "First champion",
        "Last used", "Session", "Tags",
    ];

    public static string ToCsv(
        IReadOnlyList<AccountEntry> accounts, SkinCatalogue catalogue, DateTimeOffset nowUtc)
    {
        var text = new StringBuilder();
        text.AppendLine(string.Join(",", Header.Select(Escape)));

        foreach (var account in accounts.Where(a => !a.IsDeleted))
        {
            var identity = account.Identity;
            var stats = identity.ClientStats;
            var value = FleetAnalysis.ValueOf(account, catalogue);
            var session = FleetAnalysis.SessionStandings([account], nowUtc)[0];

            text.AppendLine(string.Join(",", new[]
            {
                Escape(account.Label),
                Escape(account.DisplayRiotId),
                Escape(account.Region),
                Number(identity.SummonerLevel),
                Escape(identity.SoloRank?.ToString()),
                Escape(identity.FlexRank?.ToString()),
                Number(stats?.ChampionsOwned),
                Number(stats?.SkinsOwned),
                Number(value.LegacySkins),
                value.KnownRp.ToString(CultureInfo.InvariantCulture),
                Number(stats?.BlueEssence),
                Number(stats?.RiotPoints),
                Date(identity.Observed?.CreatedUtc),
                Escape(FirstChampion(account, catalogue)),
                Date(account.LastUsedUtc),
                Escape(session.Describe()),
                Escape(string.Join(" ", account.Tags)),
            }));
        }

        return text.ToString();
    }

    private static string? FirstChampion(AccountEntry account, SkinCatalogue catalogue)
    {
        if (account.Identity.ClientStats?.FirstChampionId is not { } id) return null;

        var name = catalogue.ChampionName(id);
        var when = account.Identity.ClientStats.FirstChampionPurchasedUtc;

        return when is null ? name : name + " " + when.Value.UtcDateTime.ToString("yyyy-MM-dd");
    }

    private static string Number(int? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Date(DateTimeOffset? value)
        => value?.UtcDateTime.ToString("yyyy-MM-dd") ?? string.Empty;

    /// <summary>
    /// CSV-quotes a field.
    ///
    /// Always quoted rather than only when necessary: an account label can hold a comma, a quote or a
    /// newline, and a spreadsheet silently mangling a row is the kind of thing nobody notices until
    /// the export is the only copy left.
    /// </summary>
    private static string Escape(string? value)
        => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
}
