using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace ScreenTranslator.Models;

/// <summary>Сочетание клавиш: модификаторы + основная клавиша.</summary>
public sealed class HotkeySetting
{
    public HotkeySetting()
    {
    }

    public HotkeySetting(ModifierKeys modifiers, Key key)
    {
        Modifiers = modifiers;
        Key = key;
    }

    public ModifierKeys Modifiers { get; set; }
    public Key Key { get; set; }

    [JsonIgnore]
    public bool IsValid => Key != Key.None && Modifiers != ModifierKeys.None;

    public bool SameAs(HotkeySetting? other) =>
        other != null && other.Modifiers == Modifiers && other.Key == Key;

    public HotkeySetting Clone() => new(Modifiers, Key);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (Key != Key.None) parts.Add(KeyToText(Key));
        return string.Join("+", parts);
    }

    public static string KeyToText(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (key - Key.NumPad0),
        Key.OemTilde => "`",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemPipe => "\\",
        Key.Space => "Пробел",
        Key.Return => "Enter",
        Key.Snapshot => "PrintScreen",
        _ => key.ToString()
    };
}
