using System.Globalization;
using System.Text.Json;

namespace StarfrontCollab.Wire;

/// The server sent something this plugin cannot read.
public sealed class WireException(string message) : Exception(message);

/// Bounded, tolerant readers over server JSON. Unknown fields are ignored;
/// a known field with the wrong shape is refused rather than guessed at.
internal static class Json
{
    internal const int MaxBodyBytes = 262_144;
    private const int MaxValues = 32_768;

    internal static JsonDocument Parse(ReadOnlyMemory<byte> bytes)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { throw new WireException("The server sent JSON this plugin cannot read."); }
        try
        {
            var count = 0;
            Check(document.RootElement, ref count);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static void Check(JsonElement value, ref int count)
    {
        if (++count > MaxValues) throw new WireException("The server's reply is too large to read.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new WireException($"The server repeated the '{property.Name}' field.");
                Check(property.Value, ref count);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) Check(item, ref count);
        }
    }

    internal static JsonElement? Field(this JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var result)
            && result.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? result : null;

    internal static string Text(this JsonElement value, string key, int maximum) =>
        value.OptText(key, maximum) ?? throw Bad(key);

    internal static string? OptText(this JsonElement value, string key, int maximum)
    {
        if (value.Field(key) is not { } field) return null;
        if (field.ValueKind != JsonValueKind.String) throw Bad(key);
        var text = field.GetString()!;
        if (text.Length > maximum || text.Any(char.IsControl)) throw Bad(key);
        return text;
    }

    internal static double Number(this JsonElement value, string key) => value.OptNumber(key) ?? throw Bad(key);

    internal static double? OptNumber(this JsonElement value, string key) =>
        value.Field(key) is { } field ? AsNumber(field) ?? throw Bad(key) : null;

    internal static double? AsNumber(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDouble(out var number) && double.IsFinite(number) => number,
        JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number) => number,
        _ => null
    };

    internal static int? OptInt(this JsonElement value, string key)
    {
        if (value.OptNumber(key) is not { } number) return null;
        return AsInt(number) ?? throw Bad(key);
    }

    internal static int? AsInt(double number) =>
        number == Math.Floor(number) && number is >= int.MinValue and <= int.MaxValue ? (int)number : null;

    internal static bool? OptBool(this JsonElement value, string key) => value.Field(key) switch
    {
        null => null,
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => throw Bad(key)
    };

    internal static JsonElement? OptObject(this JsonElement value, string key) => value.Field(key) switch
    {
        null => null,
        { ValueKind: JsonValueKind.Object } field => field,
        _ => throw Bad(key)
    };

    internal static JsonElement[] OptArray(this JsonElement value, string key, int maximum)
    {
        if (value.Field(key) is not { } field) return [];
        if (field.ValueKind != JsonValueKind.Array || field.GetArrayLength() > maximum) throw Bad(key);
        return [.. field.EnumerateArray()];
    }

    internal static WireException Bad(string key) => new($"The server's '{key}' field is not readable.");
}
