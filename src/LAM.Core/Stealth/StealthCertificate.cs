using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace LAM.Core.Stealth;

/// <summary>
/// Supplies the certificate the chat proxy presents to the League client.
///
/// The client validates it completely normally — there is no bypass, and there used to be: Riot
/// honoured a <c>chat.allow_bad_cert.enabled</c> configuration key until April 2026, which let a
/// self-signed certificate work. That stopped being honoured, silently, and the symptom was an empty
/// friends list. So a genuinely trusted certificate is now the only route.
///
/// The trick is therefore in DNS, not in PKI: a real public hostname whose A record points at
/// <c>127.0.0.1</c>, and a real Let's Encrypt certificate for it. The client resolves the name to the
/// user's own machine, connects, sees a certificate signed by a public authority for exactly the name
/// it asked for, and is satisfied. Nothing is forged.
///
/// The private key ships with the app and is therefore public. That is acceptable only because the
/// name it vouches for can never resolve anywhere but loopback — it cannot be used to intercept
/// anything else. It must never be a wildcard, for the same reason.
///
/// Two sources, newest valid wins: one embedded in the build so a fresh install works offline, and one
/// cached from a URL so a ~90-day renewal reaches users who never update the app.
/// </summary>
public sealed class StealthCertificate
{
    /// <summary>Let's Encrypt issues for 90 days; refresh well before the edge.</summary>
    private static readonly TimeSpan RefreshWhenUnder = TimeSpan.FromDays(20);

    private readonly string _expectedHost;
    private readonly string _refreshUrl;
    private readonly string _cachePath;
    private readonly Action<string>? _trace;
    private readonly HttpClient _http;

    public StealthCertificate(string expectedHost, string refreshUrl, string cachePath,
        Action<string>? trace = null, HttpClient? http = null)
    {
        _expectedHost = expectedHost;
        _refreshUrl = refreshUrl;
        _cachePath = cachePath;
        _trace = trace;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>
    /// The best certificate available, or null when there is none usable.
    ///
    /// Null is a normal answer and means stealth is unavailable this session — never a failed sign-in.
    /// </summary>
    public async Task<SslStreamCertificateContext?> TryLoadAsync(
        byte[]? embedded, CancellationToken cancellationToken)
    {
        var best = Best(Read(embedded, "embedded"), Read(ReadCache(), "cached"));

        if (best is not null && best.Value.Leaf.NotAfter - DateTime.Now > RefreshWhenUnder)
            return Context(best.Value);

        _trace?.Invoke("stealth certificate is missing or expiring; fetching a fresh one");

        if (await FetchAsync(cancellationToken) is { } fetched)
        {
            var parsed = Read(fetched, "downloaded");
            if (parsed is not null)
            {
                WriteCache(fetched);
                best = Best(best, parsed);
            }
        }

        if (best is null)
        {
            _trace?.Invoke("no usable stealth certificate; signing in without stealth");
            return null;
        }

        if (best.Value.Leaf.NotAfter <= DateTime.Now)
        {
            _trace?.Invoke("stealth certificate expired on " + best.Value.Leaf.NotAfter.ToString("d MMM yyyy"));
            return null;
        }

        return Context(best.Value);
    }

    private static SslStreamCertificateContext Context((X509Certificate2 Leaf, X509Certificate2Collection All) source)
    {
        // Send the intermediates too. Loading only the leaf — which is what the reference
        // implementation does — leaves the client to build the chain itself from its own store or by
        // fetching the issuer, which is slower and fails on a machine that cannot do either.
        var extra = new X509Certificate2Collection();

        foreach (var certificate in source.All)
        {
            if (!certificate.Equals(source.Leaf)) extra.Add(certificate);
        }

        return SslStreamCertificateContext.Create(source.Leaf, extra, offline: true);
    }

    private static (X509Certificate2 Leaf, X509Certificate2Collection All)? Best(
        (X509Certificate2 Leaf, X509Certificate2Collection All)? left,
        (X509Certificate2 Leaf, X509Certificate2Collection All)? right)
    {
        if (left is null) return right;
        if (right is null) return left;

        return right.Value.Leaf.NotAfter > left.Value.Leaf.NotAfter ? right : left;
    }

    /// <summary>
    /// Parses a PKCS#12 blob and checks it is actually for us.
    ///
    /// The name check matters: the certificate is downloaded over the network, and a blob that is
    /// valid but issued for some other host would fail at TLS time with an error the user cannot act
    /// on. The reference implementation does not check this at all.
    /// </summary>
    private (X509Certificate2 Leaf, X509Certificate2Collection All)? Read(byte[]? blob, string origin)
    {
        if (blob is null || blob.Length == 0) return null;

        try
        {
            // X509CertificateLoader, not the constructor: the constructor is obsolete on .NET 9.
            var all = X509CertificateLoader.LoadPkcs12Collection(
                blob, password: null, X509KeyStorageFlags.DefaultKeySet);

            foreach (var certificate in all)
            {
                if (!certificate.HasPrivateKey) continue;
                if (!MatchesHost(certificate)) continue;

                return (certificate, all);
            }

            _trace?.Invoke("the " + origin + " stealth certificate is not for " + _expectedHost);
            return null;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException)
        {
            _trace?.Invoke("could not read the " + origin + " stealth certificate ("
                           + ex.GetType().Name + ")");
            return null;
        }
    }

    private bool MatchesHost(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (extension is not X509SubjectAlternativeNameExtension san) continue;

            foreach (var name in san.EnumerateDnsNames())
            {
                if (string.Equals(name, _expectedHost, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        return false;
    }

    private byte[]? ReadCache()
    {
        try
        {
            return File.Exists(_cachePath) ? File.ReadAllBytes(_cachePath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteCache(byte[] blob)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath) ?? ".");
            File.WriteAllBytes(_cachePath, blob);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written costs one download next time, nothing more.
        }
    }

    private async Task<byte[]?> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(_refreshUrl, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _trace?.Invoke("could not fetch a fresh stealth certificate (" + ex.GetType().Name + ")");
            return null;
        }
    }
}
