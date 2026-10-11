using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using ScreenTranslator.Models;

namespace ScreenTranslator.Services;

/// <summary>Поиск установленных браузеров и открытие ссылок.</summary>
public static class BrowserService
{
    public static List<BrowserInfo> DetectBrowsers()
    {
        var result = new List<BrowserInfo>();
        var seenExe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var sources = new (RegistryHive Hive, RegistryView View)[]
        {
            (RegistryHive.CurrentUser, RegistryView.Default),
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32) // = SOFTWARE\WOW6432Node
        };

        foreach (var (hive, view) in sources)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var clients = baseKey.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet");
                if (clients == null) continue;

                foreach (var id in clients.GetSubKeyNames())
                {
                    using var browserKey = clients.OpenSubKey(id);
                    if (browserKey == null) continue;

                    var name = browserKey.GetValue(null) as string;
                    using var commandKey = browserKey.OpenSubKey(@"shell\open\command");
                    var exe = ParseExecutable(commandKey?.GetValue(null) as string);
                    if (exe == null || !File.Exists(exe) || !seenExe.Add(exe)) continue;

                    result.Add(new BrowserInfo(id, string.IsNullOrWhiteSpace(name) ? id : name.Trim(), exe));
                }
            }
            catch (Exception ex)
            {
                Log.Error("Ошибка поиска браузеров", ex);
            }
        }

        return result.OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Выделяет путь к exe из командной строки вида "C:\...\chrome.exe" --args.</summary>
    public static string? ParseExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = Environment.ExpandEnvironmentVariables(command.Trim());

        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }

        int exeIndex = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIndex > 0 ? command[..(exeIndex + 4)] : command;
    }

    /// <summary>
    /// Открывает перевод в браузере с учётом ограничений длины ссылки и наличия браузера.
    /// Все проблемы показываются пользователю диалогами.
    /// </summary>
    /// <param name="kind">Веб-переводчик: Google или Яндекс.</param>
    /// <returns>true, если браузер был запущен.</returns>
    public static bool OpenTranslation(Window? owner, WebTranslatorKind kind, string text, string? source, string target)
    {
        text = text.Trim();

        if (string.IsNullOrEmpty(text))
        {
            Dialogs.Info(owner, "Нет текста для перевода.");
            return false;
        }

        var url = WebTranslatorUrls.Build(kind, text, source, target);
        int maxText = WebTranslatorUrls.MaxTextLength(kind);
        if (text.Length > maxText || url.Length > WebTranslatorUrls.MaxUrlLength)
        {
            var question =
                $"Текст слишком длинный для передачи через ссылку ({text.Length} символов, " +
                $"{WebTranslatorUrls.ServiceName(kind)} принимает до {maxText}).\n\n" +
                "Скопировать текст в буфер обмена и открыть переводчик? Затем вставьте текст вручную (Ctrl+V).";
            if (!Dialogs.Confirm(owner, question)) return false;

            if (!ClipboardHelper.TrySetText(text))
            {
                Dialogs.Error(owner, "Не удалось скопировать текст в буфер обмена.");
                return false;
            }
            url = WebTranslatorUrls.BuildEmpty(kind, source, target);
        }

        return OpenUrl(owner, url);
    }

    public static bool OpenUrl(Window? owner, string url)
    {
        var settings = SettingsService.Current;

        if (settings.BrowserId != BrowserInfo.DefaultId)
        {
            if (string.IsNullOrEmpty(settings.BrowserPath) || !File.Exists(settings.BrowserPath))
            {
                var fallback = Dialogs.Confirm(owner,
                    $"Браузер «{settings.BrowserName}» не найден — возможно, он был удалён.\n\n" +
                    "Открыть перевод в браузере по умолчанию? Выбрать другой браузер можно в настройках.");
                return fallback && OpenInDefaultBrowser(owner, url);
            }

            try
            {
                var psi = new ProcessStartInfo(settings.BrowserPath) { UseShellExecute = false };
                psi.ArgumentList.Add(url);
                Process.Start(psi)?.Dispose();
                return true;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                Log.Error("Не удалось запустить браузер", ex);
                var fallback = Dialogs.Confirm(owner,
                    $"Не удалось запустить «{settings.BrowserName}»: {ex.Message}\n\nОткрыть в браузере по умолчанию?");
                return fallback && OpenInDefaultBrowser(owner, url);
            }
        }

        return OpenInDefaultBrowser(owner, url);
    }

    private static bool OpenInDefaultBrowser(Window? owner, string url)
    {
        // Запускаем браузер по умолчанию напрямую: ShellExecute может обрезать очень длинные ссылки.
        try
        {
            var command = GetDefaultBrowserCommand();
            var exe = ParseExecutable(command);
            if (command != null && exe != null && File.Exists(exe))
            {
                int exeEnd = command.StartsWith('"') ? command.IndexOf('"', 1) + 1 : command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) + 4;
                var args = command[exeEnd..].Trim();
                args = args.Contains("%1") ? args.Replace("%1", url) : (args + " \"" + url + "\"").Trim();
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false })?.Dispose();
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось запустить браузер по умолчанию напрямую", ex);
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось открыть ссылку", ex);
            Dialogs.Error(owner, "Не удалось открыть браузер: " + ex.Message +
                                 "\n\nПроверьте, что в Windows назначен браузер по умолчанию, или выберите браузер в настройках.");
            return false;
        }
    }

    private static string? GetDefaultBrowserCommand()
    {
        foreach (var scheme in new[] { "https", "http" })
        {
            using var choice = Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{scheme}\UserChoice");
            if (choice?.GetValue("ProgId") is not string progId || progId.Length == 0) continue;

            using var command = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            if (command?.GetValue(null) is string value && value.Length > 0) return value;
        }
        return null;
    }
}
