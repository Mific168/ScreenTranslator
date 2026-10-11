using System;
using ScreenTranslator.Models;

namespace ScreenTranslator.Services;

/// <summary>Ссылки на Google Переводчик и Яндекс Переводчик с текстом в URL.</summary>
public static class WebTranslatorUrls
{
    /// <summary>Предел длины ссылки: командная строка Windows ограничена 32 767 символами.</summary>
    public const int MaxUrlLength = 30000;

    public static string ServiceName(WebTranslatorKind kind) =>
        kind == WebTranslatorKind.Yandex ? "Яндекс Переводчик" : "Google Переводчик";

    /// <summary>Максимальная длина текста, которую принимает веб-версия сервиса.</summary>
    public static int MaxTextLength(WebTranslatorKind kind) =>
        kind == WebTranslatorKind.Yandex ? 10000 : 5000;

    public static string Build(WebTranslatorKind kind, string text, string? source, string target)
    {
        var encoded = Uri.EscapeDataString(text);
        if (kind == WebTranslatorKind.Yandex)
        {
            var src = LanguageCatalog.ToYandex(source);
            var srcPart = src == null ? "" : $"source_lang={src}&";
            return $"https://translate.yandex.ru/?{srcPart}target_lang={LanguageCatalog.ToYandex(target)}&text={encoded}";
        }
        return $"https://translate.google.com/?sl={LanguageCatalog.ToGoogle(source)}&tl={LanguageCatalog.ToGoogle(target)}&text={encoded}&op=translate";
    }

    /// <summary>Ссылка без текста — когда текст слишком длинный и вставляется вручную.</summary>
    public static string BuildEmpty(WebTranslatorKind kind, string? source, string target) =>
        kind == WebTranslatorKind.Yandex
            ? $"https://translate.yandex.ru/?target_lang={LanguageCatalog.ToYandex(target)}"
            : $"https://translate.google.com/?sl={LanguageCatalog.ToGoogle(source)}&tl={LanguageCatalog.ToGoogle(target)}&op=translate";
}
