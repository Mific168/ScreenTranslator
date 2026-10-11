using System;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using ScreenTranslator.Native;

namespace ScreenTranslator.Services;

/// <summary>Светлая/тёмная тема по системной настройке Windows.</summary>
public static class ThemeService
{
    // Сборка указана явно, чтобы тема находилась независимо от того, какая сборка запущена.
    private const string LightUri = "pack://application:,,,/ScreenTranslator;component/Themes/Light.xaml";
    private const string DarkUri = "pack://application:,,,/ScreenTranslator;component/Themes/Dark.xaml";

    public static bool IsDark { get; private set; }

    public static void Initialize() => Apply(ReadSystemIsDark());

    /// <summary>Вызывается при WM_SETTINGCHANGE "ImmersiveColorSet".</summary>
    public static void Refresh()
    {
        bool dark = ReadSystemIsDark();
        if (dark != IsDark) Apply(dark);
    }

    private static bool ReadSystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void Apply(bool dark)
    {
        IsDark = dark;
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var themeDictionary = new ResourceDictionary { Source = new Uri(dark ? DarkUri : LightUri) };

        // Словарь цветов всегда первый (см. App.xaml), стили элементов — после него.
        if (dictionaries.Count > 0) dictionaries[0] = themeDictionary;
        else dictionaries.Add(themeDictionary);

        foreach (Window window in Application.Current.Windows)
            ApplyWindowFrame(window);
    }

    /// <summary>Тёмный заголовок окна и скруглённые углы Windows 11 (на Windows 10 игнорируется).</summary>
    public static void ApplyWindowFrame(Window window, bool rounded = false)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int dark = IsDark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        if (rounded)
        {
            int corner = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
    }
}
