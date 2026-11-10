using System.Text.Json;

namespace UsageTracker.Core.Codex;

/// <summary>
/// Parses the response of <c>GET https://chatgpt.com/backend-api/wham/usage</c>, the endpoint the
/// Codex CLI uses for ChatGPT-plan rate limits. Undocumented; the shape seen so far is:
/// <code>
/// {
///   "plan_type": "plus",
///   "rate_limit": {
///     "limit_reached": false,
///     "primary_window":   { "used_percent": 12, "limit_window_seconds": 18000,  "reset_after_seconds": 9000,  "reset_at": 1762750800 },
///     "secondary_window": { "used_percent": 8,  "limit_window_seconds": 604800, "reset_after_seconds": 90000, "reset_at": 1763100000 }
///   }
/// }
/// </code>
/// Windows are assigned by length, not by slot: Codex has sent the weekly window in
/// <c>primary_window</c> with <c>secondary_window</c> null while the 5-hour limit was switched off.
/// </summary>
public static class CodexUsageParser
{
    /// <summary>A window this long or longer is the weekly allowance.</summary>
    internal const long WeeklyThresholdSeconds = 86_400;

    public static bool TryParse(string json, DateTimeOffset now, out UsageReading reading, out string? error)
    {
        reading = new UsageReading(null, null);
        error = null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = "Codex usage response is not JSON: " + ex.Message;
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Codex usage response is not a JSON object";
                return false;
            }

            var plan = PlanLabel(root.Child("plan_type").String());
            var rateLimit = root.Child("rate_limit");
            if (rateLimit is not { ValueKind: JsonValueKind.Object } limits)
            {
                error = "Codex usage response has no rate_limit: " + Json.DescribeShape(json);
                return false;
            }

            UsageWindow? session = null;
            UsageWindow? weekly = null;
            foreach (var (slot, defaultWeekly) in new[] { ("primary_window", false), ("secondary_window", true) })
            {
                var element = limits.Child(slot);
                if (element is not { ValueKind: JsonValueKind.Object } w) continue;
                var window = ReadWindow(w, now);
                if (window is null) continue;

                var lengthSeconds = w.Child("limit_window_seconds").Integer();
                var isWeekly = lengthSeconds is { } len ? len >= WeeklyThresholdSeconds : defaultWeekly;
                if (isWeekly) weekly = window;
                else session = window;
            }

            if (session is null && weekly is null && limits.Child("limit_reached") is { ValueKind: JsonValueKind.True })
            {
                // Limit reached but no window details: show the session as full rather than nothing.
                session = new UsageWindow(100, null);
            }

            reading = new UsageReading(session, weekly, plan);
            return true;
        }
    }

    private static UsageWindow? ReadWindow(JsonElement window, DateTimeOffset now)
    {
        var used = window.Child("used_percent").Number();
        if (used is null) return null;

        DateTimeOffset? resetsAt = null;
        var resetAt = window.Child("reset_at").Integer();
        var resetAfter = window.Child("reset_after_seconds").Integer();
        if (resetAt is > 0 and < 253_402_300_799) // DateTimeOffset's max year 9999
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetAt.Value);
        }
        else if (resetAfter is > 0 and < 100L * 365 * 86_400)
        {
            resetsAt = now.AddSeconds(resetAfter.Value);
        }

        return new UsageWindow(Math.Max(0, used.Value), resetsAt);
    }

    private static string? PlanLabel(string? planType)
    {
        if (string.IsNullOrWhiteSpace(planType)) return null;
        var trimmed = planType.Trim();
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }
}
