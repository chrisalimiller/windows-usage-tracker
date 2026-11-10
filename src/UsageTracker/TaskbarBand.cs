using System.Drawing;
using System.Runtime.InteropServices;
using UsageTracker.Core;
using UsageTracker.Native;
using static UsageTracker.Native.Win32;

namespace UsageTracker;

/// <summary>
/// The widget window itself: a layered child window of the primary taskbar (Shell_TrayWnd),
/// placed either in a strip freed at the right end of the taskbar (see <see cref="SpaceReserver"/>)
/// or just left of the notification area (TrayNotifyWnd). It never floats: if it
/// can't be a child of the taskbar, <see cref="TryAttach"/> fails and the caller shows
/// a tray icon with the error instead.
/// </summary>
internal sealed class TaskbarBand : IDisposable
{
    public const string WindowClass = "WindowsUsageTracker.Band";

    private readonly IntPtr _instance;
    private readonly IntPtr _owner;
    private readonly BandRenderer _renderer = new();

    private IntPtr _hwnd;
    private IntPtr _taskbar;
    private bool _destroying;
    private bool _trackingMouse;
    private bool _hover;
    private bool _loggedZOrder;

    private IReadOnlyList<BandRow> _rows = Array.Empty<BandRow>();
    private RECT? _strip;
    private BandFit _fit = BandFit.Full;
    private int[]? _backing;
    private bool _hidden;
    private RECT _screenRect;
    private BandPalette _palette = BandPalette.For(lightTaskbar: false);

    // Last applied geometry, so the layout timer only moves/redraws on change.
    private RECT _lastTaskbar;
    private RECT _lastTray;
    private RECT? _lastStrip;
    private BandFit _lastFit;
    private uint _lastDpi;
    private int _lastWidth;
    private int _lastHeight;
    private bool _needsRender = true;

    public TaskbarBand(IntPtr instance, IntPtr owner)
    {
        _instance = instance;
        _owner = owner;
    }

    /// <summary>Raised on right-click with the cursor position in screen coordinates.</summary>
    public event Action<POINT>? ContextMenuRequested;

    /// <summary>Raised on a left click.</summary>
    public event Action? Clicked;

    /// <summary>Raised when the window was destroyed by someone else (usually Explorer restarting).</summary>
    public event Action? Lost;

    public IntPtr Handle => _hwnd;

    public IntPtr Taskbar => _taskbar;

    /// <summary>Where the widget is on screen (physical pixels), or empty when hidden.</summary>
    public RECT ScreenRect => _hidden ? default : _screenRect;

    public bool IsShown => _hwnd != IntPtr.Zero && !_hidden;

    /// <summary>
    /// Where to sit: a reserved strip at the right end of the taskbar ("make room"), or null to
    /// sit just left of the tray in the given form ("dodge").
    /// </summary>
    public void SetPlacement(RECT? strip, BandFit fit, int[]? backing)
    {
        if (strip is { } s && s.Width <= 0) strip = null;
        if (strip is null) backing = null;
        var changed = !SameNullable(strip, _strip) || fit != _fit || !ReferenceEquals(backing, _backing);
        _strip = strip;
        _fit = strip is null ? fit : BandFit.Full;
        if (!ReferenceEquals(backing, _backing)) _needsRender = true;
        _backing = backing;
        if (changed && _hwnd != IntPtr.Zero) UpdateLayout(force: false);
    }

    /// <summary>Full and compact widths for the current content at this taskbar height and DPI.</summary>
    public (int Full, int Compact) Widths(TaskbarGeometry g)
    {
        var height = g.Horizontal ? g.TaskbarRect.Height : (int)Math.Round(48 * g.Scale);
        return (MeasureWidth(height, g.Dpi, g.Scale), BandRenderer.MeasureCompactWidth(_rows, height, g.Scale));
    }
    public bool IsAttachedTo(IntPtr taskbar) =>
        _hwnd != IntPtr.Zero && taskbar != IntPtr.Zero && _taskbar == taskbar
        && IsWindow(_hwnd) && GetParent(_hwnd) == taskbar;

    public void SetContent(IReadOnlyList<BandRow> rows, BandPalette palette)
    {
        _rows = rows;
        _palette = palette;
        _needsRender = true;

        if (_hwnd != IntPtr.Zero) UpdateLayout(force: false);
    }

    /// <summary>Creates the window and makes it a child of <paramref name="taskbar"/>.</summary>
    public bool TryAttach(IntPtr taskbar, out string error)
    {
        Destroy();
        error = string.Empty;

        _hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, WindowClass, "Windows Usage Tracker",
            WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            error = $"CreateWindowEx failed (error {Marshal.GetLastWin32Error()})";
            return false;
        }

