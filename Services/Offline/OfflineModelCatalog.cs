using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ScreenTranslator.Services.Offline;

/// <summary>
/// Офлайн-модель перевода: одно направление Opus-MT (en→ru) или многоязычная NLLB-200 (Source = Target = "*").
/// </summary>
public sealed record OfflineModelInfo(string Source, string Target, string Repository, int SizeMb, string? Title = null)
{
    public const string AnyLanguage = "*";

    /// <summary>Идентификатор: "en-ru" для Opus-MT, "nllb-200" для NLLB.</summary>
    public string Id => IsMultilingual ? "nllb-200" : $"{Source}-{Target}";

    public bool IsMultilingual => Source == AnyLanguage;

    public string DisplayName => Title ?? $"{LanguageCatalog.NameOf(Source)} → {LanguageCatalog.NameOf(Target)}";

    public string Directory => Path.Combine(OfflineModelCatalog.ModelsFolder, Id);

    public bool IsInstalled => File.Exists(Path.Combine(Directory, OfflineModelCatalog.CompleteMarker));
}

/// <summary>Один шаг перевода: модель и (для NLLB) коды языков.</summary>
public sealed record TranslationStep(OfflineModelInfo Model, string? SourceToken = null, string? TargetToken = null);

/// <summary>
/// Каталог офлайн-моделей, сконвертированных в ONNX (https://huggingface.co/Xenova):
///  • Opus-MT (Helsinki-NLP) — быстрые модели на одно направление, ~110 МБ;
///  • NLLB-200 (Meta, 600 млн параметров) — одна модель для всех языков, качественнее, но медленнее, ~870 МБ.
/// Модели скачиваются в %LocalAppData%\ScreenTranslator\models.
/// </summary>
public static class OfflineModelCatalog
{
    public const string CompleteMarker = ".complete";
    public const string Pivot = "en";

