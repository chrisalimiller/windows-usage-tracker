namespace UsageTracker.Core;

/// <summary>Where usage would be now if it were spread evenly across a limit window.</summary>
public static class Pace
{
    public static readonly TimeSpan SessionLength = TimeSpan.FromHours(5);
    public static readonly TimeSpan WeeklyLength = TimeSpan.FromDays(7);

    /// <summary>
    /// Fraction (0 to 1) of the window that has passed, or null when the reset time is
    /// unknown. Usage above this fraction (as a percent) is ahead of an even pace.
    /// </summary>
    public static double? ElapsedFraction(UsageWindow? window, TimeSpan length, DateTimeOffset now)
    {
        if (window?.ResetsAt is not { } resetsAt || length <= TimeSpan.Zero) return null;
        var remaining = resetsAt - now;
        if (remaining <= TimeSpan.Zero) return 1;
        if (remaining >= length) return 0;
        return 1 - remaining.TotalSeconds / length.TotalSeconds;
    }
}

/// <summary>Recent 5-hour usage readings for one provider, kept in memory for the details panel.</summary>
public sealed class UsageHistory
{
    public static readonly TimeSpan Span = TimeSpan.FromHours(5);

    private readonly List<(DateTimeOffset At, double Percent)> _points = new();

    public IReadOnlyList<(DateTimeOffset At, double Percent)> Points => _points;

    /// <summary>Adds a reading; repeats of the same fetch are ignored and old points dropped.</summary>
    public void Add(DateTimeOffset at, double percent)
    {
        if (_points.Count > 0 && _points[^1].At >= at) return;
        _points.Add((at, Math.Max(0, percent)));
        Trim(at);
    }

    /// <summary>Points from the last <see cref="Span"/> before <paramref name="now"/>.</summary>
    public IReadOnlyList<(DateTimeOffset At, double Percent)> Recent(DateTimeOffset now)
    {
        Trim(now);
        return _points;
    }

    private void Trim(DateTimeOffset now)
    {
        var cutoff = now - Span;
        var drop = 0;
        while (drop < _points.Count && _points[drop].At < cutoff) drop++;
        if (drop > 0) _points.RemoveRange(0, drop);
    }
}
