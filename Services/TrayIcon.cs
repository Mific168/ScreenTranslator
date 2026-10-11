using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using ScreenTranslator.Native;

namespace ScreenTranslator.Services;

/// <summary>
/// Значок в области уведомлений на Shell_NotifyIcon (без WinForms).
/// Контекстное меню — обычный WPF ContextMenu, поэтому оно следует теме приложения.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = NativeMethods.WM_APP + 1;
    private const int IconId = 1;

    private readonly MessageWindow _window;
    private readonly ContextMenu _menu;
    private readonly int _taskbarCreatedMessage;
    private IntPtr _hIcon;
    private string _tooltip;
    private bool _added;

    public event Action? DoubleClick;

    public TrayIcon(MessageWindow window, ContextMenu menu, string tooltip)
    {
        _window = window;
        _menu = menu;
        _tooltip = tooltip;
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

        int size = (int)Math.Round(16 * NativeMethods.GetDpiForSystem() / 96.0);
        _hIcon = IconFactory.CreateHIcon(Math.Max(16, size));

        _window.AddHook(WndProc);
        Add();
    }

    public void SetTooltip(string tooltip)
    {
        _tooltip = tooltip;
        var data = CreateData(NativeMethods.NIF_TIP);
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    /// <summary>Всплывающее уведомление Windows.</summary>
    public void ShowBalloon(string title, string text, bool isError = false)
    {
        var data = CreateData(NativeMethods.NIF_INFO);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = isError ? NativeMethods.NIIF_WARNING : NativeMethods.NIIF_INFO;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    private void Add()
    {
        var data = CreateData(NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP);
        _added = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
    }

    private NativeMethods.NOTIFYICONDATA CreateData(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _hIcon,
        szTip = Truncate(_tooltip, 127),
        szInfo = "",
        szInfoTitle = ""
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            switch (lParam.ToInt32() & 0xFFFF)
            {
                case NativeMethods.WM_LBUTTONDBLCLK:
                    DoubleClick?.Invoke();
                    break;
                case NativeMethods.WM_RBUTTONUP:
                    ShowMenu();
                    break;
            }
            handled = true;
        }
        else if (msg == _taskbarCreatedMessage)
        {
            // Проводник перезапустился — значок нужно добавить заново.
            Add();
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;

        // Без этого меню не закроется по клику вне его.
        if (PresentationSource.FromVisual(_menu) is HwndSource source)
            NativeMethods.SetForegroundWindow(source.Handle);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose()
    {
        _window.RemoveHook(WndProc);
        if (_added)
        {
            var data = CreateData(0);
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
            _added = false;
        }
        if (_hIcon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }
}