        // Turn the popup into a child before reparenting, as SetParent's docs require.
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, (ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED) & ~WS_EX_TOPMOST);
        var style = GetWindowLong(_hwnd, GWL_STYLE);
        SetWindowLong(_hwnd, GWL_STYLE, (style & ~WS_POPUP) | WS_CHILD | WS_CLIPSIBLINGS);

        SetParent(_hwnd, taskbar);
        var parentError = Marshal.GetLastWin32Error();
        if (GetParent(_hwnd) != taskbar)
        {
            error = $"SetParent onto the taskbar failed (error {parentError})";
            Destroy();
            return false;
        }

        // Windows 11 can leave a reparented layered surface beneath the taskbar's XAML
        // content. Re-applying the layered style once after reparenting rebinds it.
        ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex & ~WS_EX_LAYERED);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED);
        SetWindowPos(_hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        _taskbar = taskbar;
        _lastTaskbar = default;
        _lastTray = default;
        _lastStrip = null;
        _lastDpi = 0;
        _lastWidth = 0;
        _lastHeight = 0;
        _needsRender = true;
        _loggedZOrder = false;

        UpdateLayout(force: true);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        Log.Info($"Embedded in taskbar 0x{taskbar.ToInt64():X} as child window 0x{_hwnd.ToInt64():X}");
        return true;
    }

    /// <summary>Repositions and redraws if the taskbar, tray, DPI, placement or content changed.</summary>
    public void UpdateLayout(bool force)
    {
        if (_hwnd == IntPtr.Zero || _taskbar == IntPtr.Zero) return;
        if (TaskbarGeometry.Read(_taskbar) is not { } g) return;

        var tb = g.TaskbarRect;
        var trayRect = g.TrayRect;
        var dpi = g.Dpi;
        var scale = g.Scale;

        if (_strip is null && _fit == BandFit.Hidden)
        {
            if (!_hidden)
            {
                ShowWindow(_hwnd, SW_HIDE);
                _hidden = true;
                Log.Verbose("Layout: hidden, the app buttons left no room");
            }

            _lastFit = BandFit.Hidden;
            return;
        }

        var horizontal = g.Horizontal;
        var height = horizontal ? tb.Height : (int)Math.Round(48 * scale);
        var width = _fit == BandFit.Compact && _strip is null
            ? BandRenderer.MeasureCompactWidth(_rows, height, scale)
            : MeasureWidth(height, dpi, scale);

        var geometryChanged = force || _hidden || !Same(tb, _lastTaskbar) || !Same(trayRect, _lastTray) || dpi != _lastDpi
            || width != _lastWidth || height != _lastHeight || !SameNullable(_strip, _lastStrip) || _fit != _lastFit;

        if (geometryChanged)
        {
            int screenX, screenY;
            if (_strip is { } strip)
            {
                // "Make room": fill the freed strip at the right end; the content is centered in it.
                screenX = strip.Left;
                width = strip.Width;
                height = strip.Height;
                screenY = strip.Top;
            }
            else if (horizontal)
            {
                var gap = (int)Math.Round(4 * scale);
                screenX = Math.Max(tb.Left, trayRect.Left - width - gap);
                screenY = tb.Top + (tb.Height - height) / 2;
            }
            else
            {
                // Vertical taskbars (Windows 10): sit above the tray.
                width = Math.Min(width, tb.Width);
                screenX = tb.Left;
                screenY = Math.Max(tb.Top, trayRect.Top - height);
            }

            var point = new POINT { X = screenX, Y = screenY };
            MapWindowPoints(IntPtr.Zero, _taskbar, ref point, 1);
            SetWindowPos(_hwnd, HWND_TOP, point.X, point.Y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            _hidden = false;
            _screenRect = new RECT { Left = screenX, Top = screenY, Right = screenX + width, Bottom = screenY + height };

            Log.Verbose($"Layout: taskbar {tb} tray {trayRect} dpi {dpi} {(_strip is null ? $"dodge/{_fit}" : "make-room strip")} -> widget {width}x{height} at screen ({screenX},{screenY})");

            _lastTaskbar = tb;
            _lastTray = trayRect;
            _lastDpi = dpi;
            _lastWidth = width;
            _lastHeight = height;
            _lastStrip = _strip;
            _lastFit = _fit;
            _needsRender = true;
        }

        KeepOnTop();

        if (_needsRender)
        {
            Present(width, height, scale);
            _needsRender = false;
        }
    }

    private IReadOnlyList<BandRow>? _measuredRows;
    private int _measuredHeight;
    private uint _measuredDpi;
    private int _measuredWidth;

    /// <summary>Text measurement is only redone when the content, height or DPI changes.</summary>
    private int MeasureWidth(int height, uint dpi, float scale)
    {
        if (!ReferenceEquals(_measuredRows, _rows) || _measuredHeight != height || _measuredDpi != dpi)
        {
            _measuredWidth = _renderer.MeasureWidth(_rows, height, scale);
            _measuredRows = _rows;
            _measuredHeight = height;
            _measuredDpi = dpi;
        }

        return _measuredWidth;
    }

    /// <summary>
    /// The Windows 11 taskbar draws its content in a XAML island that is also a child of
    /// Shell_TrayWnd; if it is ever raised above us we would be hidden, so stay first.
    /// </summary>
    private void KeepOnTop()
    {
        var above = GetWindow(_hwnd, GW_HWNDPREV);
        if (above == IntPtr.Zero) return;
        if (!_loggedZOrder)
        {
            Log.Verbose($"Z-order: sibling 0x{above.ToInt64():X} ({ClassName(above)}) was above the widget; raising");
            _loggedZOrder = true;
        }

        SetWindowPos(_hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void Present(int width, int height, float scale)
    {
        if (width <= 0 || height <= 0) return;
        var style = new BandStyle(Compact: _strip is null && _fit == BandFit.Compact, _hover, _backing);
        var pixels = _renderer.Render(_rows, width, height, scale, _palette, style);

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            },
        };

        var bitmap = CreateDIBSection(memDc, ref info, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            Log.Error($"CreateDIBSection failed (error {Marshal.GetLastWin32Error()})");
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            return;
        }

        var old = SelectObject(memDc, bitmap);
        CopyPixels(pixels, bits);

        var size = new SIZE { Cx = width, Cy = height };
        var source = new POINT();
        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            SourceConstantAlpha = 255,
            AlphaFormat = AC_SRC_ALPHA,
        };

        if (!UpdateLayeredWindow(_hwnd, screenDc, IntPtr.Zero, ref size, memDc, ref source, 0, ref blend, ULW_ALPHA))
        {
            Log.Error($"UpdateLayeredWindow failed (error {Marshal.GetLastWin32Error()}) for {width}x{height}");
        }

        SelectObject(memDc, old);
        DeleteObject(bitmap);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);
    }

    private static unsafe void CopyPixels(uint[] pixels, IntPtr bits)
    {
        fixed (uint* src = pixels)
        {
            Buffer.MemoryCopy(src, (void*)bits, pixels.Length * 4L, pixels.Length * 4L);
        }
    }

    /// <summary>Window procedure for the band window class.</summary>
    public IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                return new IntPtr(MA_NOACTIVATE);

            case WM_NCHITTEST:
                return new IntPtr(HTCLIENT);

            case WM_MOUSEMOVE:
                if (!_trackingMouse)
                {
                    var tme = new TRACKMOUSEEVENT
                    {
                        cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                        dwFlags = TME_LEAVE,
                        hwndTrack = hwnd,
                    };
                    _trackingMouse = TrackMouseEvent(ref tme);
                }

                if (!_hover)
                {
                    _hover = true;
                    _needsRender = true;
                    UpdateLayout(force: false);
                }

                return IntPtr.Zero;

            case WM_MOUSELEAVE:
                _trackingMouse = false;
                if (_hover)
                {
                    _hover = false;
                    _needsRender = true;
                    UpdateLayout(force: false);
                }

                return IntPtr.Zero;

            case WM_RBUTTONUP:
                if (GetCursorPos(out var pt)) ContextMenuRequested?.Invoke(pt);
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                Clicked?.Invoke();
                return IntPtr.Zero;

            case WM_DPICHANGED_AFTERPARENT:
                UpdateLayout(force: true);
                return IntPtr.Zero;

            case WM_DESTROY:
                if (hwnd == _hwnd && !_destroying)
                {
                    Log.Info("Widget window was destroyed by its parent (Explorer restarted?)");
                    _hwnd = IntPtr.Zero;
                    _taskbar = IntPtr.Zero;
                    Lost?.Invoke();
                }

                return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Destroy()
    {
        if (_hwnd != IntPtr.Zero)
        {
            _destroying = true;
            if (IsWindow(_hwnd)) DestroyWindow(_hwnd);
            _destroying = false;
            _hwnd = IntPtr.Zero;
        }

        _taskbar = IntPtr.Zero;
        _trackingMouse = false;
        _hover = false;
        _hidden = false;
    }

    private static bool SameNullable(RECT? a, RECT? b) =>
        a is { } x ? b is { } y && Same(x, y) : b is null;

    private static bool Same(RECT a, RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    public void Dispose()
    {
        Destroy();
        _renderer.Dispose();
    }
}
