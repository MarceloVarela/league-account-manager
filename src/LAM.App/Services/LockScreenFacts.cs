using System;
using System.Globalization;
using System.IO;

namespace LAM.App.Services;

/// <summary>
/// The two non-secret numbers the lock screen wants to show before anything is decrypted: how many
/// accounts are stored, and how long the idle timer is.
///
/// Both live in <c>AppSettings</c> / <c>VaultDocument</c>, inside the encrypted body — unreadable at
/// exactly the moment the lock panel is drawn. So they are mirrored into a plain file beside the
/// vault, the same trick and for the same reason as <see cref="ThemePreference"/>.
///
/// Neither is a secret. The account count is on screen a second after unlocking and the idle interval
/// is a setting the user chose; what the file deliberately does NOT contain is any account name,
/// region or identifier.
///
/// The vault stays authoritative. A missing, stale or unreadable file degrades the copy — the panel
/// omits the count and falls back to the default interval — and never blocks unlocking.
/// </summary>
public static class LockScreenFacts
{
    private const string FileName = "lockfacts";

    public readonly record struct Facts(int AccountCount, int IdleMinutes);

    public static Facts Read(string vaultRoot)
    {
        try
        {
            var path = Path.Combine(vaultRoot, FileName);
            if (!File.Exists(path)) return new Facts(0, 0);

            var parts = File.ReadAllText(path).Trim().Split(' ');

            return new Facts(
                parts.Length > 0 && int.TryParse(parts[0], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var count) ? count : 0,
                parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var minutes) ? minutes : 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Facts(0, 0);
        }
    }

    public static void Write(string vaultRoot, int accountCount, int idleMinutes)
    {
        try
        {
            Directory.CreateDirectory(vaultRoot);

            File.WriteAllText(
                Path.Combine(vaultRoot, FileName),
                accountCount.ToString(CultureInfo.InvariantCulture) + " "
                + idleMinutes.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cosmetic line on the lock screen is never worth failing anything else for.
        }
    }
}
