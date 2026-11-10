using UsageTracker.Core;
using Xunit;

namespace UsageTracker.Core.Tests;

public class PlacementTests
{
    [Theory]
    [InlineData(300, BandFit.Full, BandFit.Full)]
    [InlineData(180, BandFit.Full, BandFit.Full)]
    [InlineData(179, BandFit.Full, BandFit.Compact)]
    [InlineData(70, BandFit.Full, BandFit.Compact)]
    [InlineData(69, BandFit.Full, BandFit.Hidden)]
    public void ShrinksAsSoonAsItDoesNotFit(int available, BandFit current, BandFit expected)
    {
        Assert.Equal(expected, Dodge.Decide(available, fullWidth: 180, compactWidth: 70, current, hysteresis: 16));
    }

    [Theory]
    [InlineData(180, BandFit.Compact)] // fits exactly, but needs the margin to grow back
    [InlineData(196, BandFit.Full)]
    public void GrowsBackOnlyWithMargin(int available, BandFit expected)
    {
        Assert.Equal(expected, Dodge.Decide(available, 180, 70, BandFit.Compact, 16));
    }

    [Fact]
    public void HiddenNeedsMarginToReappear()
    {
        Assert.Equal(BandFit.Hidden, Dodge.Decide(75, 180, 70, BandFit.Hidden, 16));
        Assert.Equal(BandFit.Compact, Dodge.Decide(86, 180, 70, BandFit.Hidden, 16));
    }

    [Fact]
    public void RecentEventsCountsInsideWindow()
    {
        var events = new RecentEvents(TimeSpan.FromSeconds(60));
        var t = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(1, events.Add(t));
        Assert.Equal(2, events.Add(t.AddSeconds(30)));
        Assert.Equal(2, events.Add(t.AddSeconds(70)));
        Assert.Equal(1, events.Add(t.AddSeconds(200)));
    }
}
