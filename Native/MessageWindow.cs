using System;
using System.Windows.Interop;

namespace ScreenTranslator.Native;

/// <summary>
/// Скрытое окно для приёма системных сообщений: горячие клавиши (WM_HOTKEY),
/// события значка в трее и изменение темы Windows (WM_SETTINGCHANGE).
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    private readonly HwndSource _source;

    public MessageWindow()
    {
        var parameters = new HwndSourceParameters("ScreenTranslator.MessageWindow")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,               // невидимое окно (без WS_VISIBLE)
            ExtendedWindowStyle = 0x00000080 // WS_EX_TOOLWINDOW — не показывается в Alt+Tab
        };
        _source = new HwndSource(parameters);
    }

    public IntPtr Handle => _source.Handle;

    public void AddHook(HwndSourceHook hook) => _source.AddHook(hook);

    public void RemoveHook(HwndSourceHook hook) => _source.RemoveHook(hook);

    public void Dispose() => _source.Dispose();
}
