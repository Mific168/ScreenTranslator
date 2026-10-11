using System;
using System.Windows.Controls;
using System.Windows.Input;
using ScreenTranslator.Models;

namespace ScreenTranslator.Controls;

/// <summary>Поле, которое запоминает нажатое сочетание клавиш.</summary>
public sealed class HotkeyBox : TextBox
{
    private HotkeySetting? _hotkey;

    public event EventHandler? HotkeyChanged;

    public HotkeyBox()
    {
        SetResourceReference(StyleProperty, typeof(TextBox));
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        Cursor = Cursors.Hand;
        Tag = "Нажмите сочетание клавиш";
        ToolTip = "Щёлкните и нажмите новое сочетание, например Ctrl+Shift+T";
        InputMethod.SetIsInputMethodEnabled(this, false);
    }

    public HotkeySetting? Hotkey
    {
        get => _hotkey;
        set
        {
            _hotkey = value?.Clone();
            Text = _hotkey?.ToString() ?? "";
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        // Tab без модификаторов — обычная навигация по полям.
        if (key == Key.Tab && modifiers == ModifierKeys.None)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
            Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            // Пока нажаты только модификаторы — показываем промежуточный вид.
            Text = new HotkeySetting(modifiers, Key.None) + "+…";
            return;
        }

        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            Text = _hotkey?.ToString() ?? "";
            Keyboard.ClearFocus();
            return;
        }

        if (modifiers == ModifierKeys.None)
        {
            Text = "Добавьте Ctrl, Alt, Shift или Win";
            return;
        }

        Hotkey = new HotkeySetting(modifiers, key);
        HotkeyChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        // Отпустили модификаторы, не нажав основную клавишу, — возвращаем сохранённое значение.
        if (Keyboard.Modifiers == ModifierKeys.None && Text.EndsWith("+…"))
            Text = _hotkey?.ToString() ?? "";
        base.OnPreviewKeyUp(e);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Text = _hotkey?.ToString() ?? "";
    }
}
