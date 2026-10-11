using System;
using System.Collections.Generic;
using System.Windows.Input;
using ScreenTranslator.Models;
using ScreenTranslator.Native;

namespace ScreenTranslator.Services;

/// <summary>Глобальные горячие клавиши через RegisterHotKey.</summary>
internal sealed class HotkeyService : IDisposable
{
    private readonly MessageWindow _window;
    private readonly HashSet<int> _registered = new();

    public event Action<int>? Pressed;

    public HotkeyService(MessageWindow window)
    {
        _window = window;
        _window.AddHook(WndProc);
    }

    /// <returns>false, если сочетание уже занято другой программой.</returns>
    public bool Register(int id, HotkeySetting hotkey)
    {
        Unregister(id);
        if (!hotkey.IsValid) return false;

        uint modifiers = NativeMethods.MOD_NOREPEAT;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= NativeMethods.MOD_ALT;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= NativeMethods.MOD_CONTROL;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= NativeMethods.MOD_SHIFT;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= NativeMethods.MOD_WIN;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key);
        if (!NativeMethods.RegisterHotKey(_window.Handle, id, modifiers, vk)) return false;

        _registered.Add(id);
        return true;
    }

    public void Unregister(int id)
    {
        if (_registered.Remove(id))
            NativeMethods.UnregisterHotKey(_window.Handle, id);
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered)
            NativeMethods.UnregisterHotKey(_window.Handle, id);
        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            Pressed?.Invoke(wParam.ToInt32());
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.RemoveHook(WndProc);
    }
}
