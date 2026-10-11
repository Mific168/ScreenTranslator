using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenTranslator.Models;

namespace ScreenTranslator.Services;

/// <summary>Загрузка и сохранение настроек, шифрование ключей API через DPAPI.</summary>
public static class SettingsService
{
    public const string AppFolderName = "ScreenTranslator";

    // Дополнительная «соль» DPAPI: ключ, скопированный в чужой профиль или программу, не расшифруется.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ScreenTranslator.ApiKey.v1");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string AppDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    private static string FilePath => Path.Combine(AppDataFolder, "settings.json");

    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath, Encoding.UTF8);
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            // Повреждённый файл не должен ронять приложение: сохраняем копию и начинаем с настроек по умолчанию.
            Log.Error("Не удалось прочитать настройки", ex);
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch { /* не критично */ }
            Current = new AppSettings();
        }

        Normalize(Current);
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataFolder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions), Encoding.UTF8);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось сохранить настройки", ex);
            Dialogs.Error(null, "Не удалось сохранить настройки:\n" + ex.Message);
        }
    }

    private static void Normalize(AppSettings s)
    {
        if (s.SelectHotkey is not { IsValid: true }) s.SelectHotkey = new AppSettings().SelectHotkey;
        if (s.RepeatHotkey is not { IsValid: true }) s.RepeatHotkey = new AppSettings().RepeatHotkey;
        if (LanguageCatalog.Get(s.NativeLanguage) == null) s.NativeLanguage = "ru";
        if (string.IsNullOrWhiteSpace(s.OcrLanguage)) s.OcrLanguage = "auto";
        if (string.IsNullOrWhiteSpace(s.BrowserId)) s.BrowserId = BrowserInfo.DefaultId;
        s.LibreUrl ??= "";
        // DeepL API, Yandex Cloud API и режим «только браузер» убраны — переключаемся на офлайн-перевод.
        if (s.Provider is TranslationProviderKind.DeepL or TranslationProviderKind.YandexCloud or TranslationProviderKind.BrowserOnly)
            s.Provider = TranslationProviderKind.Offline;
    }

    // ---------- Ключи API ----------

    public static string GetLibreKey() => Unprotect(Current.LibreKeyProtected);

    public static string GetOpenRouterKey() => Unprotect(Current.OpenRouterKeyProtected);

    /// <summary>Шифрует строку для текущего пользователя Windows (DPAPI).</summary>
    public static string? Protect(string? plain)
    {
        if (string.IsNullOrWhiteSpace(plain)) return null;
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain.Trim()), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public static string Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex)
        {
            // Например, файл настроек перенесён с другого компьютера — ключ придётся ввести заново.
            Log.Error("Не удалось расшифровать ключ API", ex);
            return "";
        }
    }
}
