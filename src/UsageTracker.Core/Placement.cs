namespace UsageTracker.Core;

/// <summary>How much of the widget fits next to the app buttons when it has to dodge them.</summary>
public enum BandFit
{
    Full,
    Compact,
    Hidden,
}

public static class Dodge
{
    /// <summary>
    /// Picks the largest form that fits in <paramref name="available"/> pixels. Growing back
    /// needs <paramref name="hysteresis"/> extra pixels so the widget doesn't flicker
    /// between forms while an app button's label changes length.
    /// </summary>
    public static BandFit Decide(int available, int fullWidth, int compactWidth, BandFit current, int hysteresis)
    {
        int Need(BandFit fit, int width) => (int)fit < (int)current ? width + hysteresis : width;

        if (available >= Need(BandFit.Full, fullWidth)) return BandFit.Full;
        if (available >= Need(BandFit.Compact, compactWidth)) return BandFit.Compact;
        return BandFit.Hidden;
    }
}

/// <summary>Counts events inside a sliding time window.</summary>
public sealed class RecentEvents
{
    private readonly Queue<DateTimeOffset> _events = new();
    private readonly TimeSpan _window;

    public RecentEvents(TimeSpan window)
    {
        _window = window;
    }

    /// <summary>Records an event and returns how many happened within the window.</summary>
    public int Add(DateTimeOffset at)
    {
        _events.Enqueue(at);
        while (_events.Count > 0 && at - _events.Peek() > _window) _events.Dequeue();
        return _events.Count;
    }

    public void Clear() => _events.Clear();
}
