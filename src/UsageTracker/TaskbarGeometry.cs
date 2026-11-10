using UsageTracker.Native;
using static UsageTracker.Native.Win32;

namespace UsageTracker;

/// <summary>Where the primary taskbar and its parts are right now, in physical screen pixels.</summary>
internal readonly record struct TaskbarGeometry(
    IntPtr Taskbar,
    RECT TaskbarRect,
    IntPtr Tray,
    RECT TrayRect,
    IntPtr Content,
    RECT? ContentRect,
    uint Dpi)
{
    /// <summary>Windows 11 draws the taskbar's buttons, tray and clock in this XAML island window.</summary>
    public const string ContentClass = "Windows.UI.Composition.DesktopWindowContentBridge";

    public float Scale => Dpi / 96f;

    public bool Horizontal => TaskbarRect.Width >= TaskbarRect.Height;

    public static TaskbarGeometry? Read(IntPtr taskbar)
    {
        if (taskbar == IntPtr.Zero || WindowRect(taskbar) is not { } tb || tb.Width <= 0 || tb.Height <= 0) return null;

        var tray = FindWindowExW(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        var trayRect = WindowRect(tray) ?? new RECT { Left = tb.Right, Right = tb.Right, Top = tb.Top, Bottom = tb.Bottom };

        var content = FindContent(taskbar);
        var dpi = GetDpiForWindow(taskbar);
        return new TaskbarGeometry(taskbar, tb, tray, trayRect, content, WindowRect(content), dpi == 0 ? 96 : dpi);
    }

    /// <summary>The widest direct child of the taskbar with the XAML island class.</summary>
    public static IntPtr FindContent(IntPtr taskbar)
    {
        var best = IntPtr.Zero;
        var bestWidth = -1;
        var child = IntPtr.Zero;
        while ((child = FindWindowExW(taskbar, child, ContentClass, null)) != IntPtr.Zero)
        {
            var width = WindowRect(child)?.Width ?? 0;
            if (width > bestWidth)
            {
                best = child;
                bestWidth = width;
            }
        }

        return best;
    }
}
