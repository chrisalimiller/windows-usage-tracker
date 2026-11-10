using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using UsageTracker.Core;

namespace UsageTracker;

/// <summary>One provider's line in the taskbar widget.</summary>
internal sealed record BandRow(ProviderKind Kind, string Label, UsageWindow? Session, UsageWindow? Weekly, bool Dim, bool NeedsAttention);

/// <summary>Colors that read well on the current taskbar.</summary>
internal sealed record BandPalette(Color Text, Color Muted, Color Warning, Color Critical, Color Hover, Color Track, Color Fill, Color Claude, Color Codex)
{
    public static BandPalette For(bool lightTaskbar) => lightTaskbar
        ? new BandPalette(
            Text: Color.FromArgb(255, 26, 26, 26), Muted: Color.FromArgb(150, 0, 0, 0),
            Warning: Color.FromArgb(255, 157, 93, 0), Critical: Color.FromArgb(255, 196, 43, 28),
            Hover: Color.FromArgb(18, 0, 0, 0), Track: Color.FromArgb(34, 0, 0, 0), Fill: Color.FromArgb(190, 0, 0, 0),
            Claude: Color.FromArgb(255, 194, 96, 62), Codex: Color.FromArgb(255, 61, 111, 214))
        : new BandPalette(
            Text: Color.FromArgb(255, 255, 255, 255), Muted: Color.FromArgb(165, 255, 255, 255),
            Warning: Color.FromArgb(255, 255, 185, 0), Critical: Color.FromArgb(255, 255, 99, 97),
            Hover: Color.FromArgb(24, 255, 255, 255), Track: Color.FromArgb(48, 255, 255, 255), Fill: Color.FromArgb(225, 255, 255, 255),
            Claude: Color.FromArgb(255, 224, 138, 106), Codex: Color.FromArgb(255, 143, 176, 245));

    public Color For(Severity severity) => severity switch
    {
        Severity.Critical => Critical,
        Severity.Warning => Warning,
        _ => Text,
    };

    public Color BarFor(Severity severity) => severity switch
    {
        Severity.Critical => Critical,
        Severity.Warning => Warning,
        _ => Fill,
    };

    public Color Accent(ProviderKind kind) => kind == ProviderKind.Claude ? Claude : Codex;
}

/// <summary>How the widget is drawn this time.</summary>
internal sealed record BandStyle(bool Compact, bool Hover, Color? Backing, Color? TopLine);

/// <summary>
/// Draws the widget into a premultiplied 32-bit pixel buffer for UpdateLayeredWindow.
/// Full form: two rows of "Label 5h [bar] 23%  7d [bar] 41%". Compact form: one small
/// ring per provider showing whichever limit is closer.
/// Text uses grayscale antialiasing because ClearType needs an opaque background.
/// </summary>
internal sealed class BandRenderer : IDisposable
{
    private const string FontFamilyName = "Segoe UI";
    private const string SessionPrefix = "5h";
    private const string WeeklyPrefix = "7d";

    private readonly StringFormat _format;
    private float _fontPx;
    private Font? _label;
    private Font? _value;
    private Font? _ringFont;

    public BandRenderer()
    {
        _format = (StringFormat)StringFormat.GenericTypographic.Clone();
        _format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
    }

    private sealed record Layout(
        float LabelX, float SessionX, float WeeklyX, float PrefixWidth, float BarWidth, float BarHeight,
        float ValueWidth, float LineHeight, float Top, float RowGap, float Gap, int Width);

    /// <summary>Width in pixels the full form needs at this height and scale.</summary>
    public int MeasureWidth(IReadOnlyList<BandRow> rows, int height, float scale) => ComputeLayout(rows, height, scale).Width;

    /// <summary>Width in pixels the compact form needs.</summary>
    public static int MeasureCompactWidth(IReadOnlyList<BandRow> rows, int height, float scale)
    {
        var count = Math.Max(1, rows.Count);
        var d = RingDiameter(height, scale);
        return (int)Math.Ceiling(count * d + (count - 1) * 6 * scale + 2 * 6 * scale);
    }

    private static float RingDiameter(int height, float scale) => Math.Min(30 * scale, height - 10 * scale);

