using UsageTracker.Core.Claude;

namespace UsageTracker.Core.Tests;

public class ClaudeUsageParserTests
{
    [Fact]
    public void Parses_five_hour_and_seven_day_windows()
    {
        const string json = """
            {
              "five_hour": { "utilization": 23.0, "resets_at": "2026-11-10T05:00:00.123456+00:00" },
              "seven_day": { "utilization": 41.5, "resets_at": "2026-11-13T09:00:00+00:00" },
              "seven_day_oauth_apps": null,
              "seven_day_opus": null,
              "extra_usage": { "is_enabled": false }
            }
            """;

        Assert.True(ClaudeUsageParser.TryParse(json, "Max", out var reading, out var error), error);
        Assert.Equal(23.0, reading.Session!.UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 5, 0, 0, TimeSpan.Zero).AddTicks(1234560), reading.Session.ResetsAt);
        Assert.Equal(41.5, reading.Weekly!.UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero), reading.Weekly.ResetsAt);
        Assert.Equal("Max", reading.PlanLabel);
    }

    [Fact]
    public void A_null_window_is_reported_as_missing()
    {
        const string json = """{ "five_hour": { "utilization": 5, "resets_at": null }, "seven_day": null }""";

        Assert.True(ClaudeUsageParser.TryParse(json, null, out var reading, out _));
        Assert.Equal(5, reading.Session!.UsedPercent);
        Assert.Null(reading.Session.ResetsAt);
        Assert.Null(reading.Weekly);
    }

    [Fact]
    public void A_window_without_utilization_is_missing()
    {
        const string json = """{ "five_hour": { "resets_at": "2026-11-10T05:00:00Z" }, "seven_day": { "utilization": 10 } }""";

        Assert.True(ClaudeUsageParser.TryParse(json, null, out var reading, out _));
        Assert.Null(reading.Session);
        Assert.Equal(10, reading.Weekly!.UsedPercent);
    }

    [Fact]
    public void Utilization_sent_as_a_string_still_parses()
    {
        const string json = """{ "five_hour": { "utilization": "64.2", "resets_at": "2026-11-10T05:00:00Z" } }""";

        Assert.True(ClaudeUsageParser.TryParse(json, null, out var reading, out _));
        Assert.Equal(64.2, reading.Session!.UsedPercent, 3);
    }

    [Fact]
    public void Negative_utilization_is_clamped_to_zero()
    {
        const string json = """{ "five_hour": { "utilization": -3 }, "seven_day": { "utilization": 0 } }""";

        Assert.True(ClaudeUsageParser.TryParse(json, null, out var reading, out _));
        Assert.Equal(0, reading.Session!.UsedPercent);
    }

    [Theory]
    [InlineData("""{ "error": { "type": "authentication_error" } }""")]
    [InlineData("""[1, 2, 3]""")]
    [InlineData("""<html>blocked</html>""")]
    [InlineData("")]
    public void Unexpected_shapes_fail_with_a_reason(string json)
    {
        Assert.False(ClaudeUsageParser.TryParse(json, null, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Failure_reason_names_keys_but_not_values()
    {
        const string json = """{ "account_email": "someone@example.com" }""";

        Assert.False(ClaudeUsageParser.TryParse(json, null, out _, out var error));
        Assert.Contains("account_email", error);
        Assert.DoesNotContain("someone@example.com", error);
    }
}
