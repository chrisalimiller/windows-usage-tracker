using UsageTracker.Core;
using Xunit;

namespace UsageTracker.Core.Tests;

public class HistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ElapsedFractionIsShareOfWindowPassed()
    {
        var window = new UsageWindow(40, Now.AddHours(2));
        Assert.Equal(0.6, Pace.ElapsedFraction(window, Pace.SessionLength, Now)!.Value, 6);
    }

    [Fact]
    public void ElapsedFractionClampsAndHandlesUnknownReset()
    {
        Assert.Null(Pace.ElapsedFraction(new UsageWindow(10, null), Pace.SessionLength, Now));
        Assert.Null(Pace.ElapsedFraction(null, Pace.SessionLength, Now));
        Assert.Equal(1, Pace.ElapsedFraction(new UsageWindow(10, Now.AddMinutes(-1)), Pace.SessionLength, Now));
        Assert.Equal(0, Pace.ElapsedFraction(new UsageWindow(10, Now.AddHours(9)), Pace.SessionLength, Now));
    }

    [Fact]
    public void HistoryKeepsOnlyRecentPointsInOrder()
    {
        var history = new UsageHistory();
        history.Add(Now.AddHours(-6), 5);
        history.Add(Now.AddHours(-2), 20);
        history.Add(Now.AddHours(-2), 99); // same fetch again: ignored
        history.Add(Now.AddHours(-1), 30);

        var recent = history.Recent(Now);
        Assert.Equal(new[] { 20.0, 30.0 }, recent.Select(p => p.Percent));
    }

    [Fact]
    public void HistoryDropsPointsAsTimePasses()
    {
        var history = new UsageHistory();
        history.Add(Now, 10);
        Assert.Empty(history.Recent(Now.AddHours(6)));
    }
}
