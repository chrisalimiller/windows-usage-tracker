using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using UsageTracker.Core;
using UsageTracker.Native;
using static UsageTracker.Native.Win32;

namespace UsageTracker;

/// <summary>One provider's section in the details panel, with all text already formatted.</summary>
internal sealed record PanelProvider(
    ProviderKind Kind,
    string Name,
    string? Detail,
    UsageWindow? Session,
    UsageWindow? Weekly,
    double? SessionPace,
    double? WeeklyPace,
    string SessionText,
    string WeeklyText,
    IReadOnlyList<(DateTimeOffset At, double Percent)> History,
    DateTimeOffset Now,
    string? Message,
    bool Dim);

/// <summary>
/// The panel that opens above the taskbar when you click the widget, like the clock's calendar:
/// bars with pace markers, reset times and the last five hours of 5-hour usage. It closes when
/// you click anywhere else, press Esc or click the widget again.
/// </summary>
internal sealed class DetailsPanel : IDisposable
{
    public const string WindowClass = "WindowsUsageTracker.Panel";

    private readonly IntPtr _instance;
    private readonly IntPtr _owner;
    private readonly PanelRenderer _renderer = new();
    private IntPtr _hwnd;
    private DateTime _hiddenAt = DateTime.MinValue;
    private IReadOnlyList<PanelProvider> _content = Array.Empty<PanelProvider>();
    private bool _light;
    private RECT _anchor;
    private IntPtr _taskbar;

    public DetailsPanel(IntPtr instance, IntPtr owner)
    {
        _instance = instance;
        _owner = owner;
    }

    public bool Visible => _hwnd != IntPtr.Zero && IsWindowVisible(_hwnd);

    /// <summary>Opens the panel above <paramref name="anchor"/>, or closes it if it's open.</summary>
    public void Toggle(RECT anchor, IntPtr taskbar)
    {
        if (Visible)
        {
            Hide();
            return;
        }

        // Clicking the widget while the panel is open first deactivates (and hides) the panel;
        // don't let that same click open it again.
        if (DateTime.UtcNow - _hiddenAt < TimeSpan.FromMilliseconds(400)) return;
        Show(anchor, taskbar);
    }

    public void SetContent(IReadOnlyList<PanelProvider> content, bool lightTheme)
    {
        _content = content;
        _light = lightTheme;
        if (Visible) Present(activate: false);
    }

    private void Show(RECT anchor, IntPtr taskbar)
    {
        if (anchor.Width <= 0) return;
        _anchor = anchor;
        _taskbar = taskbar;
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd))
        {
            _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_LAYERED, WindowClass, "Usage details",
                WS_POPUP, 0, 0, 1, 1, _owner, IntPtr.Zero, _instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                Log.Error($"Couldn't create the details panel (error {Marshal.GetLastWin32Error()})");
                return;
            }
        }

        Present(activate: true);
    }

    public void Hide()
    {
        if (!Visible) return;
        ShowWindow(_hwnd, SW_HIDE);
        _hiddenAt = DateTime.UtcNow;
    }

    private void Present(bool activate)
    {
        var dpi = GetDpiForWindow(_taskbar != IntPtr.Zero ? _taskbar : _owner);
        var scale = (dpi == 0 ? 96 : dpi) / 96f;
        var palette = PanelPalette.For(_light);
        var (pixels, width, height) = _renderer.Render(_content, scale, palette);

        // Above the widget, right-aligned with it, inside the monitor's work area. Below it if
        // the taskbar is at the top of the screen.
        var monitor = MonitorFromWindow(_taskbar != IntPtr.Zero ? _taskbar : _owner, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        var work = GetMonitorInfoW(monitor, ref info) ? info.rcWork : new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        var margin = PanelRenderer.ShadowMargin(scale);
        var gap = (int)Math.Round(8 * scale);
        var x = _anchor.Right - width + margin;
        x = Math.Clamp(x, work.Left + gap - margin, Math.Max(work.Left, work.Right - width - gap + margin));
        var taskbarAtTop = _anchor.Top <= work.Top;
        var y = taskbarAtTop ? work.Top + gap - margin : work.Bottom - height - gap + margin;

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            },
        };

        var bitmap = CreateDIBSection(memDc, ref bmi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        if (bitmap != IntPtr.Zero && bits != IntPtr.Zero)
        {
            var old = SelectObject(memDc, bitmap);
            CopyPixels(pixels, bits);
            var size = new SIZE { Cx = width, Cy = height };
            var source = new POINT();
            var dest = new POINT { X = x, Y = y };
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
            var destPtr = Marshal.AllocHGlobal(Marshal.SizeOf<POINT>());
            try
            {
                Marshal.StructureToPtr(dest, destPtr, false);
                if (!UpdateLayeredWindow(_hwnd, screenDc, destPtr, ref size, memDc, ref source, 0, ref blend, ULW_ALPHA))
                {
                    Log.Error($"UpdateLayeredWindow failed for the details panel (error {Marshal.GetLastWin32Error()})");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(destPtr);
            }

            SelectObject(memDc, old);
            DeleteObject(bitmap);
        }

        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);

        if (activate)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, x, y, width, height, SWP_SHOWWINDOW);
            // Taking focus is what lets a click anywhere else close the panel (WM_ACTIVATE).
            if (!SetForegroundWindow(_hwnd)) Log.Verbose("Details panel couldn't take focus; click the widget again to close it");
        }
    }

    private static unsafe void CopyPixels(uint[] pixels, IntPtr bits)
    {
        fixed (uint* src = pixels)
        {
            Buffer.MemoryCopy(src, (void*)bits, pixels.Length * 4L, pixels.Length * 4L);
        }
    }

    public IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_ACTIVATE:
                if ((wParam.ToInt64() & 0xFFFF) == WA_INACTIVE) Hide();
                return IntPtr.Zero;

            case WM_KEYDOWN:
                if (wParam.ToInt64() == VK_ESCAPE) Hide();
                return IntPtr.Zero;

            case WM_NCHITTEST:
                return new IntPtr(HTCLIENT);
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd)) DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        _renderer.Dispose();
    }
}

