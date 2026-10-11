using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenTranslator.Models;
using ScreenTranslator.Native;

namespace ScreenTranslator.Services;

/// <summary>Снимок всего виртуального рабочего стола (всех мониторов сразу).</summary>
public sealed class CapturedScreen
{
    public CapturedScreen(BitmapSource image, PixelRect bounds, IReadOnlyList<MonitorInfo> monitors)
    {
        Image = image;
        Bounds = bounds;
        Monitors = monitors;
    }

    public BitmapSource Image { get; }
    public PixelRect Bounds { get; }
    public IReadOnlyList<MonitorInfo> Monitors { get; }

    /// <summary>Часть снимка без копирования пикселей (для оверлея выделения).</summary>
    public BitmapSource View(PixelRect rect)
    {
        var r = rect.Intersect(Bounds);
        var cropped = new CroppedBitmap(Image, new Int32Rect(r.X - Bounds.X, r.Y - Bounds.Y, r.Width, r.Height));
        cropped.Freeze();
        return cropped;
    }

    /// <summary>Независимая копия области: большой снимок после этого можно освободить.</summary>
    public BitmapSource Crop(PixelRect rect) => ScreenService.Detach(View(rect));
}

/// <summary>Мониторы, DPI и захват экрана через GDI BitBlt.</summary>
public static class ScreenService
{
    public static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        NativeMethods.MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
        {
            var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (!NativeMethods.GetMonitorInfo(hMonitor, ref info)) return true;

            uint dpi = 96;
            try
            {
                if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0)
                    dpi = dpiX;
            }
            catch (DllNotFoundException)
            {
                // очень старая система — остаётся 96
            }

            list.Add(new MonitorInfo(
                info.szDevice,
                ToPixelRect(info.rcMonitor),
                ToPixelRect(info.rcWork),
                dpi,
                (info.dwFlags & 1) != 0));
            return true;
        };

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return list;
    }

    /// <summary>Монитор, на котором находится большая часть прямоугольника.</summary>
    public static MonitorInfo MonitorFor(PixelRect rect, IReadOnlyList<MonitorInfo>? monitors = null)
    {
        monitors ??= GetMonitors();
        return monitors
                   .OrderByDescending(m =>
                   {
                       var i = m.Bounds.Intersect(rect);
                       return (long)i.Width * i.Height;
                   })
                   .ThenByDescending(m => m.IsPrimary)
                   .FirstOrDefault()
               ?? new MonitorInfo("", rect, rect, 96, true);
    }

    public static MonitorInfo MonitorUnderCursor()
    {
        NativeMethods.GetCursorPos(out var pt);
        var monitors = GetMonitors();
        return monitors.FirstOrDefault(m => m.Bounds.Contains(pt.X, pt.Y))
               ?? monitors.FirstOrDefault(m => m.IsPrimary)
               ?? MonitorFor(new PixelRect(pt.X, pt.Y, 1, 1), monitors);
    }

    public static CapturedScreen CaptureVirtualScreen()
    {
        var monitors = GetMonitors();
        var bounds = monitors.Aggregate(default(PixelRect), (acc, m) => acc.Union(m.Bounds));
        return new CapturedScreen(CaptureRegion(bounds), bounds, monitors);
    }

    /// <summary>Захват области экрана в физических пикселях.</summary>
    public static BitmapSource CaptureRegion(PixelRect r)
    {
        if (r.IsEmpty) throw new ArgumentException("Пустая область захвата.");

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
        var header = new NativeMethods.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
            biWidth = r.Width,
            biHeight = -r.Height,
            biPlanes = 1,
            biBitCount = 32
        };
        IntPtr bitmap = NativeMethods.CreateDIBSection(screenDc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero)
        {
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось создать буфер для снимка экрана.");
        }

        IntPtr old = NativeMethods.SelectObject(memDc, bitmap);
        try
        {
            if (!NativeMethods.BitBlt(memDc, 0, 0, r.Width, r.Height, screenDc, r.X, r.Y,
                    NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось скопировать изображение экрана.");

            int stride = r.Width * 4;
            var pixels = new byte[stride * r.Height];
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            var source = BitmapSource.Create(r.Width, r.Height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.SelectObject(memDc, old);
            NativeMethods.DeleteObject(bitmap);
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Копия пикселей, не ссылающаяся на исходный (большой) снимок.</summary>
    public static BitmapSource Detach(BitmapSource source)
    {
        int stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        var bgr = source.Format == PixelFormats.Bgr32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
        bgr.CopyPixels(pixels, stride, 0);
        var copy = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        copy.Freeze();
        return copy;
    }

    /// <summary>
    /// Восстанавливает сохранённую область с учётом текущей конфигурации мониторов.
    /// Возвращает null, если область больше не помещается на экран.
    /// </summary>
    public static PixelRect? ResolveSavedRegion(SavedRegion saved, IReadOnlyList<MonitorInfo> monitors)
    {
        var rect = saved.Rect;
        if (rect.IsEmpty || monitors.Count == 0) return null;

        var monitor = monitors.FirstOrDefault(m =>
            string.Equals(m.DeviceName, saved.MonitorDevice, StringComparison.OrdinalIgnoreCase));

        if (monitor != null && saved.MonitorWidth > 0 && saved.MonitorHeight > 0)
        {
            var oldBounds = saved.MonitorBounds;
            PixelRect candidate;
            if (monitor.Bounds == oldBounds)
            {
                candidate = rect;
            }
            else
            {
                // Монитор переместился или сменил разрешение: сохраняем относительное положение.
                double sx = monitor.Bounds.Width / (double)oldBounds.Width;
                double sy = monitor.Bounds.Height / (double)oldBounds.Height;
                candidate = new PixelRect(
                    monitor.Bounds.X + (int)Math.Round((rect.X - oldBounds.X) * sx),
                    monitor.Bounds.Y + (int)Math.Round((rect.Y - oldBounds.Y) * sy),
                    Math.Max(1, (int)Math.Round(rect.Width * sx)),
                    Math.Max(1, (int)Math.Round(rect.Height * sy)));
            }
            return monitor.Bounds.Contains(candidate) ? candidate : null;
        }

        // Монитор не найден: годится, только если область целиком лежит на каком-либо мониторе.
        return monitors.Any(m => m.Bounds.Contains(rect)) ? rect : null;
    }

    private static PixelRect ToPixelRect(NativeMethods.RECT r) => PixelRect.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
}
