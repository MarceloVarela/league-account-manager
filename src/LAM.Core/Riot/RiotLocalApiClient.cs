using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace LAM.Core.Riot;

/// <summary>
/// Talks to the Riot Client's and the League client's loopback HTTP APIs.
///
/// Both serve HTTPS with a self-signed certificate, so validation has to be relaxed — but only for
/// 127.0.0.1, and only for the port named in a lockfile we just read off disk. A blanket
/// "accept any certificate" callback would disable TLS checking for every request the app makes.
/// </summary>
public sealed class RiotLocalApiClient : IDisposable
{
    private readonly HttpClient _http;

    public RiotLocalApiClient(Lockfile lockfile)
    {
        Lockfile = lockfile;

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, _, _, _) =>
                request.RequestUri is { IsLoopback: true } uri && uri.Port == lockfile.Port,
            AllowAutoRedirect = false,
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = lockfile.BaseAddress,
            Timeout = TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", lockfile.BasicAuthParameter);
    }

    public Lockfile Lockfile { get; }

    /// <summary>
    /// Whether the client currently holds an authorised RSO session.
    ///
    /// This is the signal the session-swap strategy waits on: a 200 means the swapped-in cookies
    /// were accepted, while the client's characteristic <c>400 No client authorization</c> means
    /// they were not and we should fall back to typing.
    /// </summary>
    public async Task<bool> IsAuthorizedAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync("/rso-auth/v1/authorization", cancellationToken);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // The client is still starting, or has just torn the API down. Neither is authorised.
            return false;
        }
    }

    /// <summary>Polls <see cref="IsAuthorizedAsync"/> until it succeeds or the timeout elapses.</summary>
    public async Task<bool> WaitForAuthorizedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsAuthorizedAsync(cancellationToken)) return true;
            await Task.Delay(500, cancellationToken);
        }
        return false;
    }

    /// <summary>GETs a JSON document, returning null on any non-success or transport failure.</summary>
    public async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(path, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(path, cancellationToken);
            if (!response.IsSuccessStatusCode) return default;
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return default;
        }
    }

    /// <summary>
    /// Fetches a binary asset, such as a skin tile. The client serves its own art tree on loopback,
    /// which is faster than any mirror and needs no internet connection.
    /// </summary>
    public async Task<byte[]?> GetBytesAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(path, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
