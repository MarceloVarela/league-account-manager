using System.Net;
using System.Net.Sockets;

namespace LAM.Core.Stealth;

/// <summary>
/// Everything stealth needs, started before the Riot Client and torn down with the app.
///
/// One object owns the whole feature so there is exactly one place that decides whether stealth is
/// on. That decision is deliberately all-or-nothing and made BEFORE the client launches, because
/// there is no way back afterwards: once the client has been handed a rewritten configuration it no
/// longer knows where the real chat server is, so a proxy that dies mid-session takes chat with it
/// until the client is restarted.
///
/// So the rule is fail-open. If the certificate is unusable, or a port will not bind, or the hostname
/// does not resolve to this machine, <see cref="TryStartAsync"/> returns null and the caller launches
/// the client normally — no argument, nothing in the middle, chat working exactly as it always did.
/// A lost convenience, never a failed sign-in.
/// </summary>
public sealed class StealthSession : IDisposable
{
    private readonly ClientConfigProxy _config;
    private readonly ChatProxy _chat;
    private readonly Action<string>? _trace;

    private StealthSession(ClientConfigProxy config, ChatProxy chat, Action<string>? trace)
    {
        _config = config;
        _chat = chat;
        _trace = trace;

        // The real chat server is only known once the client asks for its configuration, which
        // happens after both proxies are already listening.
        _config.ChatServerResolved += _chat.UseUpstream;
    }

    /// <summary>
    /// Raised when the fake friend is sent a command asking for a different mode.
    ///
    /// Surfaced here rather than handled inside the proxy because the mode lives in the encrypted
    /// vault, and only the app knows how to persist it.
    /// </summary>
    public event Action<StealthMode>? ModeRequested
    {
        add => _chat.ModeRequested += value;
        remove => _chat.ModeRequested -= value;
    }

    /// <summary>Applies the current mode to every live connection straight away.</summary>
    public Task RefreshPresenceAsync(CancellationToken cancellationToken = default)
        => _chat.RefreshPresenceAsync(cancellationToken);

    /// <summary>The argument the Riot Client must be launched with for any of this to matter.</summary>
    public string LaunchArgument =>
        "--client-config-url=\"http://127.0.0.1:" + _config.Port + "\" --allow-direct-launch";

    /// <summary>
    /// Brings the whole thing up, or returns null if anything at all is not right.
    ///
    /// Order matters: the chat proxy binds first, because the config proxy has to be able to tell the
    /// client which port to connect to.
    /// </summary>
    public static async Task<StealthSession?> TryStartAsync(
        StealthOptions options, Func<StealthMode> mode, Func<bool> lobbyChat, Action<string>? trace,
        CancellationToken cancellationToken)
    {
        if (!ResolvesLocally(options.Host, trace)) return null;

        var certificates = new StealthCertificate(
            options.Host, options.CertificateUrl, options.CertificateCachePath, trace);

        var certificate = await certificates.TryLoadAsync(options.EmbeddedCertificate, cancellationToken);
        if (certificate is null) return null;

        // The marker lives beside the certificate cache, so "greeted" survives a restart the same
        // way the certificate does.
        var root = Path.GetDirectoryName(options.CertificateCachePath) ?? ".";

        var chat = new ChatProxy(
            certificate, mode, lobbyChat, () => StealthGreeting.ClaimFirstRun(root), trace);

        if (!chat.TryStart())
        {
            chat.Dispose();
            return null;
        }

        var config = new ClientConfigProxy(options.Host, () => chat.Port, trace);

        if (!config.TryStart())
        {
            config.Dispose();
            chat.Dispose();
            return null;
        }

        trace?.Invoke("stealth ready");
        return new StealthSession(config, chat, trace);
    }

    /// <summary>
    /// Checks the hostname still points at this machine.
    ///
    /// The name is public but its address record is 127.0.0.1, and some routers, ISP resolvers and
    /// filtering DNS services refuse to return an answer like that. Where they do, the client cannot
    /// reach the proxy and chat simply never connects — so one lookup up front turns a baffling
    /// silence into a sentence the user can act on.
    /// </summary>
    private static bool ResolvesLocally(string host, Action<string>? trace)
    {
        try
        {
            var addresses = Dns.GetHostAddresses(host);

            if (addresses.Any(IPAddress.IsLoopback)) return true;

            trace?.Invoke("stealth unavailable: " + host + " does not resolve to this machine. "
                          + "Some DNS providers will not return that answer; try 1.1.1.1 or 8.8.8.8.");
            return false;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            trace?.Invoke("stealth unavailable: could not resolve " + host
                          + " (" + ex.GetType().Name + ")");
            return false;
        }
    }

    public void Dispose()
    {
        _config.ChatServerResolved -= _chat.UseUpstream;

        _config.Dispose();
        _chat.Dispose();

        _trace?.Invoke("stealth stopped");
    }
}

/// <summary>
/// Where the certificate and its hostname come from.
///
/// These are build constants rather than user settings: the hostname is baked into the certificate,
/// and a user who changed either would simply break the feature.
/// </summary>
public sealed record StealthOptions(
    string Host,
    string CertificateUrl,
    string CertificateCachePath,
    byte[]? EmbeddedCertificate);
