using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Exporter.Services;

/// <summary>
/// Reads <see cref="Guid"/> values in any of the forms Jellyfin uses.
/// </summary>
/// <remarks>
/// Jellyfin serializes ids without dashes (<c>e65358ead88d9d5e9625964773399452</c>) in its
/// configuration and API payloads, while <see cref="JsonSerializer"/>'s built-in Guid
/// converter only reads the dashed form. A job history written by another component, or hand
/// edited by an operator, would therefore fail to load and be quarantined even though every id
/// in it is valid. This converter accepts both forms (and writes the dashed form).
/// </remarks>
public sealed class FlexibleGuidJsonConverter : JsonConverter<Guid>
{
    /// <inheritdoc />
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a GUID string but found {reader.TokenType}.");
        }

        var raw = reader.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Guid.Empty;
        }

        if (Guid.TryParse(raw, out var parsed) || Guid.TryParseExact(raw, "N", out parsed))
        {
            return parsed;
        }

        throw new JsonException($"'{raw}' is not a valid GUID.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString("D"));
    }
}
