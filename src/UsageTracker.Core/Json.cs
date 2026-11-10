using System.Globalization;
using System.Text.Json;

namespace UsageTracker.Core;

/// <summary>Tolerant readers for loosely typed JSON from undocumented endpoints.</summary>
internal static class Json
{
    public static JsonElement? Child(this JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return value;
    }

    public static double? Number(this JsonElement? element)
    {
        if (element is not { } e) return null;
        return e.ValueKind switch
        {
            JsonValueKind.Number when e.TryGetDouble(out var d) && double.IsFinite(d) => d,
            JsonValueKind.String when double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) => d,
            _ => null,
        };
    }

    public static long? Integer(this JsonElement? element)
    {
        var d = element.Number();
        if (d is null) return null;
        if (d > long.MaxValue || d < long.MinValue) return null;
        return (long)d.Value;
    }

    public static string? String(this JsonElement? element)
    {
        if (element is not { } e) return null;
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText(),
            _ => null,
        };
    }

    public static DateTimeOffset? IsoDate(this JsonElement? element)
    {
        var text = element.String();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;
    }

    /// <summary>Top-level property names, for logging a response shape without its values.</summary>
    public static string DescribeShape(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return doc.RootElement.ValueKind.ToString();
            return "{" + string.Join(", ", doc.RootElement.EnumerateObject().Select(p => p.Name + ":" + p.Value.ValueKind)) + "}";
        }
        catch (JsonException)
        {
            return "<not JSON, " + json.Length + " chars>";
        }
    }
}
