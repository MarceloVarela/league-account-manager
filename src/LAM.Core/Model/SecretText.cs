using System.Text.Json;
using System.Text.Json.Serialization;

namespace LAM.Core.Model;

/// <summary>
/// A string that must never reach a log, a crash dump, or an exported diagnostic.
///
/// The whole vault document is encrypted at rest, so this type is not what protects the secret on
/// disk — it protects it from *us*. <see cref="ToString"/> returns a mask, so an accidental
/// <c>$"user={entry.Password}"</c> emits <c>***</c> rather than the password, and serialising a
/// document with <see cref="VaultJson.Redacted"/> masks every one of these fields at once.
/// </summary>
[JsonConverter(typeof(SecretTextConverter))]
public sealed record SecretText(string Value)
{
    public const string Mask = "***";

    public static implicit operator SecretText(string value) => new(value);

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>Masked on purpose. Use <see cref="Value"/> when you genuinely need the secret.</summary>
    public override string ToString() => Mask;
}

/// <summary>
/// Writes a <see cref="SecretText"/> as its real value for storage, or as a mask for diagnostics.
///
/// The mode is baked into the converter *instance* rather than read from a flag at write time,
/// because System.Text.Json resolves <c>options.Converters</c> ahead of a type-level
/// <c>[JsonConverter]</c> attribute. So <see cref="VaultJson.Redacted"/> registers a redacting
/// instance and wins, while every other options object falls through to the attribute and stores
/// the real value. No call site can pick the wrong one by forgetting a parameter.
/// </summary>
public sealed class SecretTextConverter : JsonConverter<SecretText>
{
    private readonly bool _redact;

    public SecretTextConverter() : this(redact: false) { }

    public SecretTextConverter(bool redact) => _redact = redact;

    public override SecretText? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : new SecretText(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, SecretText value, JsonSerializerOptions options)
        => writer.WriteStringValue(_redact ? SecretText.Mask : value.Value);
}