    public static string ModelsFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), SettingsService.AppFolderName, "models");

    /// <summary>Файлы модели: путь в репозитории → имя файла на диске.</summary>
    public static readonly (string Remote, string Local)[] Files =
    {
        ("config.json", Seq2SeqTranslator.ConfigFile),
        ("tokenizer.json", Seq2SeqTranslator.TokenizerFile),
        ("onnx/encoder_model_quantized.onnx", Seq2SeqTranslator.EncoderFile),
        ("onnx/decoder_model_merged_quantized.onnx", Seq2SeqTranslator.DecoderFile)
    };

    /// <summary>Многоязычная модель NLLB-200.</summary>
    public static readonly OfflineModelInfo Nllb = new(OfflineModelInfo.AnyLanguage, OfflineModelInfo.AnyLanguage,
        "Xenova/nllb-200-distilled-600M", 870, "NLLB-200 — все языки, качественнее (медленнее)");

    private static OfflineModelInfo M(string source, string target, int sizeMb = 112) =>
        new(source, target, $"Xenova/opus-mt-{source}-{target}", sizeMb);

    /// <summary>Направления Opus-MT. Для остальных пар перевод идёт через английский.</summary>
    private static readonly OfflineModelInfo[] Opus =
    {
        M("en", "ru"), M("ru", "en"),
        M("en", "uk"), M("uk", "en"), M("uk", "ru"), M("ru", "uk"),
        M("en", "de"), M("de", "en"),
        M("en", "fr"), M("fr", "en"), M("fr", "ru"), M("ru", "fr"),
        M("en", "es"), M("es", "en"), M("es", "ru"), M("ru", "es"),
        M("en", "it"), M("it", "en"),
        M("en", "zh"), M("zh", "en"),
        M("ja", "en"), M("ko", "en"),
        M("pl", "en"), M("tr", "en")
    };

    /// <summary>Все модели: NLLB первой, затем Opus-MT.</summary>
    public static readonly IReadOnlyList<OfflineModelInfo> All = new[] { Nllb }.Concat(Opus).ToArray();

    /// <summary>Коды языков NLLB-200 (FLORES-200) для языков приложения.</summary>
    private static readonly Dictionary<string, string> NllbCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ru"] = "rus_Cyrl", ["en"] = "eng_Latn", ["uk"] = "ukr_Cyrl", ["be"] = "bel_Cyrl", ["kk"] = "kaz_Cyrl",
        ["de"] = "deu_Latn", ["fr"] = "fra_Latn", ["es"] = "spa_Latn", ["it"] = "ita_Latn", ["pt"] = "por_Latn",
        ["pl"] = "pol_Latn", ["nl"] = "nld_Latn", ["tr"] = "tur_Latn", ["cs"] = "ces_Latn", ["sk"] = "slk_Latn",
        ["sl"] = "slv_Latn", ["bg"] = "bul_Cyrl", ["ro"] = "ron_Latn", ["hu"] = "hun_Latn", ["el"] = "ell_Grek",
        ["sv"] = "swe_Latn", ["da"] = "dan_Latn", ["nb"] = "nob_Latn", ["fi"] = "fin_Latn", ["et"] = "est_Latn",
        ["lv"] = "lvs_Latn", ["lt"] = "lit_Latn", ["ja"] = "jpn_Jpan", ["ko"] = "kor_Hang", ["zh"] = "zho_Hans",
        ["zh-TW"] = "zho_Hant", ["ar"] = "arb_Arab", ["he"] = "heb_Hebr", ["hi"] = "hin_Deva", ["th"] = "tha_Thai",
        ["vi"] = "vie_Latn", ["id"] = "ind_Latn"
    };

    public static string? NllbCode(string language) => NllbCodes.TryGetValue(language, out var code) ? code : null;

    public static OfflineModelInfo? Find(string source, string target) =>
        Opus.FirstOrDefault(m => m.Source == Normalize(source) && m.Target == Normalize(target));

    public static OfflineModelInfo? FindById(string id) =>
        All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Традиционный китайский Opus-MT переводит той же моделью, что и упрощённый.</summary>
    private static string Normalize(string code) => code == "zh-TW" ? "zh" : code;

    /// <summary>Цепочка моделей Opus-MT: напрямую или через английский (de→en→ru).</summary>
    /// <param name="installedOnly">true — только установленные модели.</param>
    public static List<OfflineModelInfo>? FindRoute(string source, string target, bool installedOnly)
    {
        bool Usable(OfflineModelInfo? m) => m != null && (!installedOnly || m.IsInstalled);

        var direct = Find(source, target);
        if (Usable(direct)) return new List<OfflineModelInfo> { direct! };

        if (source != Pivot && target != Pivot)
        {
            var first = Find(source, Pivot);
            var second = Find(Pivot, target);
            if (Usable(first) && Usable(second)) return new List<OfflineModelInfo> { first!, second! };
        }
        return null;
    }

    /// <summary>
    /// План перевода из установленных моделей. Если выбрано «качество» и NLLB установлена — переводит она;
    /// иначе Opus-MT, а NLLB — запасной вариант для языков, для которых нет моделей Opus-MT.
    /// </summary>
    public static List<TranslationStep>? Plan(string source, string target, bool preferQuality)
    {
        var nllbSource = NllbCode(source);
        var nllbTarget = NllbCode(target);
        bool nllbReady = Nllb.IsInstalled && nllbSource != null && nllbTarget != null;
        var nllbPlan = nllbReady ? new List<TranslationStep> { new(Nllb, nllbSource, nllbTarget) } : null;

        if (preferQuality && nllbPlan != null) return nllbPlan;
        var route = FindRoute(source, target, installedOnly: true);
        if (route != null) return route.Select(m => new TranslationStep(m)).ToList();
        return nllbPlan;
    }

    public static IEnumerable<OfflineModelInfo> Installed => All.Where(m => m.IsInstalled);
}
