using System.Drawing;
using System.Runtime.InteropServices;
using UsageTracker.Native;
using static UsageTracker.Native.Win32;

namespace UsageTracker;

/// <summary>Reads small screen areas, used to match the taskbar's background.</summary>
internal static class ScreenSampler
{
    public static Color? Average(int x, int y, int width, int height)
    {
        var pixels = Capture(x, y, width, height);
        if (pixels is null) return null;
        long r = 0, g = 0, b = 0;
        foreach (var p in pixels)
        {
            r += (p >> 16) & 0xFF;
            g += (p >> 8) & 0xFF;
            b += p & 0xFF;
        }

        var n = pixels.Length;
        return Color.FromArgb(255, (int)(r / n), (int)(g / n), (int)(b / n));
    }

    /// <summary>
    /// Averages each row of a narrow screen area, top to bottom, as opaque 0xAARRGGBB values.
    /// Used to copy the taskbar's background (its tint, gradient and top edge line) one row at a time.
    /// </summary>
    public static int[]? Rows(int x, int y, int width, int height)
    {
        var pixels = Capture(x, y, width, height);
        if (pixels is null) return null;
        var rows = new int[height];
        for (var row = 0; row < height; row++)
        {
            long r = 0, g = 0, b = 0;
            for (var col = 0; col < width; col++)
            {
                var p = pixels[row * width + col];
                r += (p >> 16) & 0xFF;
                g += (p >> 8) & 0xFF;
                b += p & 0xFF;
            }

            rows[row] = unchecked((int)0xFF000000) | (int)(r / width) << 16 | (int)(g / width) << 8 | (int)(b / width);
        }

        return rows;
    }

    /// <summary>The largest per-channel difference between two row samples, or int.MaxValue if they don't line up.</summary>
    public static int Distance(int[] a, int[] b)
    {
        if (a.Length != b.Length) return int.MaxValue;
        var worst = 0;
        for (var i = 0; i < a.Length; i++)
        {
            worst = Math.Max(worst, Distance(Color.FromArgb(a[i]), Color.FromArgb(b[i])));
        }

        return worst;
    }

    private static int[]? Capture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0 || width * height > 16384) return null;

        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var info = new BITMAPINFO
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

        var bitmap = CreateDIBSection(mem, ref info, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        try
        {
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return null;
            var old = SelectObject(mem, bitmap);
            var ok = BitBlt(mem, 0, 0, width, height, screen, x, y, SRCCOPY);
            SelectObject(mem, old);
            if (!ok) return null;

            var pixels = new int[width * height];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static int Distance(Color a, Color b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
}
