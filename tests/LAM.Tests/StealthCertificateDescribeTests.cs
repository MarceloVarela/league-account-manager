using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LAM.Core.Stealth;
using Xunit;

namespace LAM.Tests;

/// <summary>
/// Pins <see cref="StealthCertificate.Describe"/> — the local, no-network status read the Settings
/// pane shows. Uses throwaway self-signed certificates with chosen expiry dates, so it needs neither
/// the real shipped pfx nor a network, and the Valid/ExpiringSoon/Expired/Missing thresholds cannot
/// drift unnoticed.
/// </summary>
public sealed class StealthCertificateDescribeTests
{
    private const string Host = "unit.test.example";

    private static byte[] SelfSigned(string host, DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=" + host, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());

        using var cert = request.CreateSelfSigned(notAfter - TimeSpan.FromDays(90), notAfter);
        return cert.Export(X509ContentType.Pkcs12);
    }

    private static string NoSuchPath()
        => Path.Combine(Path.GetTempPath(), "lam-no-cache-" + Guid.NewGuid().ToString("N") + ".pfx");

    [Fact]
    public void No_certificate_anywhere_is_Missing()
    {
        var info = StealthCertificate.Describe(embedded: null, NoSuchPath(), Host);

        Assert.Equal(StealthCertificateStatus.Missing, info.Status);
        Assert.Null(info.NotAfter);
    }

    [Fact]
    public void A_certificate_well_before_expiry_is_Valid()
    {
        var info = StealthCertificate.Describe(SelfSigned(Host, DateTimeOffset.Now.AddDays(60)), NoSuchPath(), Host);

        Assert.Equal(StealthCertificateStatus.Valid, info.Status);
        Assert.NotNull(info.NotAfter);
    }

    [Fact]
    public void A_certificate_inside_the_20_day_window_is_ExpiringSoon()
    {
        var info = StealthCertificate.Describe(SelfSigned(Host, DateTimeOffset.Now.AddDays(10)), NoSuchPath(), Host);

        Assert.Equal(StealthCertificateStatus.ExpiringSoon, info.Status);
    }

    [Fact]
    public void A_past_certificate_is_Expired()
    {
        var info = StealthCertificate.Describe(SelfSigned(Host, DateTimeOffset.Now.AddDays(-1)), NoSuchPath(), Host);

        Assert.Equal(StealthCertificateStatus.Expired, info.Status);
    }

    [Fact]
    public void A_certificate_for_a_different_host_does_not_count()
    {
        // Issued for someone else, so it cannot authenticate our hostname — unusable, i.e. Missing.
        var info = StealthCertificate.Describe(SelfSigned("other.example", DateTimeOffset.Now.AddDays(60)), NoSuchPath(), Host);

        Assert.Equal(StealthCertificateStatus.Missing, info.Status);
    }

    [Fact]
    public void The_later_of_embedded_and_cached_wins()
    {
        var cache = NoSuchPath();
        File.WriteAllBytes(cache, SelfSigned(Host, DateTimeOffset.Now.AddDays(5)));   // cached: expiring soon
        try
        {
            var embedded = SelfSigned(Host, DateTimeOffset.Now.AddDays(60));          // embedded: healthy
            var info = StealthCertificate.Describe(embedded, cache, Host);

            // Newest valid wins, so the healthy embedded one determines the status.
            Assert.Equal(StealthCertificateStatus.Valid, info.Status);
        }
        finally
        {
            File.Delete(cache);
        }
    }
}
