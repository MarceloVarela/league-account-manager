namespace LAM.Core.Stealth;

/// <summary>
/// Remembers that the fake friend has already introduced itself.
///
/// The introduction is worth sending exactly once, because otherwise nobody discovers that the friend
/// is interactive at all. It is worth sending ONLY once, because the client reopens its chat
/// connection regularly — on a reconnect, a client restart, a network blip — and a greeting tied to
/// the connection therefore arrives again and again for something the user already knows.
///
/// A marker file beside the vault, the same shape as <see cref="ThemePreference"/> and
/// <see cref="LockScreenFacts"/>: existence is the whole value. Deleting it makes the friend
/// introduce itself once more, which is the obvious way to get it back.
/// </summary>
public static class StealthGreeting
{
    private const string FileName = "stealth-greeted";

    /// <summary>Whether the introduction still needs sending, marking it sent if so.</summary>
    public static bool ClaimFirstRun(string vaultRoot)
    {
        try
        {
            var path = Path.Combine(vaultRoot, FileName);
            if (File.Exists(path)) return false;

            Directory.CreateDirectory(vaultRoot);
            File.WriteAllText(path, string.Empty);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            // If the marker cannot be written it would be sent again on the next connection, which is
            // the exact behaviour being fixed. Staying quiet is the better failure.
            return false;
        }
    }
}
