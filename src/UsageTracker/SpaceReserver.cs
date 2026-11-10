using UsageTracker.Core;
using UsageTracker.Native;
using static UsageTracker.Native.Win32;

namespace UsageTracker;

/// <summary>
/// "Make room": narrows the window Windows 11 draws the taskbar's buttons, tray and clock in
/// (a XAML island that normally spans the whole bar), so Explorer lays them out in less space
/// and leaves a strip free at the right end for the widget.
///
/// This isn't a supported Windows feature, so it watches for trouble and gives up cleanly
/// (restoring the original size) if Explorer keeps putting the size back or the buttons
/// don't rearrange. The caller then falls back to dodging.
/// </summary>
internal sealed class SpaceReserver
{
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(1.5);

    private readonly RecentEvents _undone = new(TimeSpan.FromSeconds(60));
    private IntPtr _content;
    private IntPtr _taskbar;
    private bool _applied;
    private int _lastSetWidth;
    private DateTime _setAt;

    /// <summary>Why making room was given up, or null while it's working or untried.</summary>
    public string? Failure { get; private set; }

    public IntPtr Content => _content;

    /// <summary>
    /// Makes sure <paramref name="reserve"/> pixels are free at the right end of the taskbar and
    /// returns that strip in screen coordinates, or null if making room isn't possible here.
    /// </summary>
    public RECT? Apply(TaskbarGeometry g, int reserve)
    {
        if (Failure is not null) return null;
        if (!g.Horizontal)
        {
            Fail("the taskbar is vertical");
            return null;
        }

        if (g.Content == IntPtr.Zero || g.ContentRect is not { } c)
        {
            Fail("this taskbar has no XAML content window (Windows 10, or a taskbar replacement)");
            return null;
        }

        if (g.Content != _content)
        {
            _content = g.Content;
            _taskbar = g.Taskbar;
            _applied = false;
            _undone.Clear();
        }

        var tb = g.TaskbarRect;
        var fullWidth = tb.Right - c.Left;
        var target = fullWidth - reserve;
        if (!_applied && c.Right < tb.Right - 1)
        {
            Fail($"the taskbar content {c} doesn't reach the right edge of the taskbar {tb}; another tool may be changing it");
            return null;
        }

        if (target < tb.Width / 3)
        {
            Fail($"the taskbar is too narrow ({tb.Width}px) to give up {reserve}px");
            return null;
        }

        if (Math.Abs(c.Width - target) > 1)
        {
            var now = DateTime.UtcNow;
            // Our resize is asynchronous; give it a moment before judging or repeating it.
            if (_applied && now - _setAt < TimeSpan.FromMilliseconds(300) && Math.Abs(_lastSetWidth - target) <= 1)
            {
                return new RECT { Left = tb.Right - reserve, Top = tb.Top, Right = tb.Right, Bottom = tb.Bottom };
            }

            var putBack = _applied && c.Right >= tb.Right - 1 && Math.Abs(c.Width - _lastSetWidth) > 1;
            if (putBack)
            {
                var count = _undone.Add(DateTimeOffset.UtcNow);
                Log.Verbose($"Make room: Explorer restored the content width to {c.Width}px ({count} in the last minute)");
                if (count > 8)
                {
                    Fail("Explorer keeps resizing its taskbar content back");
                    return null;
                }
            }

            var origin = new POINT { X = c.Left, Y = c.Top };
            MapWindowPoints(IntPtr.Zero, g.Taskbar, ref origin, 1);
            if (!SetWindowPos(g.Content, IntPtr.Zero, origin.X, origin.Y, target, c.Height,
                    SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS))
            {
                Fail($"resizing the taskbar content failed (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
                return null;
            }

            if (!_applied) Log.Info($"Make room: narrowing taskbar content 0x{g.Content.ToInt64():X} from {c.Width}px to {target}px");
            _applied = true;
            _lastSetWidth = target;
            _setAt = now;
        }

        return new RECT { Left = tb.Right - reserve, Top = tb.Top, Right = tb.Right, Bottom = tb.Bottom };
    }

    /// <summary>
    /// Checks that Explorer really rearranged its buttons into the narrower space rather than
    /// cutting them off. Gives up if a button (usually the clock) still reaches into the strip.
    /// </summary>
    public void Verify(ProbeResult probe, RECT strip, float scale)
    {
        if (!_applied || Failure is not null || !probe.Ok || probe.Content != _content) return;
        if (probe.TakenAt < _setAt + SettleTime) return;
        if (probe.RightmostEdge is { } right && right > strip.Left + (int)Math.Round(2 * scale))
        {
            Fail($"the taskbar didn't rearrange its buttons (a button still ends at x={right}, past {strip.Left})");
        }
    }

    /// <summary>Puts the taskbar content back to its full width.</summary>
    public void Restore()
    {
        if (!_applied) return;
        _applied = false;
        if (!IsWindow(_content) || !IsWindow(_taskbar) || WindowRect(_content) is not { } c || WindowRect(_taskbar) is not { } tb) return;

        var origin = new POINT { X = c.Left, Y = c.Top };
        MapWindowPoints(IntPtr.Zero, _taskbar, ref origin, 1);
        SetWindowPos(_content, IntPtr.Zero, origin.X, origin.Y, tb.Right - c.Left, c.Height,
            SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
        Log.Info($"Make room: restored taskbar content to {tb.Right - c.Left}px");
    }

    /// <summary>Forgets an earlier failure so the next <see cref="Apply"/> tries again.</summary>
    public void Reset()
    {
        Restore();
        Failure = null;
        _content = IntPtr.Zero;
        _taskbar = IntPtr.Zero;
        _undone.Clear();
    }

    private void Fail(string reason)
    {
        Failure = reason;
        Log.Info($"Make room isn't working here, so the widget will dodge app buttons instead: {reason}");
        Restore();
    }
}
