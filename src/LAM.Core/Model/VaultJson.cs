using System.Text.Json;
using System.Text.Json.Serialization;

namespace LAM.Core.Model;

/// <summary>The two serialisation modes for vault data, so no call site has to choose by hand.</summary>
public static class VaultJson
{
    /// <summary>
    /// For the encrypted vault body. Compact, and secrets are written in full — which is fine
    /// because the output never leaves <see cref="Vault.VaultCrypto.Encrypt"/> unencrypted.
    /// </summary>
    public static readonly JsonSerializerOptions Storage = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// For anything a human or a log file might see. Every <see cref="SecretText"/> is masked.
    /// Use this for support bundles, debug dumps and error reports.
    /// </summary>
    public static readonly JsonSerializerOptions Redacted = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(), new SecretTextConverter(redact: true) },
    };
}
