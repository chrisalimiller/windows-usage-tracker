using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using UsageTracker.Core;

namespace UsageTracker;

/// <summary>One line in the taskbar widget.</summary>
internal sealed record BandRow(string Label, UsageWindow? Session, UsageWindow? Weekly, bool Dim, bool NeedsAttention);

/// <summary>Colors that read well on the current taskbar.</summary>
internal sealed record BandPalette(Color Text, Color Muted, Color Warning, Color Critical, Color Hover)
{
    public static BandPalette For(bool lightTaskbar) => lightTaskbar
        ? new BandPalette(Color.FromArgb(255, 26, 26, 26), Color.FromArgb(150, 0, 0, 0),
            Color.FromArgb(255, 157, 93, 0), Color.FromArgb(255, 196, 43, 28), Color.FromArgb(18, 0, 0, 0))
        : new BandPalette(Color.FromArgb(255, 255, 255, 255), Color.FromArgb(165, 255, 255, 255),
            Color.FromArgb(255, 255, 185, 0), Color.FromArgb(255, 255, 99, 97), Color.FromArgb(24, 255, 255, 255));

    public Color For(Severity severity) => severity switch
    {
        Severity.Critical => Critical,
        Severity.Warning => Warning,
        _ => Text,
    };
}

/// <summary>
/// Draws the widget into a premultiplied 32-bit pixel buffer for UpdateLayeredWindow.
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

    public BandRenderer()
    {
        _format = (StringFormat)StringFormat.GenericTypographic.Clone();
        _format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
    }

    private sealed record Layout(float LabelX, float SessionX, float WeeklyX, float PrefixWidth, float ValueWidth, float LineHeight, float Top, float RowGap, int Width);

    /// <summary>Width in pixels the rows need at this height and scale.</summary>
    public int MeasureWidth(IReadOnlyList<BandRow> rows, int height, float scale) => ComputeLayout(rows, height, scale).Width;

    /// <summary>Renders and returns premultiplied BGRA pixels, top-down, width * height.</summary>
    public uint[] Render(IReadOnlyList<BandRow> rows, int width, int height, float scale, BandPalette palette, bool hover)
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

                if (hover)
                {
                    var inset = 3 * scale;
                    using var path = RoundedRect(new RectangleF(1, inset, width - 2, height - 2 * inset), 4 * scale);
                    using var brush = new SolidBrush(palette.Hover);
                    g.FillPath(brush, path);
                }

                var layout = ComputeLayout(rows, height, scale);
                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    var y = layout.Top + i * (layout.LineHeight + layout.RowGap);
                    DrawText(g, row.Label, _label!, row.NeedsAttention ? palette.Warning : palette.Text, layout.LabelX, y);
                    DrawWindow(g, SessionPrefix, row.Session, row.Dim, palette, layout.SessionX, y, layout.PrefixWidth);
                    DrawWindow(g, WeeklyPrefix, row.Weekly, row.Dim, palette, layout.WeeklyX, y, layout.PrefixWidth);
                }
            }

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

        // Layered windows let clicks fall through fully transparent pixels. An alpha of 1
        // keeps the whole widget clickable and hoverable without being visible.
        for (var i = 0; i < pixels.Length; i++)
        {
            if (pixels[i] >> 24 == 0) pixels[i] = 0x01000000;
        }

        return pixels;
    }

    private void DrawWindow(Graphics g, string prefix, UsageWindow? window, bool dim, BandPalette palette, float x, float y, float prefixWidth)
    {
        DrawText(g, prefix, _value!, palette.Muted, x, y);
        var color = window is null || dim ? palette.Muted : palette.For(Format.SeverityOf(window.UsedPercent));
        DrawText(g, Format.Percent(window), _value!, color, x + prefixWidth, y);
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
        var prefixWidth = Math.Max(Measure(SessionPrefix, _value!), Measure(WeeklyPrefix, _value!)) + 3 * scale;
        var valueWidth = Measure("100%", _value!);
        var lineHeight = _value!.GetHeight(g);

        var padX = 8 * scale;
        var labelX = padX;
        var sessionX = labelX + labelWidth + 7 * scale;
        var weeklyX = sessionX + prefixWidth + valueWidth + 7 * scale;
        var width = (int)Math.Ceiling(weeklyX + prefixWidth + valueWidth + padX);
        var block = count * lineHeight + (count - 1) * rowGap;
        var top = (height - block) / 2f;
        return new Layout(labelX, sessionX, weeklyX, prefixWidth, valueWidth, lineHeight, top, rowGap, width);
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

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
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
        _format.Dispose();
    }
}
