using System.Text.Json;

namespace UsageTracker.Core.Claude;

/// <summary>
/// Parses the response of <c>GET https://api.anthropic.com/api/oauth/usage</c>.
/// The endpoint is undocumented; the shape seen so far is:
/// <code>
/// {
///   "five_hour":  { "utilization": 23.0, "resets_at": "2026-11-10T05:00:00.000000+00:00" },
///   "seven_day":  { "utilization": 41.0, "resets_at": "2026-11-13T09:00:00.000000+00:00" },
///   "seven_day_opus": null, ...
/// }
/// </code>
/// <c>utilization</c> is a percentage (0 to 100). Either window may be null.
/// </summary>
public static class ClaudeUsageParser
{
    public static bool TryParse(string json, string? planLabel, out UsageReading reading, out string? error)
    {
        reading = new UsageReading(null, null, planLabel);
        error = null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = "Claude usage response is not JSON: " + ex.Message;
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Claude usage response is not a JSON object";
                return false;
            }

            var hasFiveHour = root.TryGetProperty("five_hour", out _);
            var hasSevenDay = root.TryGetProperty("seven_day", out _);
            if (!hasFiveHour && !hasSevenDay)
            {
                error = "Claude usage response has neither five_hour nor seven_day: " + Json.DescribeShape(json);
                return false;
            }

            reading = new UsageReading(ReadWindow(root.Child("five_hour")), ReadWindow(root.Child("seven_day")), planLabel);
            return true;
        }
    }

    private static UsageWindow? ReadWindow(JsonElement? bucket)
    {
        if (bucket is not { } b || b.ValueKind != JsonValueKind.Object) return null;
        var utilization = b.Child("utilization").Number();
        if (utilization is null) return null;
        return new UsageWindow(Math.Max(0, utilization.Value), b.Child("resets_at").IsoDate());
    }
}
