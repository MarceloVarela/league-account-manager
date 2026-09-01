namespace LAM.Core.Riot;

/// <summary>
/// Maps a region to the two different hostnames Riot's API needs.
///
/// The API is split in a way that catches people out: account and match data live on a *regional*
/// route (americas / europe / asia / sea), while summoner and league data live on a *platform*
/// route (br1 / na1 / euw1 …). Getting it wrong returns a 404 that looks like "no such account".
/// </summary>
public static class RiotRegions
{
    private sealed record Routing(string Platform, string Regional, string Display);

    private static readonly Dictionary<string, Routing> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BR"] = new("br1", "americas", "Brazil"),
        ["NA"] = new("na1", "americas", "North America"),
        ["LAN"] = new("la1", "americas", "Latin America North"),
        ["LAS"] = new("la2", "americas", "Latin America South"),
        ["OCE"] = new("oc1", "sea", "Oceania"),
        ["EUW"] = new("euw1", "europe", "Europe West"),
        ["EUNE"] = new("eun1", "europe", "Europe Nordic & East"),
        ["TR"] = new("tr1", "europe", "Turkey"),
        ["RU"] = new("ru", "europe", "Russia"),
        ["ME"] = new("me1", "europe", "Middle East"),
        ["KR"] = new("kr", "asia", "Korea"),
        ["JP"] = new("jp1", "asia", "Japan"),
        ["TW"] = new("tw2", "sea", "Taiwan"),
        ["SG"] = new("sg2", "sea", "Singapore"),
        ["PH"] = new("ph2", "sea", "Philippines"),
        ["TH"] = new("th2", "sea", "Thailand"),
        ["VN"] = new("vn2", "sea", "Vietnam"),
    };

    public static IReadOnlyList<string> All => Map.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    public static string? PlatformFor(string? region)
        => region is not null && Map.TryGetValue(region, out var routing) ? routing.Platform : null;

    public static string? RegionalFor(string? region)
        => region is not null && Map.TryGetValue(region, out var routing) ? routing.Regional : null;

    public static string DisplayFor(string? region)
        => region is not null && Map.TryGetValue(region, out var routing) ? routing.Display : (region ?? "Unknown");

    public static bool IsKnown(string? region) => region is not null && Map.ContainsKey(region);
}
