using System.Globalization;

namespace UsageTracker.Core.Tests;

public class FormatTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    // ICU puts a narrow no-break space before AM/PM; compare with plain spaces.
    private static string N(string s) => s.Replace('\u202F', ' ').Replace('\u00A0', ' ');

    [Theory]
    [InlineData(0, "0%")]
    [InlineData(22.5, "23%")]
    [InlineData(99.4, "99%")]
    [InlineData(100, "100%")]
    public void Percent_rounds_to_whole_numbers(double used, string expected)
    {
        Assert.Equal(expected, Format.Percent(new UsageWindow(used, null)));
    }

    [Fact]
    public void Missing_window_shows_dashes()
    {
        Assert.Equal("--", Format.Percent(null));
    }

    [Theory]
    [InlineData(0, Severity.Normal)]
    [InlineData(69.9, Severity.Normal)]
    [InlineData(70, Severity.Warning)]
    [InlineData(89.9, Severity.Warning)]
    [InlineData(90, Severity.Critical)]
    [InlineData(120, Severity.Critical)]
    public void Severity_thresholds(double used, Severity expected)
    {
        Assert.Equal(expected, Format.SeverityOf(used));
    }

    [Theory]
    [InlineData(30, "<1m")]
    [InlineData(60, "1m")]
    [InlineData(61, "2m")]
    [InlineData(3600, "1h")]
    [InlineData(8040, "2h 14m")]
    [InlineData(90000, "1d 1h")]
    [InlineData(172800, "2d")]
    public void Duration_is_compact(int seconds, string expected)
    {
        Assert.Equal(expected, Format.Duration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Resets_within_a_day_show_countdown_and_clock()
    {
        var text = N(Format.Reset(Now.AddHours(2).AddMinutes(14), Now, TimeZoneInfo.Utc, Culture));
        Assert.Equal("resets in 2h 14m (2:14 PM)", text);
    }

    [Fact]
    public void Resets_beyond_a_day_show_the_weekday()
    {
        var text = N(Format.Reset(Now.AddDays(3), Now, TimeZoneInfo.Utc, Culture));
        Assert.Equal("resets Fri 12:00 PM (in 3d)", text);
    }

    [Fact]
    public void Unknown_and_past_resets()
    {
        Assert.Equal("reset time unknown", Format.Reset(null, Now, TimeZoneInfo.Utc, Culture));
        Assert.Equal("resetting now", Format.Reset(Now.AddSeconds(-5), Now, TimeZoneInfo.Utc, Culture));
    }

    [Fact]
    public void Tooltip_lists_both_providers_and_problems()
    {
        var claude = new ProviderSnapshot(ProviderKind.Claude, ProviderStatus.Ok,
            new UsageReading(new UsageWindow(23, Now.AddHours(1)), new UsageWindow(41, Now.AddDays(2)), "Max"),
            Now.AddMinutes(-2), null, Now);
        var codex = new ProviderSnapshot(ProviderKind.Codex, ProviderStatus.TokenExpired, null, null,
            "Your Codex sign-in has expired.", Now);

        var text = N(Format.Tooltip(new[] { claude, codex }, Now, TimeZoneInfo.Utc, Culture));

        Assert.Contains("Claude (Max)", text);
        Assert.Contains("5-hour: 23% used, resets in 1h (1:00 PM)", text);
        Assert.Contains("Weekly: 41% used, resets Thu 12:00 PM (in 2d)", text);
        Assert.Contains("Updated 2 min ago", text);
        Assert.Contains("Codex", text);
        Assert.Contains("Your Codex sign-in has expired.", text);
    }

    [Fact]
    public void Summary_fits_a_tray_tip()
    {
        var s = new ProviderSnapshot(ProviderKind.Claude, ProviderStatus.Ok,
            new UsageReading(new UsageWindow(5, null), null), Now, null, Now);
        Assert.Equal("Claude 5h 5% 7d -- | Codex --", Format.Summary(new[] { s, ProviderSnapshot.Initial(ProviderKind.Codex) }));
    }
}