internal sealed record PanelPalette(Color Background, Color Border, Color Text, Color Muted, Color Divider, BandPalette Band)
{
    public static PanelPalette For(bool light) => light
        ? new PanelPalette(Color.FromArgb(255, 249, 249, 249), Color.FromArgb(26, 0, 0, 0), Color.FromArgb(255, 26, 26, 26),
            Color.FromArgb(255, 96, 96, 96), Color.FromArgb(20, 0, 0, 0), BandPalette.For(lightTaskbar: true))
        : new PanelPalette(Color.FromArgb(255, 44, 44, 44), Color.FromArgb(28, 255, 255, 255), Color.FromArgb(255, 255, 255, 255),
            Color.FromArgb(255, 170, 170, 170), Color.FromArgb(24, 255, 255, 255), BandPalette.For(lightTaskbar: false));
}

/// <summary>Draws the details panel card, with a soft shadow, into premultiplied pixels.</summary>
internal sealed class PanelRenderer : IDisposable
{
    private const string FontFamilyName = "Segoe UI";
    private readonly StringFormat _format;
    private float _fontScale;
    private Font? _title;
    private Font? _body;
    private Font? _small;

    public PanelRenderer()
    {
        _format = (StringFormat)StringFormat.GenericTypographic.Clone();
        _format.FormatFlags |= StringFormatFlags.NoClip;
    }

    public static int ShadowMargin(float scale) => (int)Math.Round(12 * scale);

