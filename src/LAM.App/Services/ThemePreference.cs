using System;
using System.IO;

namespace LAM.App.Services;

/// <summary>
/// Remembers the chosen palette outside the vault.
///
/// The vault-lock screen is the first thing drawn and the last thing that could read an encrypted
/// setting — so the theme was only applied at unlock, and every dark-theme user watched the app open
/// light and flip. This is a single word in a plain file beside the vault: it reveals nothing (the
/// choice is visible on screen anyway) and it is readable before any key exists.
///
/// The vault's own <c>AppSettings.UseDarkTheme</c> stays authoritative; this is a mirror of it, and a
/// missing or unreadable file simply falls back to the default.
/// </summary>
public static class ThemePreference
{
    private const string FileName = "theme";

    public static AppTheme Read(string vaultRoot)
    {
        try
        {
            var path = Path.Combine(vaultRoot, FileName);
            if (!File.Exists(path)) return AppTheme.Dark;

            return File.ReadAllText(path).Trim()
                .Equals("light", StringComparison.OrdinalIgnoreCase)
                ? AppTheme.Light
                : AppTheme.Dark;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AppTheme.Dark;
        }
    }

    public static void Write(string vaultRoot, AppTheme theme)
    {
        try
        {
            Directory.CreateDirectory(vaultRoot);
            File.WriteAllText(Path.Combine(vaultRoot, FileName),
                theme == AppTheme.Light ? "light" : "dark");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A preference that cannot be saved is not worth failing a launch over.
        }
    }
}
