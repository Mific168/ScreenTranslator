using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenTranslator.Models;
using ScreenTranslator.Native;

namespace ScreenTranslator.Views;

/// <summary>
/// Полноэкранное окно выделения для одного монитора. Окно создаётся на каждом мониторе отдельно,
/// поэтому WPF применяет масштаб именно этого монитора (125%, 150%, 200% …), а итоговые
/// координаты берутся через PointToScreen — в физических пикселях.
/// </summary>
public partial class SelectionOverlay : Window
{
    private const double MinSelectionPixels = 4;

    private readonly RectangleGeometry _fullRect = new();
    private readonly RectangleGeometry _hole = new();
    private Point _start;
    private bool _dragging;

    public event Action<PixelRect>? Selected;
    public event Action? Cancelled;

    public MonitorInfo Monitor { get; }

    public SelectionOverlay(MonitorInfo monitor, BitmapSource monitorImage, string? hint = null)
    {
        InitializeComponent();
        Monitor = monitor;
        ScreenImage.Source = monitorImage;
        if (hint != null) HintText.Text = hint;

        var shade = new GeometryGroup { FillRule = FillRule.EvenOdd };
        shade.Children.Add(_fullRect);
        shade.Children.Add(_hole);
        Shade.Data = shade;

        SourceInitialized += (_, _) =>
        {
            PlaceOnMonitor();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        };
        Loaded += (_, _) => PlaceOnMonitor();
        SizeChanged += (_, _) => _fullRect.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
    }

    /// <summary>Растягивает окно точно на физические границы монитора.</summary>
    private void PlaceOnMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var b = Monitor.Bounds;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, NativeMethods.SWP_NOACTIVATE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_DPICHANGED)
        {
            // WPF подстроит масштаб под новый монитор, а мы вернём точный размер окна.
            Dispatcher.BeginInvoke(PlaceOnMonitor, DispatcherPriority.Render);
        }
        return IntPtr.Zero;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _start = Clamp(e.GetPosition(Root));
        _dragging = true;
        CaptureMouse();
        Hint.Visibility = Visibility.Collapsed;
        UpdateSelection(_start);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) UpdateSelection(Clamp(e.GetPosition(Root)));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        var end = Clamp(e.GetPosition(Root));
        var rect = ToPhysical(_start, end);
        if (rect.Width < MinSelectionPixels || rect.Height < MinSelectionPixels)
        {
            ResetSelection(); // случайный клик — продолжаем выделение
            return;
        }
        Selected?.Invoke(rect);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
            ResetSelection();
        }
        else
        {
            Cancelled?.Invoke();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancelled?.Invoke();
        }
    }

    private Point Clamp(Point p) =>
        new(Math.Clamp(p.X, 0, ActualWidth), Math.Clamp(p.Y, 0, ActualHeight));

    private void UpdateSelection(Point current)
    {
        var rect = new Rect(_start, current);
        _hole.Rect = rect;

        SelectionBorder.Visibility = Visibility.Visible;
        SelectionBorder.Margin = new Thickness(rect.X, rect.Y, 0, 0);
        SelectionBorder.Width = Math.Max(1, rect.Width);
        SelectionBorder.Height = Math.Max(1, rect.Height);

        var dpi = VisualTreeHelper.GetDpi(this);
        SizeText.Text = $"{Math.Round(rect.Width * dpi.DpiScaleX)} × {Math.Round(rect.Height * dpi.DpiScaleY)}";
        SizeLabel.Visibility = Visibility.Visible;
        double labelTop = rect.Y >= 26 ? rect.Y - 24 : rect.Bottom + 4;
        SizeLabel.Margin = new Thickness(rect.X, labelTop, 0, 0);
    }

    private void ResetSelection()
    {
        _hole.Rect = Rect.Empty;
        SelectionBorder.Visibility = Visibility.Collapsed;
        SizeLabel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Перевод двух точек окна (DIP) в прямоугольник в физических пикселях экрана.</summary>
    private PixelRect ToPhysical(Point a, Point b)
    {
        var p1 = PointToScreen(a);
        var p2 = PointToScreen(b);
        int left = (int)Math.Floor(Math.Min(p1.X, p2.X));
        int top = (int)Math.Floor(Math.Min(p1.Y, p2.Y));
        int right = (int)Math.Ceiling(Math.Max(p1.X, p2.X));
        int bottom = (int)Math.Ceiling(Math.Max(p1.Y, p2.Y));

        var r = PixelRect.FromLTRB(left, top, right, bottom).Intersect(Monitor.Bounds);
        return r;
    }
}
