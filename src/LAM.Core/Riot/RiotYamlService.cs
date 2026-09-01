using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace LAM.Core.Riot;

/// <summary>
/// Reads and writes the two Riot YAML files we care about, without disturbing anything else in them.
///
/// <c>RiotClientSettings.yaml</c> carries around sixty unrelated keys — patch-note hashes, telemetry
/// opt-outs, A/B cohorts, saved UI state. Clobbering it resets the client, so this works on
/// YamlDotNet's representation model: load the document, change the two nodes we own, write the
/// same tree back. Keys we have never heard of survive untouched, including ones Riot adds later.
/// </summary>
public sealed class RiotYamlService
{
    private readonly RiotPaths _paths;
    private readonly string _backupDirectory;

    public RiotYamlService(RiotPaths paths, string backupDirectory)
    {
        _paths = paths;
        _backupDirectory = backupDirectory;
    }

    // ---- document helpers -------------------------------------------------

    public static YamlMappingNode? LoadMapping(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var text = File.ReadAllText(path);
            return ParseMapping(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static YamlMappingNode? ParseMapping(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count == 0) return null;
            return stream.Documents[0].RootNode as YamlMappingNode;
        }
        catch (YamlException)
        {
            return null;
        }
    }

    public static string Serialise(YamlMappingNode root)
    {
        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);

