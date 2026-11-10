using System.Drawing;
using System.Runtime.InteropServices;
using UsageTracker.Native;
using static UsageTracker.Native.Win32;

namespace UsageTracker;

/// <summary>Reads the average color of a small screen area, used to match the taskbar's background.</summary>
internal static class ScreenSampler
{
    public static Color? Average(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0 || width * height > 4096) return null;

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
