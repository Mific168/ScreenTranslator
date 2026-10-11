using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenTranslator.Native;

namespace ScreenTranslator.Services;

/// <summary>
/// Рисует значок приложения в коде — проекту не нужен бинарный .ico-файл.
/// </summary>
public static class IconFactory
{
    private static BitmapSource? _windowIcon;

    /// <summary>Значок для окон WPF.</summary>
    public static BitmapSource WindowIcon => _windowIcon ??= Render(32);

    public static BitmapSource Render(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            double s = size;
            var background = new LinearGradientBrush(Color.FromRgb(0x1A, 0x86, 0xE0), Color.FromRgb(0x00, 0x5A, 0xB0), 90);
            dc.DrawRoundedRectangle(background, null, new Rect(0, 0, s, s), s * 0.22, s * 0.22);

            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var text = new FormattedText("T", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, s * 0.72, Brushes.White, 1.0);
            dc.DrawText(text, new Point((s - text.Width) / 2, (s - text.Height) / 2));

            // Небольшая «скобка выделения» в углу — намёк на захват области экрана.
            var pen = new Pen(Brushes.White, Math.Max(1, s / 16));
            double m = s * 0.14, l = s * 0.22;
            dc.DrawLine(pen, new Point(s - m - l, s - m), new Point(s - m, s - m));
            dc.DrawLine(pen, new Point(s - m, s - m - l), new Point(s - m, s - m));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Создаёт HICON для значка в трее. Освобождать через DestroyIcon.</summary>
    public static IntPtr CreateHIcon(int size)
    {
        var bitmap = Render(size);
        int stride = size * 4;
        var pixels = new byte[stride * size];
        bitmap.CopyPixels(pixels, stride, 0);

        // Значки Windows используют непредумноженную альфу.
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            if (a is > 0 and < 255)
            {
                pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / a);
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / a);
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / a);
            }
        }

        var header = new NativeMethods.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size, // top-down
            biPlanes = 1,
            biBitCount = 32
        };

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr color = NativeMethods.CreateDIBSection(screenDc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        if (color == IntPtr.Zero) return IntPtr.Zero;

        Marshal.Copy(pixels, 0, bits, pixels.Length);
        IntPtr mask = NativeMethods.CreateBitmap(size, size, 1, 1, new byte[(size + 15) / 16 * 2 * size]);

        var info = new NativeMethods.ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        IntPtr icon = NativeMethods.CreateIconIndirect(ref info);

        NativeMethods.DeleteObject(color);
        NativeMethods.DeleteObject(mask);
        return icon;
    }
}
