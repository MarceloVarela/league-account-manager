namespace LAM.Core.Riot;

/// <summary>
/// Fetches profile icons from Data Dragon and keeps them on disk.
///
/// Data Dragon is Riot's public CDN — no API key, no rate limit worth worrying about. Caching still
/// matters: without it the grid would re-download an icon per account on every launch, and the app
/// would look broken offline. Once warmed it never touches the network again for the same icon.
/// </summary>
public sealed class ProfileIconCache
{
    private const string VersionsUrl = "https://ddragon.leagueoflegends.com/api/versions.json";

    private readonly string _directory;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _version;

    public ProfileIconCache(string directory, HttpClient? http = null)
    {
        _directory = directory;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>
    /// Returns a local path for the icon, downloading it once if needed.
    ///
    /// Null when it cannot be had — the caller falls back to a generated tile rather than showing a
    /// gap, so a missing icon is a cosmetic detail and never an error.
    /// </summary>
    public async Task<string?> GetIconPathAsync(int iconId, CancellationToken cancellationToken)
    {
        if (iconId <= 0) return null;

        var path = Path.Combine(_directory, iconId + ".png");
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have fetched it while we queued.
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

            var version = await ResolveVersionAsync(cancellationToken);
            if (version is null) return null;

            var url = "https://ddragon.leagueoflegends.com/cdn/" + version + "/img/profileicon/" + iconId + ".png";
            var bytes = await _http.GetByteArrayAsync(url, cancellationToken);
            if (bytes.Length == 0) return null;

            Directory.CreateDirectory(_directory);

            // Write beside and move, so an interrupted download cannot leave a truncated file that
            // then gets treated as cached forever.
            var temporary = path + ".part";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, path, overwrite: true);

            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The current Data Dragon version, resolved once per run.
    ///
    /// Icons live under a versioned path, and a stale version eventually 404s for newly added icons.
    /// Resolved lazily so the app makes no network call at all until an icon is actually wanted.
    /// </summary>
    private async Task<string?> ResolveVersionAsync(CancellationToken cancellationToken)
    {
        if (_version is not null) return _version;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                await _http.GetStringAsync(VersionsUrl, cancellationToken));

            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                _version = entry.GetString();
                return _version;
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>True when the icon is already on disk, so the UI can show it without awaiting.</summary>
    public string? CachedPath(int iconId)
    {
        if (iconId <= 0) return null;
        var path = Path.Combine(_directory, iconId + ".png");
        return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }
}
