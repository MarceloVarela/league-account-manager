using System.Security.Cryptography;
using System.Text;
using LAM.Core.Model;

namespace LAM.Core.Vault;

public enum HealthSeverity { Info, Warning, Serious }

/// <summary>One thing worth knowing about the state of the vault.</summary>
public sealed record HealthFinding(HealthSeverity Severity, string Title, string Detail)
{
    public override string ToString() => Title + " — " + Detail;
}

/// <summary>
/// Answers "what is wrong with my vault right now", in one place.
///
/// The risk profile here is unusual: dozens of credentials for one game, in one file, on one
/// machine. That makes two things matter far more than they would for a general password manager —
/// a shared password across smurfs (one credential-stuffing hit takes the whole set) and a backup
/// that lives beside the thing it is backing up.
///
/// Everything is computed locally and in memory. Nothing is hashed to disk, nothing is looked up
/// online, and no finding ever quotes a secret.
/// </summary>
public static class VaultHealth
{
    /// <summary>A password unchanged for longer than this is worth mentioning, not alarming about.</summary>
    public static readonly TimeSpan PasswordAgeThreshold = TimeSpan.FromDays(365);

    public static IReadOnlyList<HealthFinding> Inspect(
        VaultDocument document,
        IReadOnlyList<FileInfo> backups,
        DateTimeOffset nowUtc,
        string? backupDirectory = null,
        string? vaultDirectory = null)
    {
        var findings = new List<HealthFinding>();
        var accounts = document.Live.ToList();

        findings.AddRange(ReusedPasswords(accounts));
        findings.AddRange(MissingCredentials(accounts));
        findings.AddRange(BackupFindings(backups, nowUtc, backupDirectory, vaultDirectory));
        findings.AddRange(TrashFindings(document));

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Title, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Accounts sharing one password.
    ///
    /// Compared by hash held only in memory — the point is to compare without ever assembling a list
    /// of plaintext passwords, which would be a worse artefact than the problem it is describing.
    ///
    /// This is the single most likely way a collection of smurfs is lost at once: one leaked
    /// email/password pair, tried everywhere, and every account sharing it is gone together.
    /// </summary>
    public static IReadOnlyList<HealthFinding> ReusedPasswords(IReadOnlyList<AccountEntry> accounts)
    {
        var groups = new Dictionary<string, List<AccountEntry>>(StringComparer.Ordinal);

        foreach (var account in accounts)
        {
            if (account.Password is not { IsEmpty: false } password) continue;

            var key = Fingerprint(password.Value);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(account);
        }

        return groups.Values
            .Where(group => group.Count > 1)
            .OrderByDescending(group => group.Count)
            .Select(group => new HealthFinding(
                HealthSeverity.Serious,
                group.Count + " accounts share one password",
                string.Join(", ", group.Select(a => a.DisplayRiotId))
                + ". One leaked password takes all of them together — the usual way a set of smurfs "
                + "is lost at once."))
            .ToList();
    }

    /// <summary>
    /// A stable, non-reversible fingerprint used only for equality comparison in memory.
    ///
    /// Not a password hash in the storage sense and deliberately never written anywhere: it exists
    /// so two passwords can be compared without both being held as plaintext in a list.
    /// </summary>
    private static string Fingerprint(string password)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));

    private static IEnumerable<HealthFinding> MissingCredentials(IReadOnlyList<AccountEntry> accounts)
    {
        var stranded = accounts
            .Where(a => a.Password is null or { IsEmpty: true } && a.Session is null)
            .ToList();

        if (stranded.Count > 0)
        {
            yield return new HealthFinding(
                HealthSeverity.Warning,
                stranded.Count + " accounts cannot be signed into",
                string.Join(", ", stranded.Select(a => a.DisplayRiotId))
                + ". No stored password and no saved session, so the sign-in button does nothing.");
        }

        var noEmail = accounts.Count(a => string.IsNullOrWhiteSpace(a.Recovery.Email));
        if (noEmail > 0)
        {
            yield return new HealthFinding(
                HealthSeverity.Warning,
                noEmail + " accounts have no recovery email recorded",
                "It is the single field Riot Support leans on hardest, and the hardest to reconstruct "
                + "years later.");
        }

        var lostMailbox = accounts.Where(a => !a.Recovery.EmailStillControlled).ToList();
        if (lostMailbox.Count > 0)
        {
            yield return new HealthFinding(
                HealthSeverity.Serious,
                lostMailbox.Count + " accounts are flagged as having an unreachable mailbox",
                string.Join(", ", lostMailbox.Select(a => a.DisplayRiotId))
                + ". Recovery normally runs through that mailbox, so these are the accounts most "
                + "likely to be unrecoverable if anything goes wrong.");
        }
    }

    /// <summary>
    /// Whether the backups would survive the thing they exist for.
    ///
    /// A rolling backup beside the vault protects against a bad write and nothing else — not a failed
    /// disk, not ransomware, not a wiped profile. Saying so is the whole value of the check.
    /// </summary>
    public static IEnumerable<HealthFinding> BackupFindings(
        IReadOnlyList<FileInfo> backups,
        DateTimeOffset nowUtc,
        string? backupDirectory,
        string? vaultDirectory)
    {
        if (backups.Count == 0)
        {
            yield return new HealthFinding(
                HealthSeverity.Warning,
                "No backups yet",
                "One is taken automatically before each save, so this clears itself the next time "
                + "anything changes.");
            yield break;
        }

        var newest = backups.Max(b => b.LastWriteTimeUtc);
        var age = nowUtc - new DateTimeOffset(newest, TimeSpan.Zero);

        if (age > TimeSpan.FromDays(30))
        {
            yield return new HealthFinding(
                HealthSeverity.Info,
                "Newest backup is " + (int)age.TotalDays + " days old",
                "Nothing has changed in a while, which is fine — worth knowing rather than acting on.");
        }

        if (backupDirectory is not null && vaultDirectory is not null && SameVolume(backupDirectory, vaultDirectory))
        {
            yield return new HealthFinding(
                HealthSeverity.Warning,
                "Backups live on the same drive as the vault",
                "They protect against a bad write, not against a failed disk, ransomware or a wiped "
                + "profile — all of which would take the vault and every backup together. Copy the "
                + "backup folder somewhere else occasionally.");
        }
    }

    private static bool SameVolume(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetPathRoot(Path.GetFullPath(a)),
                Path.GetPathRoot(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static IEnumerable<HealthFinding> TrashFindings(VaultDocument document)
    {
        var trashed = document.Trashed.ToList();
        if (trashed.Count == 0) yield break;

        yield return new HealthFinding(
            HealthSeverity.Info,
            trashed.Count + " accounts are in the trash",
            "Hidden from the grid but still stored, passwords and all. Restore or purge them from "
            + "the Trash tab.");
    }
}
