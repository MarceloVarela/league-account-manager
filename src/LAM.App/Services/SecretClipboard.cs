using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace LAM.App.Services;

/// <summary>
/// Copies a secret to the clipboard so that it does not outlive its use.
///
/// Two problems with a plain <c>Clipboard.SetText</c> for a password:
///
///  * Windows clipboard history keeps it, and Cloud Clipboard will happily sync it to other machines
///    signed into the same Microsoft account. A password from an encrypted local vault would then
///    exist, in plaintext, somewhere the vault has no say over.
///  * It stays there until something else replaces it, which may be hours.
///
/// Both are addressed here: the two documented exclusion formats tell Windows not to record or roam
/// the entry, and the clipboard is cleared afterwards — but only if the secret is still the thing on
/// it, so clearing never destroys something the user copied in the meantime.
/// </summary>
public static class SecretClipboard
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(45);

    /// <summary>Documented format names Windows honours to keep an entry out of history and the cloud.</summary>
    private const string ExcludeFromHistory = "ExcludeClipboardContentFromMonitorProcessing";
    private const string CanIncludeInHistory = "CanIncludeInClipboardHistory";
    private const string CanUploadToCloud = "CanUploadToCloudClipboard";

    /// <summary>
    /// Puts a secret on the clipboard, excluded from history, and clears it after a delay.
    ///
    /// Returns false if the clipboard was unavailable — another process can hold it open, and that
    /// is a normal Windows condition rather than an error worth throwing over.
    /// </summary>
    public static bool CopySecret(string secret, TimeSpan? lifetime = null)
    {
        if (!TrySetSecret(secret)) return false;

        var window = lifetime ?? DefaultLifetime;
        _ = ClearAfterAsync(secret, window);

        return true;
    }

    private static bool TrySetSecret(string secret)
    {
        try
        {
            var data = new DataObject();
            data.SetText(secret);

            // Any non-empty value works; Windows checks for the format's presence, not its contents.
            data.SetData(ExcludeFromHistory, true);
            data.SetData(CanIncludeInHistory, false);
            data.SetData(CanUploadToCloud, false);

            Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Clears the clipboard, but only while it still holds the secret.
    ///
    /// The check matters: between copying a password and this firing, the user may well have copied
    /// something else, and wiping that would be a small betrayal every time it happened.
    /// </summary>
    private static async Task ClearAfterAsync(string secret, TimeSpan lifetime)
    {
        await Task.Delay(lifetime);

        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!Clipboard.ContainsText()) return;
                if (Clipboard.GetText() != secret) return;

                Clipboard.Clear();
            });
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                       or InvalidOperationException
                                       or TaskCanceledException)
        {
            // A clipboard we cannot read is one we should not clear.
        }
    }
}
