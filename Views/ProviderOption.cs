using System.Collections.Generic;
using System.Linq;
using ScreenTranslator.Models;
using ScreenTranslator.Services;
using ScreenTranslator.Services.Offline;

namespace ScreenTranslator.Views;

/// <summary>
/// Вариант в списке «Сервис перевода» окна результата. Офлайн-переводчик представлен двумя
/// вариантами: быстрые модели Opus-MT и качественная NLLB-200.
/// </summary>
public sealed record ProviderOption(string Id, string Name, TranslationProviderKind Kind, bool PreferQuality = false)
{
    public const string OfflineFastId = "offline-fast";
    public const string OfflineQualityId = "offline-nllb";

    public override string ToString() => Name;

    /// <summary>Применяет выбор к настройкам (сохранение — снаружи).</summary>
    public void Apply(AppSettings settings)
    {
        settings.Provider = Kind;
        if (Kind == TranslationProviderKind.Offline) settings.OfflinePreferQuality = PreferQuality;
    }

    /// <summary>Идентификатор варианта, соответствующего текущим настройкам.</summary>
    public static string CurrentId()
    {
        var s = SettingsService.Current;
        return s.Provider switch
        {
            TranslationProviderKind.Offline => s.OfflinePreferQuality && OfflineModelCatalog.Nllb.IsInstalled
                ? OfflineQualityId
                : OfflineFastId,
            _ => s.Provider.ToString()
        };
    }

    /// <summary>Список сервисов окна перевода; у ненастроенных — пометка, почему они пока не будут работать.</summary>
    public static List<ProviderOption> Build()
    {
        var s = SettingsService.Current;
        bool opusInstalled = OfflineModelCatalog.Installed.Any(m => !m.IsMultilingual);

        string Note(bool configured, string reason) => configured ? "" : $" ({reason})";

        var options = new List<ProviderOption>
        {
            new(OfflineFastId, "Офлайн: быстрый" + Note(opusInstalled, "нет моделей"), TranslationProviderKind.Offline),
            new(OfflineQualityId, "Офлайн: NLLB-200" + Note(OfflineModelCatalog.Nllb.IsInstalled, "не установлена"),
                TranslationProviderKind.Offline, PreferQuality: true),
            new(nameof(TranslationProviderKind.Online), "Перевод онлайн", TranslationProviderKind.Online)
        };

        // LibreTranslate нужен только тем, у кого есть свой сервер: он выбирается в настройках
        // и появляется в окне перевода, только пока выбран.
        if (s.Provider == TranslationProviderKind.LibreTranslate)
            options.Add(new(nameof(TranslationProviderKind.LibreTranslate),
                "LibreTranslate" + Note(!string.IsNullOrWhiteSpace(s.LibreUrl), "нет адреса"),
                TranslationProviderKind.LibreTranslate));

        return options;
    }
}
