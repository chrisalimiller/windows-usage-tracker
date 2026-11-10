using UsageTracker.Core.Codex;

namespace UsageTracker.Core.Tests;

public class CodexUsageParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parses_primary_as_session_and_secondary_as_weekly()
    {
        const string json = """
            {
              "plan_type": "plus",
              "rate_limit": {
                "allowed": true,
                "limit_reached": false,
                "primary_window": { "used_percent": 12, "limit_window_seconds": 18000, "reset_after_seconds": 9000, "reset_at": 1762750800 },
                "secondary_window": { "used_percent": 8.5, "limit_window_seconds": 604800, "reset_after_seconds": 90000, "reset_at": 1763100000 }
              },
              "credits": { "has_credits": false, "unlimited": false, "balance": "0" }
            }
            """;

        Assert.True(CodexUsageParser.TryParse(json, Now, out var reading, out var error), error);
        Assert.Equal(12, reading.Session!.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1762750800), reading.Session.ResetsAt);
        Assert.Equal(8.5, reading.Weekly!.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1763100000), reading.Weekly.ResetsAt);
        Assert.Equal("Plus", reading.PlanLabel);
    }

    [Fact]
    public void A_lone_weekly_window_in_the_primary_slot_is_weekly()
    {
        // Codex has sent this while the 5-hour limit was switched off.
        const string json = """
            {
              "rate_limit": {
                "primary_window": { "used_percent": 100, "limit_window_seconds": 604800, "reset_at": 1763100000 },
                "secondary_window": null
              }
            }
            """;

        Assert.True(CodexUsageParser.TryParse(json, Now, out var reading, out _));
        Assert.Null(reading.Session);
        Assert.Equal(100, reading.Weekly!.UsedPercent);
    }

    [Fact]
    public void Windows_are_assigned_by_length_even_when_swapped()
    {
        const string json = """
            {
              "rate_limit": {
                "primary_window": { "used_percent": 30, "limit_window_seconds": 604800, "reset_at": 1763100000 },
                "secondary_window": { "used_percent": 70, "limit_window_seconds": 18000, "reset_at": 1762750800 }
              }
            }
            """;

        Assert.True(CodexUsageParser.TryParse(json, Now, out var reading, out _));
        Assert.Equal(70, reading.Session!.UsedPercent);
        Assert.Equal(30, reading.Weekly!.UsedPercent);
    }

    [Fact]
    public void Without_window_lengths_the_slot_order_decides()
    {
        const string json = """
            {
              "rate_limit": {
                "primary_window": { "used_percent": 1 },
                "secondary_window": { "used_percent": 2 }
              }
            }
            """;

        Assert.True(CodexUsageParser.TryParse(json, Now, out var reading, out _));
        Assert.Equal(1, reading.Session!.UsedPercent);
        Assert.Equal(2, reading.Weekly!.UsedPercent);
    }

    [Fact]
    public void An_unusable_reset_at_falls_back_to_reset_after_seconds()
    {
        const string json = """
            { "rate_limit": { "primary_window": { "used_percent": 0, "limit_window_seconds": 18000, "reset_at": -1, "reset_after_seconds": 600 } } }
            """;

        Assert.True(CodexUsageParser.TryParse(json, Now, out var reading, out _));
        Assert.Equal(Now.AddMinutes(10), reading.Session!.ResetsAt);
    }

    [Fact]
    public void No_reset_information_leaves_the_reset_unknown()
    {
        const string json = """{ "rate_limit": { "primary_window": { "used_percent": 0, "limit_window_seconds": 18000, "reset_at": -1 } } }""";

        Assert.True(CodexUsageParser.TryParse(json, Now, out var reading, out _));
        Assert.Equal(0, reading.Session!.UsedPercent);
        Assert.Null(reading.Session.ResetsAt);
    }

    [Fact]
    public void Limit_reached_without_details_shows_the_session_full()
    {
        const string json = """{ "rate_limit": { "limit_reached": true, "primary_window": null, "secondary_window": null } }""";

        Assert.True(CodexUsageParser.TryParse(json, Now, out var reading, out _));
        Assert.Equal(100, reading.Session!.UsedPercent);
        Assert.Null(reading.Weekly);
    }

    [Fact]
    public void Numbers_sent_as_strings_still_parse()
    {
        const string json = """{ "rate_limit": { "primary_window": { "used_percent": "42.5", "limit_window_seconds": "18000", "reset_at": "1762750800" } } }""";

        Assert.True(CodexUsageParser.TryParse(json, Now, out var reading, out _));
        Assert.Equal(42.5, reading.Session!.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1762750800), reading.Session.ResetsAt);
    }

    [Theory]
    [InlineData("""{ "detail": "Unauthorized" }""")]
    [InlineData("""{ "rate_limit": null }""")]
    [InlineData("""not json""")]
    [InlineData("""42""")]
    public void Unexpected_shapes_fail_with_a_reason(string json)
    {
        Assert.False(CodexUsageParser.TryParse(json, Now, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
