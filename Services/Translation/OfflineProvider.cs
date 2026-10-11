using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ScreenTranslator.Services.Offline;

namespace ScreenTranslator.Services.Translation;

/// <summary>
/// Встроенный офлайн-переводчик: нейросетевые модели (Opus-MT или NLLB-200) работают прямо в приложении,
/// без интернета, сервера и Docker.
/// </summary>
public sealed class OfflineProvider : ITranslationProvider
{
    private readonly OfflineModelManager _manager;

    public OfflineProvider(OfflineModelManager manager)
    {
        _manager = manager;
    }

    public string Name => "Офлайн-переводчик";

    public async Task<TranslationResult> TranslateAsync(string text, string? sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        // Модели работают только для известного направления — язык исходного текста нужен заранее.
        var source = sourceLanguage ?? LanguageDetector.Detect(text).Code;
        if (source == null)
            return TranslationResult.Fail(Name, TranslationError.UnsupportedLanguage,
                "Не удалось определить язык текста. Выберите язык исходного текста вручную.");

        if (source == targetLanguage || (source.StartsWith("zh") && targetLanguage.StartsWith("zh") && !OfflineModelCatalog.Nllb.IsInstalled))
            return TranslationResult.Ok(Name, text, source);

        var plan = OfflineModelCatalog.Plan(source, targetLanguage, SettingsService.Current.OfflinePreferQuality);
        if (plan == null)
            return TranslationResult.Fail(Name, TranslationError.NotConfigured, MissingModelMessage(source, targetLanguage));

        var translated = await TranslateMixedAsync(text, plan, targetLanguage, ct);
        // Модели Opus-MT обучены на текстах с HTML-сущностями и иногда выдают «&quot;» вместо кавычек.
        return TranslationResult.Ok(Name, WebUtility.HtmlDecode(translated), source);
    }

    /// <summary>
    /// Текст вперемешку на двух языках («Know My Name (официальное музыкальное видео) | Гимн ЧМ-2026»):
    /// модель одного направления на «чужих» словах выдаёт мусор. Куски, уже написанные системой письма
    /// языка перевода, оставляем как есть, остальное переводим (всё одним запросом — по строке на кусок).
    /// </summary>
    private async Task<string> TranslateMixedAsync(string text, IReadOnlyList<TranslationStep> plan, string target,
        CancellationToken ct)
    {
        string targetScript = LanguageDetector.ScriptOfLanguage(target);
        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Select(line => SplitByScript(line, targetScript))
            .ToList();
        // Нет ни одного куска на языке перевода — переводим как обычно, целыми предложениями.
        if (!lines.Any(segments => segments.Any(s => !s.Translate && s.Text.Any(char.IsLetter))))
            return await _manager.TranslateAsync(text, plan, ct);

        var pieces = lines.SelectMany(segments => segments).Where(s => s.Translate).Select(s => s.Text).ToList();
        var translated = pieces.Count == 0
            ? Array.Empty<string>()
            : (await _manager.TranslateAsync(string.Join("\n", pieces), plan, ct)).Replace("\r\n", "\n").Split('\n');
        if (translated.Length != pieces.Count) return await _manager.TranslateAsync(text, plan, ct);

        int next = 0;
        return string.Join(Environment.NewLine, lines.Select(segments =>
            string.Join(" ", segments.Select(s => s.Translate ? translated[next++].Trim() : s.Text).Where(s => s.Length > 0))));
    }

    /// <summary>
    /// Делит строку на куски по системе письма: слова языка перевода (Translate = false) и остальные.
    /// Слова без букв («|», «4:17», «2026») присоединяются к соседнему куску.
    /// </summary>
    private static List<(string Text, bool Translate)> SplitByScript(string line, string targetScript)
    {
        var segments = new List<(string Text, bool Translate)>();
        var neutral = new List<string>();
        foreach (var word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var script = LanguageDetector.DominantScript(word);
            if (script == null)
            {
                if (segments.Count == 0) neutral.Add(word);
                else segments[^1] = (segments[^1].Text + " " + word, segments[^1].Translate);
                continue;
            }

            bool translate = script != targetScript;
            if (segments.Count > 0 && segments[^1].Translate == translate)
                segments[^1] = (segments[^1].Text + " " + word, translate);
            else
                segments.Add((string.Join(" ", neutral.Append(word)), translate));
            neutral.Clear();
        }
        if (neutral.Count > 0) segments.Add((string.Join(" ", neutral), false));
        return segments;
    }

    private static string MissingModelMessage(string source, string target)
    {
        var direction = $"«{LanguageCatalog.NameOf(source)} → {LanguageCatalog.NameOf(target)}»";
        bool nllbSupports = OfflineModelCatalog.NllbCode(source) != null && OfflineModelCatalog.NllbCode(target) != null;
        var needed = OfflineModelCatalog.FindRoute(source, target, installedOnly: false);

        if (needed == null)
            return nllbSupports
                ? $"Для направления {direction} нужна модель NLLB-200 (~870 МБ) — установите её в настройках."
                : $"Для направления {direction} нет офлайн-модели. Откройте перевод в браузере или подключите онлайн-сервис в настройках.";

        var missing = needed.Where(m => !m.IsInstalled).Select(m => m.DisplayName);
        return $"Офлайн-модель для перевода {direction} не установлена. Установите в настройках: " +
               $"{string.Join(", ", missing)} (по ~110 МБ)" +
               (nllbSupports ? " или NLLB-200 для всех языков." : ".");
    }
}