    /// <summary>Renders and returns premultiplied BGRA pixels, top-down, width * height.</summary>
    public uint[] Render(IReadOnlyList<BandRow> rows, int width, int height, float scale, BandPalette palette, BandStyle style)
    {
        var pixels = new uint[width * height];
        using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb))
        {
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                if (style.Backing is { } backing)
                {
                    using var fill = new SolidBrush(backing);
                    g.FillRectangle(fill, 0, 0, width, height);
                    if (style.TopLine is { } line)
                    {
                        using var pen = new SolidBrush(line);
                        g.FillRectangle(pen, 0, 0, width, Math.Max(1, (int)Math.Round(scale)));
                    }
                }

                var contentWidth = style.Compact ? MeasureCompactWidth(rows, height, scale) : MeasureWidth(rows, height, scale);
                var offset = Math.Max(0, (width - contentWidth) / 2f);

                if (style.Hover)
                {
                    var inset = 3 * scale;
                    using var path = RoundedRect(new RectangleF(offset + 1, inset, contentWidth - 2, height - 2 * inset), 4 * scale);
                    using var brush = new SolidBrush(palette.Hover);
                    g.FillPath(brush, path);
                }

                if (style.Compact) DrawCompact(g, rows, height, scale, palette, offset);
                else DrawFull(g, rows, height, scale, palette, offset);
            }

            CopyOut(bitmap, pixels, width, height);
        }

        // Layered windows let clicks fall through fully transparent pixels. An alpha of 1
        // keeps the whole widget clickable and hoverable without being visible.
        for (var i = 0; i < pixels.Length; i++)
        {
            if (pixels[i] >> 24 == 0) pixels[i] = 0x01000000;
        }

        return pixels;
    }

    private void DrawFull(Graphics g, IReadOnlyList<BandRow> rows, int height, float scale, BandPalette palette, float offset)
    {
        var layout = ComputeLayout(rows, height, scale);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var y = layout.Top + i * (layout.LineHeight + layout.RowGap);
            DrawText(g, row.Label, _label!, row.NeedsAttention ? palette.Warning : palette.Text, offset + layout.LabelX, y);
            DrawMeter(g, SessionPrefix, row.Session, row.Dim, palette, offset + layout.SessionX, y, layout);
            DrawMeter(g, WeeklyPrefix, row.Weekly, row.Dim, palette, offset + layout.WeeklyX, y, layout);
        }
    }

    private void DrawMeter(Graphics g, string prefix, UsageWindow? window, bool dim, BandPalette palette, float x, float y, Layout layout)
    {
        DrawText(g, prefix, _value!, palette.Muted, x, y);

        var barX = x + layout.PrefixWidth;
        var barY = y + (layout.LineHeight - layout.BarHeight) / 2f + layout.BarHeight * 0.15f;
        var severity = window is null ? Severity.Normal : Format.SeverityOf(window.UsedPercent);
        DrawBar(g, barX, barY, layout.BarWidth, layout.BarHeight, window?.UsedPercent, palette.Track,
            window is null || dim ? palette.Muted : palette.BarFor(severity), tick: null, tickColor: palette.Text);

        var text = Format.Percent(window);
        var color = window is null || dim ? palette.Muted : palette.For(severity);
        var textWidth = g.MeasureString(text, _value!, PointF.Empty, _format).Width;
        var valueRight = barX + layout.BarWidth + layout.Gap + layout.ValueWidth;
        DrawText(g, text, _value!, color, valueRight - textWidth, y);
    }

    /// <summary>A rounded bar with an optional tick; shared with the details panel.</summary>
    internal static void DrawBar(Graphics g, float x, float y, float width, float height, double? percent, Color track, Color fill, double? tick, Color tickColor)
    {
        var radius = height / 2f;
        using (var trackPath = RoundedRect(new RectangleF(x, y, width, height), radius))
        using (var trackBrush = new SolidBrush(track))
        {
            g.FillPath(trackBrush, trackPath);
        }

        if (percent is { } p && p > 0)
        {
            var w = Math.Max(height, (float)(width * Math.Min(100, p) / 100));
            using var fillPath = RoundedRect(new RectangleF(x, y, w, height), radius);
            using var fillBrush = new SolidBrush(fill);
            g.FillPath(fillBrush, fillPath);
        }

        if (tick is { } t)
        {
            var tx = x + (float)(width * Math.Clamp(t, 0, 1));
            var tw = Math.Max(1.5f, height * 0.4f);
            using var tickBrush = new SolidBrush(tickColor);
            g.FillRectangle(tickBrush, tx - tw / 2, y - height * 0.45f, tw, height * 1.9f);
        }
    }

    private void DrawCompact(Graphics g, IReadOnlyList<BandRow> rows, int height, float scale, BandPalette palette, float offset)
    {
        var d = RingDiameter(height, scale);
        var stroke = 3.5f * scale;
        EnsureRingFont(d * 0.36f);
        var x = offset + 6 * scale;
        var y = (height - d) / 2f;
        foreach (var row in rows)
        {
            var worst = new[] { row.Session, row.Weekly }.Where(w => w is not null).MaxBy(w => w!.UsedPercent);
            var rect = new RectangleF(x + stroke / 2, y + stroke / 2, d - stroke, d - stroke);
            using (var trackPen = new Pen(palette.Track, stroke))
            {
                g.DrawEllipse(trackPen, rect);
            }

            if (worst is not null && worst.UsedPercent > 0)
            {
                var color = row.Dim ? palette.Muted : palette.BarFor(Format.SeverityOf(worst.UsedPercent));
                using var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(pen, rect, -90, (float)(360 * Math.Min(100, worst.UsedPercent) / 100));
            }

            var letter = row.Kind == ProviderKind.Claude ? "C" : "X";
            var size = g.MeasureString(letter, _ringFont!, PointF.Empty, _format);
            DrawText(g, letter, _ringFont!, row.NeedsAttention ? palette.Warning : palette.Accent(row.Kind),
                x + (d - size.Width) / 2, y + (d - size.Height) / 2);
            x += d + 6 * scale;
        }
    }

    private void DrawText(Graphics g, string text, Font font, Color color, float x, float y)
    {
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, x, y, _format);
    }

    private Layout ComputeLayout(IReadOnlyList<BandRow> rows, int height, float scale)
    {
        var count = Math.Max(1, rows.Count);
        var rowGap = 1 * scale;
        // 12px at 100% matches the taskbar clock; shrink if the taskbar is unusually short.
        var fontPx = Math.Min(12f * scale, (height - 8 * scale - rowGap * (count - 1)) / (count * 1.35f));
        fontPx = Math.Max(fontPx, 7f);
        EnsureFonts(fontPx);

        using var probe = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(probe);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        float Measure(string s, Font f) => g.MeasureString(s, f, PointF.Empty, _format).Width;

        var labelWidth = rows.Count == 0 ? Measure("Claude", _label!) : rows.Max(r => Measure(r.Label, _label!));
        var prefixWidth = Math.Max(Measure(SessionPrefix, _value!), Measure(WeeklyPrefix, _value!)) + 4 * scale;
        var valueWidth = Measure("100%", _value!);
        var lineHeight = _value!.GetHeight(g);
        var barWidth = 28 * scale;
        var barHeight = Math.Max(3f, 4 * scale);
        var gap = 4 * scale;

        var padX = 8 * scale;
        var labelX = padX;
        var meterWidth = prefixWidth + barWidth + gap + valueWidth;
        var sessionX = labelX + labelWidth + 7 * scale;
        var weeklyX = sessionX + meterWidth + 9 * scale;
        var width = (int)Math.Ceiling(weeklyX + meterWidth + padX);
        var block = count * lineHeight + (count - 1) * rowGap;
        var top = (height - block) / 2f;
        return new Layout(labelX, sessionX, weeklyX, prefixWidth, barWidth, barHeight, valueWidth, lineHeight, top, rowGap, gap, width);
    }

    private void EnsureFonts(float px)
    {
        if (_label is not null && Math.Abs(px - _fontPx) < 0.01f) return;
        _label?.Dispose();
        _value?.Dispose();
        _fontPx = px;
        _label = new Font(FontFamilyName, px, FontStyle.Bold, GraphicsUnit.Pixel);
        _value = new Font(FontFamilyName, px, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private float _ringFontPx;

    private void EnsureRingFont(float px)
    {
        if (_ringFont is not null && Math.Abs(px - _ringFontPx) < 0.01f) return;
        _ringFont?.Dispose();
        _ringFontPx = px;
        _ringFont = new Font(FontFamilyName, px, FontStyle.Bold, GraphicsUnit.Pixel);
    }

    internal static void CopyOut(Bitmap bitmap, uint[] pixels, int width, int height)
    {
        var rect = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            unsafe
            {
                fixed (uint* dst = pixels)
                {
                    for (var y = 0; y < height; y++)
                    {
                        var src = (void*)(data.Scan0 + y * data.Stride);
                        Buffer.MemoryCopy(src, dst + y * width, width * 4L, width * 4L);
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    internal static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        _label?.Dispose();
        _value?.Dispose();
        _ringFont?.Dispose();
        _format.Dispose();
    }
}
