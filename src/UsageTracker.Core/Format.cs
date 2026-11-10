using System.Globalization;
using System.Text;

namespace UsageTracker.Core;

/// <summary>How close a window is to its limit, which picks the text color.</summary>
public enum Severity
{
    Normal,
    Warning,
    Critical,
}

/// <summary>Text shown in the taskbar and the tooltip.</summary>
public static class Format
{
    public const double WarningPercent = 70;
    public const double CriticalPercent = 90;

    /// <summary>Readings older than this are drawn dimmed.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    public static Severity SeverityOf(double percent) =>
        percent >= CriticalPercent ? Severity.Critical
        : percent >= WarningPercent ? Severity.Warning
        : Severity.Normal;

    /// <summary>"23%", or "--" when there is no reading.</summary>
    public static string Percent(UsageWindow? window) =>
        window is null ? "--" : Math.Round(window.UsedPercent, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>"2h 14m", "45m", "3d 4h", "<1m".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1)) return "<1m";
        var total = (long)Math.Ceiling(span.TotalMinutes);
        var days = total / (24 * 60);
        var hours = total / 60 % 24;
        var minutes = total % 60;
        if (days > 0) return hours > 0 ? $"{days}d {hours}h" : $"{days}d";
        if (hours > 0) return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        return $"{minutes}m";
    }

    /// <summary>"resets in 2h 14m (3:45 PM)" or, beyond a day, "resets Thu 9:00 AM".</summary>
    public static string Reset(DateTimeOffset? resetsAt, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        if (resetsAt is not { } at) return "reset time unknown";
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var until = at - now;
        if (until <= TimeSpan.Zero) return "resetting now";
        var clock = local.ToString("t", culture);
        if (until < TimeSpan.FromHours(24)) return $"resets in {Duration(until)} ({clock})";
        return $"resets {local.ToString("ddd", culture)} {clock} (in {Duration(until)})";
    }

    /// <summary>"just now", "3 min ago", "2 h ago".</summary>
    public static string Age(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h ago";
        return $"{(int)age.TotalDays} d ago";
    }

    /// <summary>The multi-line tooltip shown when hovering the taskbar widget.</summary>
    public static string Tooltip(IEnumerable<ProviderSnapshot> snapshots, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        var sb = new StringBuilder();
        foreach (var s in snapshots)
        {
            if (sb.Length > 0) sb.Append("\r\n\r\n");
            var plan = s.Reading?.PlanLabel is { } p ? $" ({p})" : string.Empty;
            sb.Append(s.DisplayName).Append(plan);

            if (s.Reading is { } r)
            {
                AppendWindow(sb, "5-hour", r.Session, now, zone, culture);
                AppendWindow(sb, "Weekly", r.Weekly, now, zone, culture);
                if (s.ReadingFetchedAt is { } fetched)
                {
                    sb.Append("\r\n  Updated ").Append(Age(now - fetched));
                }
            }
            else if (s.Status == ProviderStatus.Pending)
            {
                sb.Append("\r\n  Loading…");
            }

            if (s.Status != ProviderStatus.Ok && s.Message is { } message)
            {
                sb.Append("\r\n  ").Append(message);
            }
        }

        return sb.ToString();
    }

    /// <summary>A short one-line summary for the tray icon fallback (max 127 chars).</summary>
    public static string Summary(IEnumerable<ProviderSnapshot> snapshots)
    {
        var parts = snapshots.Select(s => s.Reading is { } r
            ? $"{s.DisplayName} 5h {Percent(r.Session)} 7d {Percent(r.Weekly)}"
            : $"{s.DisplayName} --");
        return string.Join(" | ", parts);
    }

    private static void AppendWindow(StringBuilder sb, string name, UsageWindow? window, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        sb.Append("\r\n  ").Append(name).Append(": ");
        if (window is null)
        {
            sb.Append("not reported");
            return;
        }

        sb.Append(Percent(window)).Append(" used, ").Append(Reset(window.ResetsAt, now, zone, culture));
    }
}
