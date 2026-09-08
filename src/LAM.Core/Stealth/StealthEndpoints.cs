namespace LAM.Core.Stealth;

/// <summary>
/// The hostname and certificate this build is pinned to.
///
/// Build constants, not settings. The hostname is baked into the certificate, so the two travel
/// together and a user who changed either would only break the feature.
///
/// SETUP, once, before stealth can work at all:
///
///   1. Register a domain for the tool and add an A record for <see cref="Host"/> pointing at
///      127.0.0.1. If it sits behind a CDN it must be DNS-only, or the address gets rewritten.
///   2. Issue a certificate with a DNS-01 challenge — HTTP-01 cannot work, because the name resolves
///      to the user's own machine and the authority can never reach it.
///   3. Never a wildcard. One exact hostname, so the shipped private key can only ever vouch for a
///      name that points at the user's own machine and nothing else.
///   4. Export the PKCS#12 to <c>Assets/stealth.pfx</c> and publish the same file at
///      <see cref="CertificateUrl"/>.
///
/// Then every ~90 days: reissue and re-publish. Users pick it up without updating the app, which is
/// the entire reason the fetch path exists. Until this is done, stealth simply reports itself
/// unavailable and sign-in proceeds normally.
/// </summary>
public static class StealthEndpoints
{
    /// <summary>The name the certificate is issued for. Its A record must be 127.0.0.1.</summary>
    public const string Host = "localhost.marcrake.lol";

    /// <summary>Where a fresher certificate is published when the shipped one nears expiry.</summary>
    public const string CertificateUrl =
        "https://github.com/MarceloVarela/league-account-manager/releases/latest/download/stealth.pfx";

    /// <summary>
    /// Whether this build has somewhere to get a certificate from.
    ///
    /// Only says the constants are pointed at something real — not that the certificate exists, is
    /// current, or that the hostname resolves. Those are runtime questions, answered by
    /// StealthSession, and every one of them fails open.
    /// </summary>
    public static bool Configured =>
        Host.Length > 0 && CertificateUrl.StartsWith("https://", StringComparison.Ordinal);
}
