using System;
using System.IO;

namespace ScreenTranslator.Services;

/// <summary>Минимальный журнал ошибок: %AppData%\ScreenTranslator\error.log.</summary>
public static class Log
{
    private static readonly object Sync = new();
    private const long MaxSize = 512 * 1024;

    public static void Error(string message, Exception? ex = null)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(SettingsService.AppDataFolder);
                var path = Path.Combine(SettingsService.AppDataFolder, "error.log");
                if (File.Exists(path) && new FileInfo(path).Length > MaxSize) File.Delete(path);
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}{ex}{Environment.NewLine}");
            }
        }
        catch
        {
            // Журнал не должен порождать новые ошибки.
        }
    }
}
