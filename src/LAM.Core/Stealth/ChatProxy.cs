using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace LAM.Core.Stealth;

/// <summary>
/// Relays the League client's chat connection, rewriting the presence it sends.
///
/// The client believes it is talking to Riot's chat server; it is talking to us, because the config
/// proxy told it chat lives here. We terminate its TLS, open our own connection onward to the genuine
/// server, and copy bytes between the two — editing only the presence stanzas on the way out.
///
/// Authentication is never touched. The SASL exchange, the tokens, the stream negotiation all pass
/// through byte-for-byte; the session that results is genuinely the user's own. That is also why this
/// class must never log a stanza: the account's token crosses it in plain text.
///
/// The upstream leg keeps FULL certificate validation. We are the ones being trusted here; we do not
/// extend that to anyone else.
/// </summary>
public sealed class ChatProxy : IDisposable
{
    private readonly SslStreamCertificateContext _certificate;
    private readonly Func<StealthMode> _mode;
    private readonly Func<bool> _lobbyChat;

    /// <summary>
    /// Whether the fake friend should introduce itself, asked once and answered once.
    ///
    /// It used to greet on every connection, and the client reconnects its chat socket often enough
    /// that the greeting became spam for something the user already knew.
    /// </summary>
    private readonly Func<bool> _shouldGreet;
    private readonly Action<string>? _trace;

    /// <summary>
    /// The last presence each live connection sent, and the stream to send a replacement on.
    ///
    /// Kept so a mode change can take effect immediately. Presence is only emitted when the client
    /// decides to emit it — which can be minutes — so without replaying the last one, switching to
    /// offline mid-game appears to do nothing at all and then happens by itself later.
    /// </summary>
    private readonly List<Relay> _live = [];

    private TcpListener? _listener;
    private CancellationTokenSource? _running;
    private ChatServer? _upstream;

    /// <summary>
    /// Raised when a command asks for a different mode.
    ///
    /// An event rather than a direct write, because the mode lives in the vault's settings and only
    /// the app knows how to persist it.
    /// </summary>
    public event Action<StealthMode>? ModeRequested;

    public int Port { get; private set; }

    public bool IsListening => _listener is not null;

    public ChatProxy(SslStreamCertificateContext certificate, Func<StealthMode> mode,
        Func<bool> lobbyChat, Func<bool>? shouldGreet = null, Action<string>? trace = null)
    {
        _certificate = certificate;
        _mode = mode;
        _lobbyChat = lobbyChat;
        _shouldGreet = shouldGreet ?? (() => false);
        _trace = trace;
    }

    /// <summary>Where to relay to. Known only once the client has fetched its configuration.</summary>
    public void UseUpstream(ChatServer upstream) => _upstream = upstream;