    public (uint[] Pixels, int Width, int Height) Render(IReadOnlyList<PanelProvider> providers, float scale, PanelPalette palette)
    {
        EnsureFonts(scale);
        var margin = ShadowMargin(scale);
        var cardWidth = (int)Math.Round(300 * scale);
        var cardHeight = (int)Math.Ceiling(Measure(providers, cardWidth, scale));
        var width = cardWidth + 2 * margin;
        var height = cardHeight + 2 * margin;

        var pixels = new uint[width * height];
        using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb))
        {
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                var card = new RectangleF(margin, margin, cardWidth, cardHeight);
                var radius = 8 * scale;
                DrawShadow(g, card, radius, margin);
                using (var path = BandRenderer.RoundedRect(card, radius))
                using (var bg = new SolidBrush(palette.Background))
                using (var border = new Pen(palette.Border, Math.Max(1, scale)))
                {
                    g.FillPath(bg, path);
                    g.DrawPath(border, path);
                }

                Draw(g, providers, card, scale, palette);
            }

            BandRenderer.CopyOut(bitmap, pixels, width, height);
        }

        return (pixels, width, height);
    }

    private static void DrawShadow(Graphics g, RectangleF card, float radius, int margin)
    {
        // A cheap soft shadow: stacked, slightly offset rounded rectangles with low alpha.
        for (var i = margin; i > 0; i -= 2)
        {
            var spread = i;
            var r = new RectangleF(card.X - spread / 2f, card.Y - spread / 2f + margin / 4f, card.Width + spread, card.Height + spread);
            using var path = BandRenderer.RoundedRect(r, radius + spread / 2f);
            using var brush = new SolidBrush(Color.FromArgb(Math.Max(2, 14 - i), 0, 0, 0));
            g.FillPath(brush, path);
        }
    }

    // Layout constants, in 100%-scale pixels.
    private const float Pad = 16, LabelWidth = 56, HeaderHeight = 20, RowGap = 6, LineHeight = 16, BarHeight = 6, SparkHeight = 30, SectionGap = 14;

    private float Measure(IReadOnlyList<PanelProvider> providers, int cardWidth, float s)
    {
        var y = Pad * s;
        for (var i = 0; i < providers.Count; i++)
        {
            if (i > 0) y += SectionGap * 2 * s + 1;
            y += SectionHeight(providers[i], cardWidth, s);
        }

        if (providers.Count == 0) y += LineHeight * s;
        return y + Pad * s;
    }

    private float SectionHeight(PanelProvider p, int cardWidth, float s)
    {
        var h = HeaderHeight * s + RowGap * s;
        h += 2 * (BarHeight * s + 4 * s + LineHeight * s + RowGap * s);
        h += SparkHeight * s;
        if (p.Message is { } m) h += RowGap * s + MessageHeight(m, cardWidth, s);
        return h;
    }

    private float MessageHeight(string message, int cardWidth, float s)
    {
        using var probe = new Bitmap(1, 1);
        using var g = Graphics.FromImage(probe);
        return g.MeasureString(message, _small!, (int)(cardWidth - 2 * Pad * s), _format).Height;
    }

    private void Draw(Graphics g, IReadOnlyList<PanelProvider> providers, RectangleF card, float s, PanelPalette palette)
    {
        var x = card.X + Pad * s;
        var right = card.Right - Pad * s;
        var y = card.Y + Pad * s;
        var band = palette.Band;

        if (providers.Count == 0)
        {
            Text(g, "Loading…", _body!, palette.Muted, x, y);
            return;
        }

        for (var i = 0; i < providers.Count; i++)
        {
            var p = providers[i];
            if (i > 0)
            {
                y += SectionGap * s;
                using var divider = new SolidBrush(palette.Divider);
                g.FillRectangle(divider, x, y, right - x, Math.Max(1, s));
                y += SectionGap * s + 1;
            }

            var accent = band.Accent(p.Kind);

            // Header: colored mark, name, and plan / freshness on the right.
            var mark = 8 * s;
            using (var markPath = BandRenderer.RoundedRect(new RectangleF(x, y + (HeaderHeight * s - mark) / 2, mark, mark), 2 * s))
            using (var markBrush = new SolidBrush(accent))
            {
                g.FillPath(markBrush, markPath);
            }

            Text(g, p.Name, _title!, palette.Text, x + mark + 6 * s, y + 1 * s);
            if (p.Detail is { } detail)
            {
                var w = g.MeasureString(detail, _small!, PointF.Empty, _format).Width;
                Text(g, detail, _small!, palette.Muted, right - w, y + 3 * s);
            }

            y += HeaderHeight * s + RowGap * s;
            y = Row(g, "5-hour", accent, p.Session, p.SessionPace, p.SessionText, p.Dim, x, right, y, s, palette);
            y = Row(g, "Weekly", accent, p.Weekly, p.WeeklyPace, p.WeeklyText, p.Dim, x, right, y, s, palette);

            // Last five hours of 5-hour usage, on a true time axis.
            Text(g, "Last 5h", _small!, palette.Muted, x, y + 2 * s);
            var sparkX = x + LabelWidth * s;
            var severity = p.Session is null ? Severity.Normal : Format.SeverityOf(p.Session.UsedPercent);
            var color = severity == Severity.Normal ? accent : band.BarFor(severity);
            DrawSpark(g, p.History, p.Now, new RectangleF(sparkX, y, right - sparkX, SparkHeight * s), color, palette, s);
            y += SparkHeight * s;

            if (p.Message is { } message)
            {
                y += RowGap * s;
                using var brush = new SolidBrush(band.Warning);
                g.DrawString(message, _small!, brush, new RectangleF(x, y, right - x, 1000), _format);
                y += MessageHeight(message, (int)card.Width, s);
            }
        }
    }

    private float Row(Graphics g, string label, Color accent, UsageWindow? window, double? pace, string text, bool dim, float x, float right, float y, float s, PanelPalette palette)
    {
        var band = palette.Band;
        var barX = x + LabelWidth * s;
        Text(g, label, _small!, palette.Muted, x, y - 4 * s);
        var severity = window is null ? Severity.Normal : Format.SeverityOf(window.UsedPercent);
        // Brand color while there's room, amber or red as the limit gets close.
        var fill = window is null || dim ? palette.Muted : severity == Severity.Normal ? accent : band.BarFor(severity);
        var track = Color.FromArgb(40, palette.Text);
        BandRenderer.DrawBar(g, barX, y, right - barX, BarHeight * s, window?.UsedPercent, track, fill, pace, palette.Text);
        y += BarHeight * s + 4 * s;

        var percent = Format.Percent(window);
        var pctColor = window is null || dim ? palette.Muted : severity == Severity.Normal ? palette.Text : band.For(severity);
        Text(g, percent, _bodyBold!, pctColor, barX, y);
        var pw = g.MeasureString(percent, _bodyBold!, PointF.Empty, _format).Width;
        Text(g, " · " + text, _body!, palette.Muted, barX + pw, y);
        return y + LineHeight * s + RowGap * s;
    }

    private static void DrawSpark(Graphics g, IReadOnlyList<(DateTimeOffset At, double Percent)> history, DateTimeOffset now, RectangleF r, Color color, PanelPalette palette, float s)
    {
        using (var baseline = new SolidBrush(palette.Divider))
        {
            g.FillRectangle(baseline, r.X, r.Bottom - Math.Max(1, s), r.Width, Math.Max(1, s));
        }

        if (history.Count < 2) return;

        var start = now - UsageHistory.Span;
        PointF At((DateTimeOffset At, double Percent) p)
        {
            var fx = (float)Math.Clamp((p.At - start).TotalSeconds / UsageHistory.Span.TotalSeconds, 0, 1);
            var fy = (float)Math.Clamp(p.Percent / 100, 0, 1);
            return new PointF(r.X + fx * r.Width, r.Bottom - 2 * s - fy * (r.Height - 4 * s));
        }

        var points = history.Select(At).ToArray();
        using (var area = new GraphicsPath())
        {
            area.AddLines(points);
            area.AddLine(points[^1], new PointF(points[^1].X, r.Bottom));
            area.AddLine(new PointF(points[^1].X, r.Bottom), new PointF(points[0].X, r.Bottom));
            area.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(46, color));
            g.FillPath(fill, area);
        }

        using (var pen = new Pen(color, 1.5f * s) { LineJoin = LineJoin.Round })
        {
            g.DrawLines(pen, points);
        }

        var last = points[^1];
        var dot = 5 * s;
        using var dotBrush = new SolidBrush(color);
        g.FillEllipse(dotBrush, last.X - dot / 2, last.Y - dot / 2, dot, dot);
    }

    private void Text(Graphics g, string text, Font font, Color color, float x, float y)
    {
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, x, y, _format);
    }

    private Font? _bodyBold;

    private void EnsureFonts(float scale)
    {
        if (_title is not null && Math.Abs(scale - _fontScale) < 0.01f) return;
        _title?.Dispose();
        _body?.Dispose();
        _bodyBold?.Dispose();
        _small?.Dispose();
        _fontScale = scale;
        _title = new Font(FontFamilyName, 14 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        _body = new Font(FontFamilyName, 12 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        _bodyBold = new Font(FontFamilyName, 12 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        _small = new Font(FontFamilyName, 11.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    public void Dispose()
    {
        _title?.Dispose();
        _body?.Dispose();
        _bodyBold?.Dispose();
        _small?.Dispose();
        _format.Dispose();
    }
}
