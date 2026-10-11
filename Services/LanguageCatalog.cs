using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ScreenTranslator.Services;

/// <summary>
/// Описание языка и его кодов у разных провайдеров.
/// Code — внутренний код приложения (ISO 639-1, для традиционного китайского — "zh-TW").
/// </summary>
public sealed record LanguageInfo(
    string Code,
    string Name,
    string[] LibreCodes,      // варианты кодов LibreTranslate в порядке предпочтения
    string GoogleCode,        // код в Google Переводчике
    string YandexCode);       // код в Яндекс Переводчике

/// <summary>Элемент выпадающего списка языков.</summary>
public sealed record LanguageItem(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Справочник языков и слой сопоставления кодов между провайдерами.</summary>
public static class LanguageCatalog
{
    public const string Auto = "auto";

    public static readonly IReadOnlyList<LanguageInfo> All = new LanguageInfo[]
    {
        new("ru", "Русский", new[] { "ru" }, "ru", "ru"),
        new("en", "Английский", new[] { "en" }, "en", "en"),
        new("uk", "Украинский", new[] { "uk" }, "uk", "uk"),
        new("be", "Белорусский", new[] { "be" }, "be", "be"),
        new("kk", "Казахский", new[] { "kk" }, "kk", "kk"),
        new("de", "Немецкий", new[] { "de" }, "de", "de"),
        new("fr", "Французский", new[] { "fr" }, "fr", "fr"),
        new("es", "Испанский", new[] { "es" }, "es", "es"),
        new("it", "Итальянский", new[] { "it" }, "it", "it"),
        new("pt", "Португальский", new[] { "pt", "pt-BR" }, "pt", "pt"),
        new("pl", "Польский", new[] { "pl" }, "pl", "pl"),
        new("nl", "Нидерландский", new[] { "nl" }, "nl", "nl"),
        new("tr", "Турецкий", new[] { "tr" }, "tr", "tr"),
        new("cs", "Чешский", new[] { "cs" }, "cs", "cs"),
        new("sk", "Словацкий", new[] { "sk" }, "sk", "sk"),
        new("sl", "Словенский", new[] { "sl" }, "sl", "sl"),
        new("bg", "Болгарский", new[] { "bg" }, "bg", "bg"),
        new("ro", "Румынский", new[] { "ro" }, "ro", "ro"),
        new("hu", "Венгерский", new[] { "hu" }, "hu", "hu"),
        new("el", "Греческий", new[] { "el" }, "el", "el"),
        new("sv", "Шведский", new[] { "sv" }, "sv", "sv"),
        new("da", "Датский", new[] { "da" }, "da", "da"),
        new("nb", "Норвежский", new[] { "nb", "no" }, "no", "no"),
        new("fi", "Финский", new[] { "fi" }, "fi", "fi"),
        new("et", "Эстонский", new[] { "et" }, "et", "et"),
        new("lv", "Латышский", new[] { "lv" }, "lv", "lv"),
        new("lt", "Литовский", new[] { "lt" }, "lt", "lt"),
        new("ja", "Японский", new[] { "ja" }, "ja", "ja"),
        new("ko", "Корейский", new[] { "ko" }, "ko", "ko"),
        new("zh", "Китайский (упрощённый)", new[] { "zh-Hans", "zh" }, "zh-CN", "zh"),
        new("zh-TW", "Китайский (традиционный)", new[] { "zh-Hant", "zt" }, "zh-TW", "zh"),
        new("ar", "Арабский", new[] { "ar" }, "ar", "ar"),
        new("he", "Иврит", new[] { "he" }, "iw", "he"),
        new("hi", "Хинди", new[] { "hi" }, "hi", "hi"),
        new("th", "Тайский", new[] { "th" }, "th", "th"),
        new("vi", "Вьетнамский", new[] { "vi" }, "vi", "vi"),
        new("id", "Индонезийский", new[] { "id" }, "id", "id"),
    };

    private static readonly Dictionary<string, LanguageInfo> ByCode =
        All.ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);

    public static LanguageInfo? Get(string? code) =>
        code != null && ByCode.TryGetValue(code, out var info) ? info : null;

    public static string NameOf(string? code)
    {
        if (string.IsNullOrEmpty(code) || code == Auto) return "Автоопределение";
        return Get(code)?.Name ?? code;
    }

    /// <summary>
    /// Приводит любой код (BCP-47 тег OCR, код LibreTranslate, Google) к внутреннему коду
    /// приложения. Возвращает null, если язык не поддерживается.
    /// </summary>
    public static string? Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().Replace('_', '-').ToLowerInvariant();

        if (t.StartsWith("zh-hant") || t is "zh-tw" or "zh-hk" or "zh-mo" or "zt") return "zh-TW";
        if (t.StartsWith("zh")) return "zh";
        if (t is "no" or "nn" || t.StartsWith("nb") || t.StartsWith("no-") || t.StartsWith("nn-")) return "nb";
        if (t == "iw") return "he";

        if (ByCode.TryGetValue(t, out var exact)) return exact.Code;

        var primary = t.Split('-')[0];
        return ByCode.TryGetValue(primary, out var info) ? info.Code : null;
    }

    /// <summary>Список языков для выпадающих списков, отсортированный по названию.</summary>
    public static IReadOnlyList<LanguageItem> Items(bool includeAuto)
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var items = All
            .OrderBy(l => l.Name, StringComparer.Create(culture, ignoreCase: true))
            .Select(l => new LanguageItem(l.Code, l.Name))
            .ToList();
        if (includeAuto) items.Insert(0, new LanguageItem(Auto, "Автоопределение"));
        return items;
    }

    // ---------- Сопоставление с кодами провайдеров ----------

    public static string ToGoogle(string? code) =>
        string.IsNullOrEmpty(code) || code == Auto ? "auto" : Get(code)?.GoogleCode ?? code;

    public static string? ToYandex(string? code) =>
        string.IsNullOrEmpty(code) || code == Auto ? null : Get(code)?.YandexCode ?? code;

    /// <summary>
    /// Подбирает код LibreTranslate. Если известен список языков сервера, выбирается
    /// первый поддерживаемый вариант (например, "zh-Hans" на новых серверах и "zh" на старых).
    /// </summary>
    public static string? ToLibre(string code, ICollection<string>? serverCodes)
    {
        var info = Get(code);
        if (info == null) return null;
        if (serverCodes == null || serverCodes.Count == 0) return info.LibreCodes[0];
        foreach (var candidate in info.LibreCodes)
        {
            var match = serverCodes.FirstOrDefault(c => string.Equals(c, candidate, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return null;
    }
}