        // YamlStream.Save appends an explicit end-of-document marker. Riot's own writer does not
        // emit one, and while a parser accepts it, keeping the file shaped the way the client wrote
        // it avoids surprises.
        var text = writer.ToString().Replace("...\r\n", string.Empty).Replace("...\n", string.Empty);
        return text.TrimEnd() + Environment.NewLine;
    }

    /// <summary>Reads a nested scalar, e.g. ["install", "globals", "region"].</summary>
    public static string? GetScalar(YamlMappingNode root, params string[] path)
    {
        YamlNode current = root;
        foreach (var key in path)
        {
            if (current is not YamlMappingNode mapping) return null;
            if (!mapping.Children.TryGetValue(new YamlScalarNode(key), out var next)) return null;
            current = next;
        }
        return (current as YamlScalarNode)?.Value;
    }

    /// <summary>
    /// Writes a nested scalar, creating intermediate mappings as needed. String values are emitted
    /// double-quoted to match how the client writes them, so the resulting diff stays minimal.
    /// </summary>
    public static void SetScalar(YamlMappingNode root, string? value, params string[] path)
    {
        if (path.Length == 0) throw new ArgumentException("A key path is required.", nameof(path));

        var current = root;
        for (var i = 0; i < path.Length - 1; i++)
        {
            var key = new YamlScalarNode(path[i]);
            if (current.Children.TryGetValue(key, out var existing) && existing is YamlMappingNode mapping)
            {
                current = mapping;
            }
            else
            {
                var created = new YamlMappingNode();
                current.Children[key] = created;
                current = created;
            }
        }

        var leaf = new YamlScalarNode(path[^1]);
        if (value is null)
        {
            current.Children[leaf] = new YamlScalarNode((string?)null) { Style = ScalarStyle.Plain };
        }
        else
        {
            current.Children[leaf] = new YamlScalarNode(value) { Style = ScalarStyle.DoubleQuoted };
        }
    }

    /// <summary>Writes a boolean-or-null leaf using plain style, as the client does.</summary>
    public static void SetPlain(YamlMappingNode root, string? literal, params string[] path)
    {
        SetScalar(root, literal, path);
        var current = root;
        for (var i = 0; i < path.Length - 1; i++)
            current = (YamlMappingNode)current.Children[new YamlScalarNode(path[i])];
        if (current.Children[new YamlScalarNode(path[^1])] is YamlScalarNode scalar)
            scalar.Style = ScalarStyle.Plain;
    }

    // ---- region / locale --------------------------------------------------

    /// <summary>
    /// Points the client at an account's region before launch, so a BR account and an NA account
    /// each open on the right server.
    /// </summary>
    public bool TrySetRegionAndLocale(string region, string locale, out string? error)
    {
        error = null;
        var path = _paths.ClientSettingsFile;

        var root = LoadMapping(path);
        if (root is null)
        {
            error = "Could not read RiotClientSettings.yaml; leaving region untouched.";
            return false;
        }

        BackupOnce(path);

        SetScalar(root, region, "install", "globals", "region");
        SetScalar(root, locale, "install", "globals", "locale");
        SetScalar(root, region, "install", "localization", "region");
        SetScalar(root, locale, "install", "localization", "locale");

        try
        {
            AtomicWrite(path, Serialise(root));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "Could not write RiotClientSettings.yaml: " + ex.Message;
            return false;
        }
    }

    public (string? Region, string? Locale) ReadRegionAndLocale()
    {
        var root = LoadMapping(_paths.ClientSettingsFile);
        if (root is null) return (null, null);
        return (GetScalar(root, "install", "globals", "region"),
                GetScalar(root, "install", "globals", "locale"));
    }

    // ---- session (private settings) --------------------------------------

    public string? ReadPrivateSettings()
    {
        try
        {
            return File.Exists(_paths.PrivateSettingsFile)
                ? File.ReadAllText(_paths.PrivateSettingsFile)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void WritePrivateSettings(string yaml)
    {
        Directory.CreateDirectory(_paths.RiotClientDataDirectory);
        BackupOnce(_paths.PrivateSettingsFile);
        AtomicWrite(_paths.PrivateSettingsFile, yaml);
    }

    /// <summary>
    /// Removes any stored session so the client presents its login form.
    ///
    /// Needed before an autofill login: with a live session the client boots straight to the lobby
    /// and there is nothing to type into. The device cookie (<c>tdid</c>) is deliberately preserved
    /// — it identifies the machine, not the account, and dropping it makes Riot treat every login
    /// as coming from a brand-new device and demand an emailed verification code.
    /// </summary>
    public void ClearSessionKeepingDevice()
    {
        var text = ReadPrivateSettings();
        if (text is null) return;

        var root = ParseMapping(text);
        if (root is null) return;

        SetPlain(root, null, "riot-login", "persist");

        // The refresh token is the session on this client. Leaving it behind would send the client
        // straight to the lobby with no form to type into — which is precisely the failure the
        // cookie-only version of this method could not cause, because it never knew where the
        // session actually lived.
        SetPlain(root, null, "psl", "authorization", "riot-client");

        if (root.Children.TryGetValue(new YamlScalarNode("rso-authenticator"), out var node)
            && node is YamlMappingNode authenticator)
        {
            var device = new YamlScalarNode("tdid");
            var keep = authenticator.Children.TryGetValue(device, out var tdid) ? tdid : null;

            authenticator.Children.Clear();
            if (keep is not null) authenticator.Children[device] = keep;
        }

        WritePrivateSettings(Serialise(root));
    }

    /// <summary>Marks the client as willing to persist the next login ("Stay signed in").</summary>
    public void EnableSessionPersistence()
    {
        var text = ReadPrivateSettings();
        var root = text is null ? new YamlMappingNode() : ParseMapping(text) ?? new YamlMappingNode();
        SetPlain(root, "true", "riot-login", "persist");
        WritePrivateSettings(Serialise(root));
    }

    /// <summary>
    /// Where the modern client keeps its session: an OAuth refresh token, not browser cookies.
    ///
    /// This was originally written against <c>rso-authenticator</c> cookies, which was a guess made
    /// while the machine happened to be signed out — and a signed-out file shows
    /// <c>psl.authorization.riot-client: null</c>, which is indistinguishable from "this client does
    /// not persist sessions". A real sign-in settled it: the file grew from 491 to 3822 bytes and
    /// gained a refresh token under <c>psl.authorization.riot-client</c>, while the cookie section
    /// still held nothing but the device id.
    /// </summary>
    private static readonly string[] AuthorizationPath = ["psl", "authorization", "riot-client"];

    /// <summary>
    /// Legacy cookie names. Kept as a secondary check so an older client still works.
    /// <c>tdid</c> is deliberately excluded — it identifies the machine and is present even when
    /// signed out, so treating it as a session would launch an unauthenticated client and call it
    /// success.
    /// </summary>
    public static readonly string[] SessionCookieNames = ["ssid", "clid", "sub", "csid"];

    /// <summary>True when the YAML carries a session worth saving.</summary>
    public static bool ContainsSession(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return false;

        var root = ParseMapping(yaml);
        return root is not null && (HasRefreshToken(root) || HasSessionCookie(root));
    }

    private static bool HasRefreshToken(YamlMappingNode root)
        => !string.IsNullOrWhiteSpace(GetScalar(root, "psl", "authorization", "riot-client", "refresh_token"));

    /// <summary>
    /// A DPoP-bound token is tied to a key held by the client that obtained it, so restoring it
    /// elsewhere cannot work. Observed as <c>false</c> on this client, but worth refusing rather than
    /// relaunching into a silent failure if that ever changes.
    /// </summary>
    public static bool IsDeviceBound(string? yaml)
    {
        var root = yaml is null ? null : ParseMapping(yaml);
        if (root is null) return false;

        var bound = GetScalar(root, "psl", "authorization", "riot-client", "is_dpop_bound");
        return string.Equals(bound?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSessionCookie(YamlMappingNode root)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("rso-authenticator"), out var node)
            || node is not YamlMappingNode authenticator)
        {
            return false;
        }

        return SessionCookieNames.Any(name =>
        {
            if (!authenticator.Children.TryGetValue(new YamlScalarNode(name), out var cookie)) return false;
            if (cookie is not YamlMappingNode mapping) return false;
            var value = (mapping.Children.TryGetValue(new YamlScalarNode("value"), out var v)
                ? v as YamlScalarNode : null)?.Value;
            return !string.IsNullOrWhiteSpace(value);
        });
    }

    /// <summary>Describes what is stored, for the diagnostics screens.</summary>
    public static IReadOnlyList<string> DescribeCookies(string? yaml)
    {
        var root = yaml is null ? null : ParseMapping(yaml);
        if (root is null) return [];

        var found = new List<string>();

        if (HasRefreshToken(root)) found.Add("refresh_token");
        if (!string.IsNullOrWhiteSpace(GetScalar(root, "psl", "authorization", "riot-client", "id_token")))
            found.Add("id_token");

        if (root.Children.TryGetValue(new YamlScalarNode("rso-authenticator"), out var node)
            && node is YamlMappingNode authenticator)
        {
            found.AddRange(authenticator.Children.Keys
                .OfType<YamlScalarNode>()
                .Select(k => k.Value ?? string.Empty)
                .Where(k => k.Length > 0));
        }

        return found.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Writes a saved session into the live file while keeping this machine's device cookie.
    ///
    /// Only the authorisation block travels with the account. <c>tdid</c> identifies the *machine*,
    /// and carrying one account's copy across would make Riot see a different device per account —
    /// which means an emailed verification code on every switch.
    /// </summary>
    public void RestoreSession(string savedYaml)
    {
        var saved = ParseMapping(savedYaml);
        if (saved is null) return;

        var liveText = ReadPrivateSettings();
        var live = liveText is null ? null : ParseMapping(liveText);

        if (live is null)
        {
            // Nothing to merge into; the saved file carries its own device id and is better than
            // refusing to sign in.
            WritePrivateSettings(savedYaml);
            return;
        }

        var pslKey = new YamlScalarNode("psl");
        if (saved.Children.TryGetValue(pslKey, out var savedPsl))
            live.Children[pslKey] = savedPsl;

        SetPlain(live, "true", "riot-login", "persist");

        WritePrivateSettings(Serialise(live));
    }

    // ---- file plumbing ----------------------------------------------------

    /// <summary>
    /// Copies a Riot file aside the first time we are about to modify it, and never again — so the
    /// backup is always the pristine original rather than whatever we last wrote.
    /// </summary>
    private void BackupOnce(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            Directory.CreateDirectory(_backupDirectory);

            var target = Path.Combine(_backupDirectory, Path.GetFileName(path) + ".original");
            if (File.Exists(target)) return;

            File.Copy(path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing safety copy must not stop the launch it was guarding.
        }
    }

    private static void AtomicWrite(string path, string contents)
    {
        var tmp = path + ".lam-tmp";
        File.WriteAllText(tmp, contents);

        if (File.Exists(path))
            File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else
            File.Move(tmp, path);
    }
}
