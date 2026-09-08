using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace LAM.Core.Stealth;

/// <summary>
/// A local stand-in for Riot's client-configuration service.
///
/// The Riot Client is launched with <c>--client-config-url</c> pointing here. Every request is passed
/// straight through to Riot and the answer returned verbatim — except the one document that says where
/// chat lives, which is rewritten to point at our own proxy.
///
/// It is a transparent reverse proxy on purpose. The client asks for a great many things through this
/// URL and most of them have nothing to do with chat; anything not recognised must cross unchanged, or
/// the client breaks in ways that have nothing to do with the feature.
///
/// ⚠ The <c>authorization</c> and <c>x-riot-entitlements-jwt</c> headers MUST be forwarded. The chat
/// keys exist only in the authenticated response — without them Riot answers with an empty document,
/// the patch finds nothing, and stealth silently does nothing at all.
/// </summary>
public sealed class ClientConfigProxy : IDisposable
{
    private const string Upstream = "https://clientconfig.rpg.riotgames.com";
    private const string PasUrl = "https://riot-geo.pas.si.riotgames.com/pas/v1/service/chat";

    /// <summary>Headers the upstream needs to recognise the caller. Nothing else is forwarded.</summary>
    private static readonly string[] Forwarded = ["authorization", "x-riot-entitlements-jwt", "user-agent"];

    private readonly HttpListener _listener = new();
    private readonly HttpClient _http;
    private readonly string _localHost;
    private readonly Func<int> _chatPort;
    private readonly Action<string>? _trace;

    private CancellationTokenSource? _running;

    /// <summary>The genuine chat server, once a client has asked for the configuration.</summary>
    public ChatServer? Upstream_ { get; private set; }

    /// <summary>Raised the first time the real chat server is discovered.</summary>
    public event Action<ChatServer>? ChatServerResolved;

    public int Port { get; private set; }

    public bool IsListening => _listener.IsListening;

    /// <param name="localHost">The hostname our certificate is valid for.</param>
    /// <param name="chatPort">The chat proxy's port, read late because it binds after this does.</param>
    public ClientConfigProxy(string localHost, Func<int> chatPort, Action<string>? trace = null,
        HttpClient? http = null)
    {
        _localHost = localHost;
        _chatPort = chatPort;
        _trace = trace;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>
    /// Binds a loopback port and starts serving.
    ///
    /// Returns false rather than throwing: a proxy that cannot bind means signing in WITHOUT stealth,
    /// which is a lost convenience, not a failed sign-in.
    /// </summary>
    public bool TryStart()
    {
        try
        {
            Port = FreePort();

            _listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
            _listener.Start();

            _running = new CancellationTokenSource();
            _ = Task.Run(() => AcceptAsync(_running.Token));

            _trace?.Invoke("config proxy listening on 127.0.0.1:" + Port);
            return true;
        }
        catch (Exception ex) when (ex is HttpListenerException or SocketException or ObjectDisposedException)
        {
            _trace?.Invoke("config proxy could not start (" + ex.GetType().Name + ")");
            return false;
        }
    }

    private static int FreePort()
    {
        // Let the OS choose, then hand the number to the client. A fixed port would collide with
        // whatever else is on the machine, and with a second copy of this app.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException
                                           or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(context, cancellationToken), cancellationToken);
        }
    }

    private async Task ServeAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, Upstream + (context.Request.RawUrl ?? "/"));

            foreach (var name in Forwarded)
            {
                var value = context.Request.Headers[name];
                if (!string.IsNullOrEmpty(value)) request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            var patched = TryPatch(body, context.Request.Headers["authorization"], cancellationToken);

            var bytes = System.Text.Encoding.UTF8.GetBytes(patched ?? body);

            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;

            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or HttpListenerException or ObjectDisposedException
                                       or System.IO.IOException)
        {
            // Never trace the body or the header: both carry the account's token.
            _trace?.Invoke("config request failed (" + ex.GetType().Name + ")");
        }
        finally
        {
            try { context.Response.Close(); } catch (Exception ex) when (ex is ObjectDisposedException) { }
        }
    }

    private string? TryPatch(string body, string? authorization, CancellationToken cancellationToken)
    {
        var affinity = ClientConfigPatch.ReadAffinity(ReadPasToken(authorization, cancellationToken));

        var patched = ClientConfigPatch.Patch(body, affinity, _localHost, _chatPort());
        if (patched is not { } result) return null;

        if (result.Upstream is { } chat && Upstream_ is null)
        {
            Upstream_ = chat;
            _trace?.Invoke("chat server resolved: " + chat.Host + ":" + chat.Port);
            ChatServerResolved?.Invoke(chat);
        }

        return result.Json;
    }

    /// <summary>
    /// Asks Riot which region this player's chat lives in.
    ///
    /// Best-effort by design: without it the plain <c>chat.host</c> is used, which is right for most
    /// players anyway. Synchronous here because it happens inside one config request and the client is
    /// waiting on the response.
    /// </summary>
    private string? ReadPasToken(string? authorization, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(authorization)) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, PasUrl);
            request.Headers.TryAddWithoutValidation("authorization", authorization);

            using var response = _http.Send(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            return response.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult().Trim('"');
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or InvalidOperationException)
        {
            _trace?.Invoke("region lookup unavailable (" + ex.GetType().Name + ")");
            return null;
        }
    }

    public void Dispose()
    {
        _running?.Cancel();
        _running?.Dispose();
        _running = null;

        try { if (_listener.IsListening) _listener.Stop(); }
        catch (ObjectDisposedException) { }

        _listener.Close();
        _http.Dispose();
    }
}