    /// <summary>
    /// Pushes the current mode out now, by replaying each connection's last presence through the
    /// rewriter. Safe to call when nothing is connected.
    /// </summary>
    public async Task RefreshPresenceAsync(CancellationToken cancellationToken = default)
    {
        Relay[] live;
        lock (_live) live = [.. _live];

        foreach (var relay in live)
        {
            if (relay.LastPresence is not { } presence) continue;

            var outcome = PresenceRewriter.Rewrite(presence, _mode(), _lobbyChat());
            if (outcome.Drop) continue;

            try
            {
                await relay.ToServer.WriteAsync(
                    Encoding.UTF8.GetBytes(outcome.Replacement ?? presence), cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException
                                           or OperationCanceledException)
            {
                // The connection went away between the snapshot and the write. Nothing to do: the
                // next one will carry the new mode anyway.
            }
        }
    }

    /// <summary>One live client connection, and the last thing it said about itself.</summary>
    private sealed class Relay
    {
        public required SslStream ToServer { get; init; }
        public required SslStream ToClient { get; init; }
        public string? LastPresence { get; set; }
        public FakeFriend Friend { get; } = new();
    }

    /// <summary>
    /// Binds a loopback port.
    ///
    /// False rather than an exception: a proxy that cannot bind means signing in without stealth.
    /// </summary>
    public bool TryStart()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();

            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _running = new CancellationTokenSource();
            _ = Task.Run(() => AcceptAsync(_running.Token));

            _trace?.Invoke("chat proxy listening on 127.0.0.1:" + Port);
            return true;
        }
        catch (SocketException ex)
        {
            _trace?.Invoke("chat proxy could not start (" + ex.SocketErrorCode + ")");
            _listener = null;
            return false;
        }
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
                                           or SocketException)
            {
                return;
            }

            // The Riot Client and the League client each open their own connection, so this must
            // keep accepting rather than serving one and stopping.
            _ = Task.Run(() => RelayAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task RelayAsync(TcpClient client, CancellationToken cancellationToken)
    {
        if (_upstream is not { } upstream)
        {
            _trace?.Invoke("chat connection arrived before the real server was known; dropping it");
            client.Dispose();
            return;
        }

        TcpClient? server = null;
        SslStream? fromClient = null;
        SslStream? toServer = null;

        try
        {
            fromClient = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await fromClient.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions { ServerCertificateContext = _certificate },
                cancellationToken);

            server = new TcpClient();
            await server.ConnectAsync(upstream.Host, upstream.Port, cancellationToken);

            // Full validation on the way out. We are impersonating Riot to the client by arrangement;
            // that is no reason to accept an impostor ourselves.
            toServer = new SslStream(server.GetStream(), leaveInnerStreamOpen: false);
            await toServer.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = upstream.Host }, cancellationToken);

            _trace?.Invoke("chat connection established to " + upstream.Host);

            // One pump per direction. Whichever ends first tears the pair down, so a half-closed
            // socket cannot leave the other side blocked in a read forever.
            using var pair = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var relay = new Relay { ToServer = toServer, ToClient = fromClient };
            lock (_live) _live.Add(relay);

            var outbound = PumpOutboundAsync(relay, fromClient, toServer, pair.Token);
            var inbound = PumpInboundAsync(relay, toServer, fromClient, pair.Token);

            await Task.WhenAny(outbound, inbound);
            await pair.CancelAsync();

            await Task.WhenAll(Quietly(outbound), Quietly(inbound));

            lock (_live) _live.Remove(relay);
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException
                                       or OperationCanceledException or ObjectDisposedException)
        {
            _trace?.Invoke("chat connection ended (" + ex.GetType().Name + ")");
        }
        finally
        {
            // Disposed explicitly, and in every case. The reference implementation never closes
            // either side, so its sockets accumulate until the process exits.
            fromClient?.Dispose();
            toServer?.Dispose();
            server?.Dispose();
            client.Dispose();
        }
    }

    private static async Task Quietly(Task task)
    {
        try { await task; }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    /// <summary>Client to server: the only direction that gets edited.</summary>
    private async Task PumpOutboundAsync(Relay relay, SslStream from, SslStream to,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var splitter = new StanzaSplitter();

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await from.ReadAsync(buffer, cancellationToken);
            if (read == 0) return;

            var stanzas = splitter.Push(buffer, read);

            // Nothing complete yet — the stanza is still being received. Holding the bytes is the
            // whole point: forwarding a half stanza now means the rewrite never sees it.
            if (stanzas.Count == 0) continue;

            foreach (var stanza in stanzas)
            {
                // Anything addressed to the fake friend is a command, and must never leave the
                // machine — Riot has no such account, and forwarding it would tell them about a
                // roster entry that only exists locally.
                if (FakeFriend.IsForUs(stanza))
                {
                    await HandleCommandAsync(relay, stanza, cancellationToken);
                    continue;
                }

                // Remember it before deciding: a replay needs the ORIGINAL, not the rewritten copy,
                // or switching back to online would re-send an "offline" that can never be undone.
                if (stanza.StartsWith("<presence", StringComparison.Ordinal)) relay.LastPresence = stanza;

                var outcome = PresenceRewriter.Rewrite(stanza, _mode(), _lobbyChat());

                // Dropped means send nothing: the client believes it announced itself to the room and
                // the room never hears about it.
                if (outcome.Drop) continue;

                await to.WriteAsync(
                    Encoding.UTF8.GetBytes(outcome.Replacement ?? stanza), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Server to client. Copied verbatim except for the one roster push we add a friend to.
    ///
    /// Framed rather than copied straight through, because a roster push split across two TCP reads
    /// would otherwise be missed — which is precisely how the reference implementation ends up with
    /// no fake friend and no explanation.
    /// </summary>
    private async Task PumpInboundAsync(Relay relay, SslStream from, SslStream to,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var splitter = new StanzaSplitter();

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await from.ReadAsync(buffer, cancellationToken);
            if (read == 0) return;

            var stanzas = splitter.Push(buffer, read);
            if (stanzas.Count == 0) continue;

            foreach (var stanza in stanzas)
            {
                var injected = relay.Friend.Inject(stanza);

                await to.WriteAsync(Encoding.UTF8.GetBytes(injected ?? stanza), cancellationToken);

                // The client discards presence from a JID it does not know yet, so this can only be
                // sent once the roster carrying it has already gone out.
                if (injected is not null)
                {
                    await to.WriteAsync(
                        Encoding.UTF8.GetBytes(relay.Friend.Presence()), cancellationToken);

                    // Once, ever. Phrased as a statement rather than an instruction: stealth is
                    // already applied by the time this arrives, and nothing here needs doing.
                    if (_shouldGreet())
                    {
                        await SayAsync(relay,
                            "Stealth is on and you are already appearing " + Word(_mode())
                            + ". Nothing to do \u2014 message me only if you want to change it: "
                            + "offline, mobile or online.",
                            cancellationToken);
                    }
                }
            }
        }
    }

    /// <summary>Acts on a command typed to the fake friend, and answers in the same window.</summary>
    private async Task HandleCommandAsync(Relay relay, string stanza, CancellationToken cancellationToken)
    {
        var command = FakeFriend.Read(stanza);

        switch (command)
        {
            case StealthCommand.Offline:
            case StealthCommand.Mobile:
            case StealthCommand.Online:
                var mode = command switch
                {
                    StealthCommand.Offline => StealthMode.Offline,
                    StealthCommand.Mobile => StealthMode.Mobile,
                    _ => StealthMode.Online,
                };

                ModeRequested?.Invoke(mode);

                // Push it out now rather than waiting for the client to volunteer presence, which
                // could be minutes and makes the command look like it did nothing.
                await RefreshPresenceAsync(cancellationToken);
                await SayAsync(relay, "You are now appearing " + Word(mode) + ".", cancellationToken);
                return;

            case StealthCommand.Status:
                await SayAsync(relay, "You are appearing " + Word(_mode()) + ".", cancellationToken);
                return;

            case StealthCommand.Help:
                await SayAsync(relay,
                    "Send offline, mobile or online to change how you appear. Send status to check.",
                    cancellationToken);
                return;

            case StealthCommand.Unknown:
                await SayAsync(relay, "I did not understand that. Send help for the list.",
                    cancellationToken);
                return;

            default:
                // Not a message — a chat-state notification, a roster edit, something else entirely.
                // Silently swallowed, because it still must not reach Riot.
                return;
        }
    }

    private static string Word(StealthMode mode) => mode switch
    {
        StealthMode.Offline => "offline",
        StealthMode.Mobile => "on mobile",
        _ => "online",
    };

    private static async Task SayAsync(Relay relay, string text, CancellationToken cancellationToken)
    {
        if (!relay.Friend.IsPresent) return;

        try
        {
            await relay.ToClient.WriteAsync(
                Encoding.UTF8.GetBytes(relay.Friend.Say(text)), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException
                                       or OperationCanceledException)
        {
            // The window closed. Nothing to recover.
        }
    }

    public void Dispose()
    {
        _running?.Cancel();
        _running?.Dispose();
        _running = null;

        try { _listener?.Stop(); }
        catch (SocketException) { }

        _listener = null;
    }
}
