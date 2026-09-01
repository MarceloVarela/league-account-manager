namespace LAM.Core.Riot;

/// <summary>
/// Fetches skin tile art, preferring the running client and falling back to Community Dragon.
///
/// Two sources because neither is sufficient alone. The client serves its own assets and is instant,
/// but only while League is open — and browsing a collection is exactly the thing you want to do
/// when it is closed. Community Dragon mirrors the same asset paths publicly, which is what makes
/// the collection viewable offline once cached.
///
/// Downloads are lazy by design. An account here owns over fifteen hundred skins; fetching them all
/// up front for a screen that may never be scrolled would be indefensible, so tiles arrive as rows
/// come into view and are kept on disk afterwards.
/// </summary>
public sealed class SkinArtCache
{
    /// <summary>Community Dragon mirrors the client's asset tree under this prefix, lowercased.</summary>
    private const string CommunityDragon =
        "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default";

    private const string AssetPrefix = "/lol-game-data/assets";

    private readonly string _directory;
    private readonly RiotPaths _paths;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(4, 4);

    public SkinArtCache(string directory, RiotPaths paths, HttpClient? http = null)
    {
        _directory = directory;
        _paths = paths;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>The tile if it is already on disk, without touching the network.</summary>
    public string? CachedPath(int skinId) => CachedPath(skinId.ToString());

    /// <summary>As above, for art keyed by something other than a skin id — loot, for instance.</summary>
    public string? CachedPath(string key)
    {
        var path = Path.Combine(_directory, Sanitise(key) + ".jpg");
        return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    /// <summary>Loot ids are used as filenames, so anything a path would object to is replaced.</summary>
    private static string Sanitise(string key)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string([.. key.Select(c => invalid.Contains(c) ? '_' : c)]);
    }

    /// <summary>
    /// Returns a local path for the skin's tile, downloading it once if needed.
    ///
    /// Null rather than throwing when it cannot be had: art is decoration, and a missing tile should
    /// leave a placeholder rather than break a list of fifteen hundred rows.
    /// </summary>
    public Task<string?> GetTileAsync(CatalogueSkin skin, CancellationToken cancellationToken)
        => GetAssetAsync(skin.Id.ToString(), skin.TilePath, cancellationToken);

    /// <summary>
    /// The same, for any client asset path under an arbitrary cache key. Loot tiles use this — they
    /// point at the same asset tree but are identified by a loot id rather than a skin id.
    /// </summary>
    public async Task<string?> GetAssetAsync(string key, string? assetPath, CancellationToken cancellationToken)
    {
        if (CachedPath(key) is { } cached) return cached;
        if (string.IsNullOrWhiteSpace(assetPath)) return null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (CachedPath(key) is { } raced) return raced;

            var bytes = await DownloadAsync(assetPath!, cancellationToken);
            if (bytes is null || bytes.Length == 0) return null;

            Directory.CreateDirectory(_directory);

            // Write beside and move, so an interrupted download cannot leave a truncated file that
            // is then treated as cached for ever.
            var path = Path.Combine(_directory, Sanitise(key) + ".jpg");
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

    private async Task<byte[]?> DownloadAsync(string assetPath, CancellationToken cancellationToken)
    {
        // The client first: it is on loopback, needs no internet, and is already authenticated.
        if (Lockfile.Read(_paths.LeagueLockfile) is { } lockfile)
        {
            try
            {
                using var client = new RiotLocalApiClient(lockfile);
                if (await client.GetBytesAsync(assetPath, cancellationToken) is { Length: > 0 } bytes)
                    return bytes;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Fall through to the public mirror.
            }
        }

        try
        {
            return await _http.GetByteArrayAsync(ToCommunityDragonUrl(assetPath), cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps a client asset path onto the public mirror.
    ///
    /// The mirror serves the same tree with the <c>/lol-game-data/assets</c> prefix stripped and the
    /// whole path lowercased; it 404s on the original casing.
    /// </summary>
    internal static string ToCommunityDragonUrl(string assetPath)
    {
        var relative = assetPath.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase)
            ? assetPath[AssetPrefix.Length..]
            : assetPath;

        if (!relative.StartsWith('/')) relative = "/" + relative;

        return CommunityDragon + relative.ToLowerInvariant();
    }
}
