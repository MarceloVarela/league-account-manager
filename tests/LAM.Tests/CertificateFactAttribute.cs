using Xunit;

namespace LAM.Tests;

/// <summary>
/// A fact that runs only when the stealth certificate is present, and is reported as *skipped* when
/// it is not.
///
/// The certificate is git-ignored, so a contributor's clone does not have it, and these gates are a
/// maintainer check against shipping an expired one — not something a contributor can satisfy. The
/// obvious workaround, an early `return` when the file is missing, is worse than it looks: xunit
/// reports that as a **pass**. An expiry gate that turns green precisely because the thing it guards
/// is absent is the failure mode it exists to prevent, and it would have hidden a lapsed certificate
/// on any machine where the file went missing.
///
/// Setting <see cref="FactAttribute.Skip"/> makes the runner say so out loud instead.
/// </summary>
public sealed class CertificateFactAttribute : FactAttribute
{
    public CertificateFactAttribute()
    {
        if (!File.Exists(StealthCertificatePath.Value))
            Skip = "No stealth certificate in this checkout — maintainer-only gate.";
    }
}

/// <summary>Where the shipped certificate lives, resolved once from the repository root.</summary>
public static class StealthCertificatePath
{
    public static string Value { get; } = Resolve();

    private static string Resolve()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory, "src", "LAM.App")))
                return Path.Combine(directory, "src", "LAM.App", "Assets", "stealth.pfx");

            directory = Path.GetDirectoryName(directory);
        }

        // No repository root above us: report a path that does not exist, so the gates skip rather
        // than throwing out of an attribute constructor, which xunit surfaces very poorly.
        return Path.Combine(AppContext.BaseDirectory, "stealth.pfx");
    }
}
