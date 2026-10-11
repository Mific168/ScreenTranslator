using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
using WinLanguage = Windows.Globalization.Language;

namespace ScreenTranslator.Services;

public enum OcrStatus
{
    Ok,
    NoLanguagesInstalled,
    LanguageNotInstalled,
    NoText,
    Failed
}

/// <param name="LanguageTag">Тег языка OCR, которым распознан текст (например, "en-US").</param>
/// <param name="LanguageCode">Тот же язык во внутренних кодах приложения.</param>
public sealed record OcrOutcome(OcrStatus Status, string Text, string? LanguageTag, string? LanguageCode, string Message);

/// <summary>Распознавание текста встроенным движком Windows.Media.Ocr (офлайн).</summary>
public sealed class OcrService
{
    public const string InstallInstructions =
        "Как установить язык распознавания текста (OCR):\n\n" +
        "Windows 11: «Параметры» → «Время и язык» → «Язык и регион» → «Добавить язык».\n" +
        "Windows 10: «Параметры» → «Время и язык» → «Язык» → «Добавить язык».\n" +
        "Выберите нужный язык и установите его (компонент «Оптическое распознавание символов» " +
        "входит в базовый языковой пакет). Делать язык языком интерфейса не нужно.\n\n" +
        "Либо в PowerShell от имени администратора:\n" +
        "  Get-WindowsCapability -Online | Where-Object Name -like 'Language.OCR*'\n" +
        "  Add-WindowsCapability -Online -Name \"Language.OCR~~~en-US~0.0.1.0\"\n" +
        "(вместо en-US подставьте нужный язык, например de-DE, ja-JP, zh-CN).\n\n" +
        "После установки перезапустите «Экранный переводчик».";

    private const int MaxAutoLanguages = 5;

    /// <summary>Поля вокруг изображения: Windows OCR плохо видит буквы, касающиеся края.</summary>
    private const int Padding = 24;

    /// <summary>Высота строки (в пикселях), при которой Windows OCR распознаёт надёжнее всего.</summary>
    private const double TargetLineHeight = 34;

    /// <summary>Если строки ниже этой высоты — делаем второй проход с увеличением.</summary>
    private const double MinGoodLineHeight = 24;

    /// <summary>Ограничение размера увеличенного изображения (память и скорость).</summary>
    private const long MaxPixels = 20_000_000;

    private sealed record PreparedImage(byte[] Pixels, int Width, int Height, double Scale);

    private sealed record LineInfo(string Text, double Left, double Top, double Right, double Bottom)
    {
        public double Height => Bottom - Top;
    }

    /// <summary>Слово и его место на изображении — чтобы сопоставлять результаты разных движков.</summary>
    private sealed record WordBox(string Text, double X, double Y, double Width, double Height)
    {
        public double Right => X + Width;
        public double Bottom => Y + Height;
    }

    private sealed record Candidate(WinLanguage Language, string Text, double Score, int Letters, double MedianLineHeight,
        IReadOnlyList<IReadOnlyList<WordBox>> Words)
    {
        public string Tag => Language.LanguageTag;
    }

    public static IReadOnlyList<WinLanguage> GetInstalledLanguages()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages.ToList();
        }
        catch (Exception ex)
        {
            Log.Error("OCR недоступен", ex);
            return Array.Empty<WinLanguage>();
        }
    }

    public static string InstalledLanguagesText()
    {
        var langs = GetInstalledLanguages();
        return langs.Count == 0 ? "нет" : string.Join(", ", langs.Select(l => l.DisplayName));
    }

    /// <param name="languageSetting">"auto" — попробовать все установленные языки и выбрать лучший результат.</param>
    public async Task<OcrOutcome> RecognizeAsync(BitmapSource image, string languageSetting)
    {
        var installed = GetInstalledLanguages();
        if (installed.Count == 0)
            return new OcrOutcome(OcrStatus.NoLanguagesInstalled, "", null, null,
                "В Windows не установлен ни один язык распознавания текста (OCR).");

        List<WinLanguage> candidates;
        if (!string.Equals(languageSetting, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var lang = installed.FirstOrDefault(l => string.Equals(l.LanguageTag, languageSetting, StringComparison.OrdinalIgnoreCase));
            if (lang == null)
                return new OcrOutcome(OcrStatus.LanguageNotInstalled, "", languageSetting, null,
                    $"Пакет распознавания для языка «{languageSetting}», выбранного в настройках, не установлен в Windows.");
            candidates = new List<WinLanguage> { lang };
        }
        else
        {
            candidates = OrderByUserPreference(installed).Take(MaxAutoLanguages).ToList();
        }

        try
        {
            // Проход 1: мелкие изображения увеличиваем в 2 раза, все языки параллельно.
            double scale1 = InitialScale(image.PixelWidth, image.PixelHeight);
            var pass1 = Prepare(image, scale1);
            var best = await RecognizeBestAsync(candidates, pass1);

            if (best == null)
            {
                // Текст не найден — возможно, он слишком мелкий. Пробуем сильнее увеличить.
                double bigger = ClampScale(image.PixelWidth, image.PixelHeight, pass1.Scale * 2.5);
                if (bigger > pass1.Scale * 1.25)
                    best = await RecognizeBestAsync(candidates, Prepare(image, bigger));
            }
            else if (best.MedianLineHeight is > 0 and < MinGoodLineHeight)
            {
                // Проход 2: строки получились мелкими — увеличиваем до оптимальной высоты строки
                // и распознаём заново тем же языком.
                double scale2 = ClampScale(image.PixelWidth, image.PixelHeight,
                    pass1.Scale * TargetLineHeight / best.MedianLineHeight);
                if (scale2 > pass1.Scale * 1.25)
                {
                    var second = await RecognizeBestAsync(candidates, Prepare(image, scale2), best.Language);
                    if (second != null && second.Letters >= best.Letters * 0.85)
                        best = second;
                }
            }

            if (best == null)
                return new OcrOutcome(OcrStatus.NoText, "", null, null, "В выделенной области не найден текст.");

            return new OcrOutcome(OcrStatus.Ok, best.Text, best.Tag, LanguageCatalog.Normalize(best.Tag), "");
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка OCR", ex);
            return new OcrOutcome(OcrStatus.Failed, "", null, null, "Ошибка распознавания текста: " + ex.Message);
        }
    }

    /// <param name="language">Язык основного результата; null — выбрать по оценке.</param>
    private static async Task<Candidate?> RecognizeBestAsync(List<WinLanguage> languages, PreparedImage image,
        WinLanguage? language = null)
    {
        // Движки для разных языков работают параллельно: 2–3 языка укладываются в ~100–300 мс.
        var results = (await Task.WhenAll(languages.Select(l => RecognizeWithAsync(l, image))))
            .Where(r => r != null && r.Letters > 0)
            .Select(r => r!)
            .ToList();

        var best = language != null
            ? results.FirstOrDefault(r => r.Tag == language.LanguageTag)
            : results.OrderByDescending(r => r.Score).FirstOrDefault();
        if (best == null) return null;

        // Сверка слов с другими движками и словарём — в фоновом потоке (COM-объекты проверки орфографии).
        var others = results.Where(r => r != best).ToList();
        var merged = await Task.Run(() => WordMerger.Merge(best, others));
        if (ReferenceEquals(merged, best.Words)) return best;

        var text = BuildText(ToLines(merged));
        return best with { Text = text, Words = merged };
    }

    private static IEnumerable<WinLanguage> OrderByUserPreference(IReadOnlyList<WinLanguage> installed)
    {
        var preferred = new List<string>();
        try
        {
            preferred.AddRange(Windows.System.UserProfile.GlobalizationPreferences.Languages);
        }
        catch
        {
            // не критично
        }

        return installed
            .Select(l => (Lang: l, Rank: IndexOfPrimary(preferred, l.LanguageTag)))
            .OrderBy(x => x.Rank < 0 ? int.MaxValue : x.Rank)
            .Select(x => x.Lang)
            .GroupBy(l => l.LanguageTag, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());
    }

    private static int IndexOfPrimary(List<string> tags, string tag)
    {
        var primary = tag.Split('-')[0];
        return tags.FindIndex(t => string.Equals(t.Split('-')[0], primary, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<Candidate?> RecognizeWithAsync(WinLanguage language, PreparedImage image)
    {
        var engine = OcrEngine.TryCreateFromLanguage(language);
        if (engine == null) return null;

        var buffer = CryptographicBuffer.CreateFromByteArray(image.Pixels);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8,
            image.Width, image.Height, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);

        var words = ExtractWords(result);
        var lines = ToLines(words);
        var text = BuildText(lines);
        double median = lines.Count == 0 ? 0 : Median(lines.Select(l => l.Height));
        return new Candidate(language, text, Score(text, language.LanguageTag), text.Count(char.IsLetterOrDigit), median, words);
    }

    /// <summary>
    /// Оценка качества распознавания: количество букв, умноженное на «чистоту» письма
    /// и бонус, если детектор подтверждает язык движка.
    /// </summary>
    private static double Score(string text, string tag)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        double score = text.Count(char.IsLetterOrDigit);
        var detection = LanguageDetector.Detect(text);
        score *= 0.5 + 0.5 * detection.Purity;

        var engineCode = LanguageCatalog.Normalize(tag);
        if (detection.Code != null && engineCode != null && detection.Confidence >= 0.5 &&
            string.Equals(detection.Code.Split('-')[0], engineCode.Split('-')[0], StringComparison.OrdinalIgnoreCase))
            score *= 1.35;

        // Латиницу лучше распознаёт «латинский» движок, кириллицу — «кириллический» и т.д.
        // (например, русский движок путает l и I в английском тексте).
        if (LanguageDetector.DominantScript(text) == LanguageDetector.ScriptOfLanguage(engineCode))
            score *= 1.15;

        return score;
    }

    // =====================================================================
    //  Сборка текста
    // =====================================================================

    private static IReadOnlyList<IReadOnlyList<WordBox>> ExtractWords(OcrResult result)
    {
        var lines = new List<IReadOnlyList<WordBox>>();
        foreach (var line in result.Lines)
        {
            var words = line.Words
                .Where(w => !string.IsNullOrEmpty(w.Text))
                .Select(w => new WordBox(w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height))
                .ToList();
            // Строка из одного значка «i» / «|» — пиктограмма («информация»), а не текст.
            if (words.Count == 1 && words[0].Text is "i" or "l" or "|" or "I") continue;
            if (words.Count > 0) lines.Add(words);
        }
        return JoinSplitLines(lines);
    }

    /// <summary>
    /// Windows OCR иногда режет одну строку на части и ставит хвост в конец текста
    /// («Набор музыки | Starjunk 95» … «— Industrial»). Части на одной высоте, стоящие рядом,
    /// склеиваются обратно по порядку слева направо. Колонки (большой промежуток) не трогаем.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<WordBox>> JoinSplitLines(List<IReadOnlyList<WordBox>> lines)
    {
        if (lines.Count < 2) return lines;
        var rows = lines.Select(l => l.ToList()).ToList();
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < rows.Count && !merged; i++)
            for (int j = i + 1; j < rows.Count && !merged; j++)
            {
                var a = rows[i];
                var b = rows[j];
                double aTop = a.Min(w => w.Y), aBottom = a.Max(w => w.Bottom);
                double bTop = b.Min(w => w.Y), bBottom = b.Max(w => w.Bottom);
                double overlap = Math.Min(aBottom, bBottom) - Math.Max(aTop, bTop);
                double minHeight = Math.Min(aBottom - aTop, bBottom - bTop);
                double maxHeight = Math.Max(aBottom - aTop, bBottom - bTop);
                if (minHeight <= 0 || overlap < minHeight * 0.6) continue;

                double gap = Math.Max(b.Min(w => w.X) - a.Max(w => w.Right), a.Min(w => w.X) - b.Max(w => w.Right));
                if (gap > maxHeight * 2.5) continue;

                rows[i] = a.Concat(b).OrderBy(w => w.X).ToList();
                rows.RemoveAt(j);
                merged = true;
            }
        }
        return rows;
    }

    private static List<LineInfo> ToLines(IReadOnlyList<IReadOnlyList<WordBox>> wordLines)
    {
        var lines = new List<LineInfo>();
        foreach (var words in wordLines)
        {
            var sb = new StringBuilder();
            string? previous = null;
            foreach (var word in words)
            {
                // В китайском и японском слова не разделяются пробелами.
                if (previous != null && !(IsCjk(previous[^1]) && IsCjk(word.Text[0])))
                    sb.Append(' ');
                sb.Append(word.Text);
                previous = word.Text;
            }

            lines.Add(new LineInfo(
                FixConfusedCharacters(sb.ToString()),
                words.Min(w => w.X),
                words.Min(w => w.Y),
                words.Max(w => w.Right),
                words.Max(w => w.Bottom)));
        }
        return lines;
    }

    /// <summary>
    /// Склеивает строки, на которые абзац разбит переносами, в одну строку — иначе переводчик
    /// получает обрывки предложений («…when you close» / «the window…») и переводит их неверно.
    /// Строки списков, заголовки и короткие подписи интерфейса остаются отдельными строками.
    /// </summary>
    private static string BuildText(List<LineInfo> lines)
    {
        if (lines.Count == 0) return "";

        double lineHeight = Median(lines.Select(l => l.Height));
        double left = lines.Min(l => l.Left);
        double right = lines.Max(l => l.Right);
        double width = Math.Max(1, right - left);

        var sb = new StringBuilder(lines[0].Text);
        bool prevJoined = false;
        double paragraphRight = lines[0].Right;
        for (int i = 1; i < lines.Count; i++)
        {
            var prev = lines[i - 1];
            var cur = lines[i];

            double gap = cur.Top - prev.Bottom;
            bool closeBelow = gap >= -lineHeight * 0.3 && gap < lineHeight * 0.8;
            bool prevFillsWidth = prev.Right >= left + width * 0.7;
            bool sameIndent = Math.Abs(cur.Left - prev.Left) < lineHeight * 1.5;
            bool prevHasWords = prev.Text.Count(c => c == ' ') >= 2 || prev.Text.Any(IsCjk);
            bool prevEndsBlock = prev.Text.EndsWith(':');

            // Обычный абзац: предыдущая строка дошла почти до правого края.
            // Строки интерфейса игр и программ («[70R] The Fall of the Betrayer» / «Illidan Stormrage: 0/1»)
            // бывают одной ширины, но это отдельные пункты, а не перенесённое предложение.
            // Следующая строка начинается со строчной буквы — это продолжение предложения, а не новый пункт.
            bool continuesSentence = char.IsLower(cur.Text[0]) && !EndsSentence(prev.Text);
            // Заголовок над абзацем: крупнее текста или в основном из слов ЗАГЛАВНЫМИ, а абзац — нет
            // («WANTED: THE EPOCH HUNTER'S HEAD» / «Wind Trader Zhareem has asked…»).
            bool headingAbove = prev.Height > cur.Height * 1.4 ||
                                (CapsWordShare(prev.Text) >= 0.5 && CapsWordShare(cur.Text) < 0.5);
            // Внутри уже начатого абзаца строка из имён собственных («Storm Mark of Honor to an Alliance»)
            // похожа на заголовок, но это продолжение предложения — пока оно не закончилось точкой.
            bool insideParagraph = prevJoined && !EndsSentence(prev.Text);
            // Строку переносят, только когда следующее слово не помещается. Если ширина абзаца уже известна
            // и первое слово следующей строки влезло бы, абзац закончился («…по League of Legends» /
            // «League of Legends и ещё 3» — подпись канала под названием видео).
            double paragraphWidthRight = Math.Max(paragraphRight, prev.Right);
            int firstWordLength = cur.Text.IndexOf(' ') is > 0 and var space ? space : cur.Text.Length;
            double firstWordWidth = (cur.Right - cur.Left) * firstWordLength / Math.Max(1, cur.Text.Length);
            bool nextWordFits = prevJoined && prev.Right + lineHeight * 0.4 + firstWordWidth < paragraphWidthRight;
            bool wrappedParagraph = prevFillsWidth && sameIndent && prevHasWords && !headingAbove && !nextWordFits &&
                                    !IsValueLine(cur.Text) &&
                                    (insideParagraph ||
                                     (!IsInterfaceLine(cur.Text) && (continuesSentence || !IsInterfaceLine(prev.Text))));
            // Строка оборвана на служебном слове («KICK THE» / «MUMMIES») — продолжение точно ниже.
            // То же для разделителя или запятой в конце строки («…видео) |» / «Гимн ЧМ-2026…»).
            bool danglingWord = EndsWithFunctionWord(prev.Text) || prev.Text.TrimEnd().EndsWith('|') ||
                                (prev.Text.TrimEnd().EndsWith(',') && prevHasWords);
            // Заголовок ЗАГЛАВНЫМИ, перенесённый на вторую строку (может быть выровнен по центру).
            bool capsHeading = TextCase.IsMostlyUpper(prev.Text) && TextCase.IsMostlyUpper(cur.Text) &&
                               prevHasWords && !EndsSentence(prev.Text);

            bool join = closeBelow && !prevEndsBlock && !IsListItem(cur.Text) &&
                        (wrappedParagraph || danglingWord || capsHeading);
            prevJoined = join;
            paragraphRight = join ? Math.Max(paragraphWidthRight, cur.Right) : cur.Right;
            if (!join)
            {
                sb.Append(Environment.NewLine).Append(cur.Text);
                continue;
            }

            string current = sb.ToString();
            char last = current[^1];
            if ((last == '-' || last == '‐') && cur.Text.Length > 0 && char.IsLower(cur.Text[0]) &&
                current.Length > 1 && char.IsLetter(current[^2]))
            {
                // Перенос слова по слогам: «autom-» + «atically» → «automatically».
                sb.Length--;
                sb.Append(cur.Text);
            }
            else if (IsCjk(last) && IsCjk(cur.Text[0]))
            {
                sb.Append(cur.Text);
            }
            else
            {
                sb.Append(' ').Append(cur.Text);
            }
        }
        return sb.ToString().Trim();
    }

    private static readonly HashSet<string> FunctionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // английский
        "the", "a", "an", "of", "to", "and", "or", "for", "with", "in", "on", "at", "by", "from", "into",
        "as", "is", "are", "was", "be", "your", "my", "our", "their", "this", "that", "its", "his", "her",
        // немецкий
        "der", "die", "das", "den", "dem", "des", "ein", "eine", "und", "oder", "mit", "von", "zu", "im", "am",
        // французский, испанский, итальянский
        "le", "la", "les", "un", "une", "de", "du", "et", "ou", "au", "aux", "pour", "avec",
        "el", "los", "las", "una", "del", "y", "con", "para", "por", "al", "il", "di", "e", "per",
        // русский
        "и", "в", "во", "на", "с", "со", "к", "по", "от", "до", "для", "из", "о", "об", "а", "но", "или", "не", "что"
    };

    /// <summary>Служебные слова, которые в английских заголовках всё равно пишут с заглавной («Bring Your Band»).</summary>
    private static readonly HashSet<string> CapitalizedInTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "is", "are", "was", "be", "your", "my", "our", "their", "this", "that", "its", "his", "her"
    };

    /// <summary>Доля слов (от двух букв), написанных целиком заглавными.</summary>
    private static double CapsWordShare(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Where(char.IsLetter).ToList())
            .Where(l => l.Count >= 2)
            .ToList();
        return words.Count == 0 ? 0 : words.Count(l => l.All(char.IsUpper)) / (double)words.Count;
    }

    private static bool EndsWithFunctionWord(string text)
    {
        int end = text.Length;
        while (end > 0 && !char.IsLetter(text[end - 1])) end--;
        if (end < text.Length) return false; // после слова стоит знак препинания — это конец фразы
        int start = end;
        while (start > 0 && char.IsLetter(text[start - 1])) start--;
        return end > start && FunctionWords.Contains(text[start..end]);
    }

    /// <summary>
    /// Строка интерфейса, а не часть абзаца: счётчик или значение после двоеточия («Medallion of Karabor: 0/1»,
    /// «Health: 75%»), метка в квадратных скобках в начале («[70R] …») или заголовок, где все значимые
    /// слова с заглавной буквы («The Fall of the Betrayer»).
    /// </summary>
    private static bool IsInterfaceLine(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return false;
        return IsValueLine(t) || IsTitleCase(t);
    }

    /// <summary>Пункт со значением или меткой («Illidan Stormrage: 0/1», «[70R] …») — точно не часть абзаца.</summary>
    private static bool IsValueLine(string text)
    {
        var t = text.Trim();
        return ValueAtEnd.IsMatch(t) || BracketTag.IsMatch(t);
    }

    private static readonly System.Text.RegularExpressions.Regex ValueAtEnd =
        new(@"(\d\s*/\s*\d+|:\s*[\d.,]+\s*%?|\d\s*%)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex BracketTag =
        new(@"^\[[^\]]{1,10}\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsTitleCase(string text)
    {
        // Запятая внутри или точка в конце — это предложение, а не заголовок («Bring 1 Alterac Valley Mark of Honor, 1»).
        if (text.Contains(',') || (text.EndsWith('.') && !text.EndsWith(".."))) return false;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('"', '\'', '«', '»', '(', ')', ',', '.', ':', ';', '!', '?'))
            .Where(w => w.Length > 0 && char.IsLetter(w[0]))
            .ToList();
        var significant = words.Where(w => !FunctionWords.Contains(w) || CapitalizedInTitles.Contains(w)).ToList();
        // Строка ЗАГЛАВНЫМИ обрабатывается отдельно (заголовки, перенесённые на две строки).
        return significant.Count >= 2 && words.Count <= 8 &&
               significant.All(w => char.IsUpper(w[0])) && !TextCase.IsMostlyUpper(text);
    }

    // ---------- Исправление типичных ошибок распознавания ----------

    /// <summary>
    /// Игровые и декоративные шрифты Windows OCR путает предсказуемо: «0/1» читается как «O/ I»,
    /// закрывающая скобка «]» — как «l». Исправляем только там, где ошибка однозначна.
    /// </summary>
    private static string FixConfusedCharacters(string line)
    {
        // Счётчики «0/1», «3/10»: в числах O → 0, I/l/i/| → 1. Только после двоеточия, в начале строки
        // или если в счётчике есть хотя бы одна настоящая цифра — чтобы не трогать «I/O» в обычном тексте.
        line = Counter.Replace(line, m =>
        {
            bool afterColon = m.Groups["pre"].Value.Contains(':') || m.Index == 0;
            var a = m.Groups["a"].Value;
            var b = m.Groups["b"].Value;
            if (!afterColon && !(a + b).Any(char.IsDigit)) return m.Value;
            return m.Groups["pre"].Value + ToDigits(a) + "/" + ToDigits(b);
        });

        // «[70Rl …», «[OfficeRidersJ …» → «[70R] …», «[OfficeRiders] …»: скобка открыта, но не закрыта.
        if (line.Contains('[') && !line.Contains(']'))
            line = UnclosedTag.Replace(line, m => LooksLikeClosingBracket(m.Groups["tag"].Value, m.Groups["end"].Value[0])
                ? "[" + m.Groups["tag"].Value + "]"
                : m.Value, 1);
        // «lOfficeRiders] …» → «[OfficeRiders] …»: скобка закрыта, но открывающую прочитали как l/I/|.
        else if (line.Contains(']') && !line.Contains('['))
            line = UnopenedTag.Replace(line, "${pre}[${tag}]", 1);

        return line;
    }

    /// <summary>
    /// Последний символ метки — на самом деле «]»? J и | на конце слова почти не встречаются,
    /// а l/I/1 заменяем, только если метка похожа на тег (уровень, клан), а не на обычное слово
    /// вроде «[Skill»: в ней есть цифры, заглавная буква в середине или она целиком заглавная.
    /// </summary>
    private static bool LooksLikeClosingBracket(string tag, char end)
    {
        if (end is 'J' or '|') return true;
        if (tag.Any(char.IsDigit)) return true;
        bool innerUpper = tag.Skip(1).Any(char.IsUpper) && tag.Any(char.IsLower);
        bool allUpper = tag.Any(char.IsLetter) && tag.Where(char.IsLetter).All(char.IsUpper);
        return innerUpper || (allUpper && end != 'I');
    }

    private static string ToDigits(string s) =>
        new(s.Select(c => c switch { 'O' or 'o' => '0', 'I' or 'l' or 'i' or '|' => '1', _ => c }).ToArray());

    private static readonly System.Text.RegularExpressions.Regex Counter = new(
        @"(?<pre>^|:\s*|\s)(?<a>[0-9OoIli|]{1,3})\s?/\s?(?<b>[0-9OoIli|]{1,3})(?=$|[\s.,;)])",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex UnclosedTag = new(
        @"\[(?<tag>[^\[\]\s]{1,24})(?<end>[lI|1J])(?=\s|$)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex UnopenedTag = new(
        @"(?<pre>^|\s)[lI|](?<tag>[\p{Lu}\d][^\[\]\s]{0,23})\]",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool EndsSentence(string text)
    {
        var trimmed = text.TrimEnd();
        return trimmed.Length > 0 && ".!?…。！？".Contains(trimmed[^1]);
    }

    private static bool IsListItem(string text)
    {
        if (text.Length == 0) return false;
        if ("•·-–—*▪►✓☐☑".Contains(text[0])) return true;
        // «1. », «12) », «a) »
        return ListMarker.IsMatch(text);
    }

    private static readonly System.Text.RegularExpressions.Regex ListMarker =
        new(@"^(\d{1,3}|\p{L})[.)]\s", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static bool IsCjk(char c) =>
        c is >= '぀' and <= 'ヿ' or >= '㐀' and <= '䶿' or >= '一' and <= '鿿' or >= '＀' and <= '￯' or >= '　' and <= '〿';

    // =====================================================================
    //  Подготовка изображения
    // =====================================================================

    /// <summary>Мелкие изображения сразу увеличиваем в 2 раза.</summary>
    private static double InitialScale(int width, int height) =>
        ClampScale(width, height, (long)width * height < 1_000_000 ? 2.0 : 1.0);

    /// <summary>Ограничивает масштаб пределом движка OCR и бюджетом по памяти.</summary>
    private static double ClampScale(int width, int height, double scale)
    {
        double maxDim = OcrEngine.MaxImageDimension - 2 * Padding;
        scale = Math.Min(scale, maxDim / width);
        scale = Math.Min(scale, maxDim / height);
        scale = Math.Min(scale, Math.Sqrt(MaxPixels / ((double)width * height)));
        return Math.Max(0.1, scale);
    }

    /// <summary>
    /// Масштабирует изображение и добавляет поля цвета фона по краям,
    /// чтобы буквы у границы выделения не обрезались для OCR.
    /// </summary>
    private static PreparedImage Prepare(BitmapSource source, double scale)
    {
        int w = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
        int h = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
        int fullW = w + 2 * Padding, fullH = h + 2 * Padding;

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            var background = new SolidColorBrush(EdgeColor(source));
            background.Freeze();
            dc.DrawRectangle(background, null, new Rect(0, 0, fullW, fullH));
            dc.DrawImage(source, new Rect(Padding, Padding, w, h));
        }

        var target = new RenderTargetBitmap(fullW, fullH, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        int stride = fullW * 4;
        var pixels = new byte[stride * fullH];
        target.CopyPixels(pixels, stride, 0);
        return new PreparedImage(pixels, fullW, fullH, scale);
    }

    /// <summary>Средний цвет по периметру изображения — как правило, это цвет фона.</summary>
    private static Color EdgeColor(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Bgr32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;

        long b = 0, g = 0, r = 0, n = 0;
        void Add(byte[] row)
        {
            for (int i = 0; i < row.Length; i += 4)
            {
                b += row[i];
                g += row[i + 1];
                r += row[i + 2];
                n++;
            }
        }

        var line = new byte[stride];
        bgra.CopyPixels(new Int32Rect(0, 0, w, 1), line, stride, 0);
        Add(line);
        bgra.CopyPixels(new Int32Rect(0, h - 1, w, 1), line, stride, 0);
        Add(line);

        var column = new byte[4 * h];
        bgra.CopyPixels(new Int32Rect(0, 0, 1, h), column, 4, 0);
        Add(column);
        bgra.CopyPixels(new Int32Rect(w - 1, 0, 1, h), column, 4, 0);
        Add(column);

        return n == 0 ? Colors.White : Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    // =====================================================================
    //  Сверка слов между движками и со словарём
    // =====================================================================

    /// <summary>
    /// Для всего текста выбирается один движок OCR, но в смешанном тексте он ошибается в «чужих» словах:
    /// английский не знает кириллицы («Unknown command: ч» → «q»), а пиксельные шрифты ломают буквы
    /// («command» → «convnand»). Каждое слово сверяется с тем же местом у других движков и со словарём Windows.
    /// </summary>
    private static class WordMerger
    {
        /// <summary>Кириллические буквы, у которых нет латинского двойника по начертанию.</summary>
        private const string DistinctCyrillic = "бвгдёжзийлмнпфцчшщъыьэюяБГДЁЖЗИЙЛПФЦЧШЩЪЫЬЭЮЯ";

        /// <summary>Типичные ошибки OCR: что прочитано → что было. Порядок важен: сначала более вероятные.</summary>
        private static readonly (string From, string To)[] Confusions =
        {
            ("rn", "m"), ("vn", "m"), ("nn", "m"), ("rm", "m"), ("in", "m"), ("ii", "u"), ("cl", "d"), ("vv", "w"),
            ("1", "l"), ("I", "l"), ("|", "l"), ("0", "o"), ("5", "s"), ("8", "s"), ("8", "b"), ("j", "i"),
            ("n", "m"), ("n", "a"), ("c", "e"), ("v", "u"), ("ö", "d"), ("uj", "w"),
            // капитель шрифта WoW: «DE8Qjption», «DEW'Rjption» → «Description»
            ("q", "cr"), ("w'", "sc"), ("u'", "sc")
        };

        /// <summary>Ограничение времени на подбор исправлений (длинный текст с именами и терминами).</summary>
        private const int TimeBudgetMs = 300;

        private sealed record Alternative(WordBox Box, string Script);

        public static IReadOnlyList<IReadOnlyList<WordBox>> Merge(Candidate best, List<Candidate> others)
        {
            string bestScript = ScriptOf(best);
            string latinTag = bestScript == "Latin" ? best.Tag : "en-US";
            string? bestLanguage = LanguageCatalog.Normalize(best.Tag);
            bool spell = SpellCheck.IsAvailable(latinTag);
            if (others.Count == 0 && !spell) return best.Words;

            var alternatives = others
                .SelectMany(o => o.Words.SelectMany(l => l).Select(w => new Alternative(w, ScriptOf(o))))
                .ToList();
            var clock = System.Diagnostics.Stopwatch.StartNew();

            bool changed = false;
            double leftMargin = Median(best.Words.Where(l => l.Count > 0).Select(l => l[0].X));
            var result = new List<List<WordBox>>(best.Words.Count);
            foreach (var line in best.Words)
            {
                // Строка явно английская (большинство слов есть в словаре) — непонятное слово в ней не русское,
                // а искажённое английское («WAntEö: THE EPOCH HEAD», а не «ШАЛТЕД»).
                int english = spell ? line.Count(w => Split(w.Text).Core is var c && IsLatinWord(c) && IsDictionaryWord(c, latinTag) == true) : 0;
                bool latinContext = english >= 2 && english * 3 >= line.Count(w => w.Text.Any(char.IsLetter));

                var texts = line.Select(w => Choose(w, alternatives, bestScript, bestLanguage, latinTag, spell, latinContext, clock)).ToArray();
                if (bestScript != "Cyrillic") SpreadCyrillic(line, texts, alternatives);
                // «I» рядом с русским словом — не английское местоимение, а разделитель «|» («видео) | Гимн»).
                for (int i = 0; i < texts.Length; i++)
                    if (texts[i] == "I" && ((i > 0 && texts[i - 1].Any(IsCyrillic)) || (i + 1 < texts.Length && texts[i + 1].Any(IsCyrillic))))
                        texts[i] = "|";
                // Запятая, точка или черта в начале строки — край рамки или орнамента («, This quest…», «| Arathi…»).
                if (texts.Length > 1 && texts[0] is "," or "." or ";" or "•" or "·" or "|") texts[0] = "";
                // Вертикальный край рамки целиком левее общего края текста читается как «II», «Ii», «l».
                if (texts.Length > 1 && texts[0].Length > 0 && texts[0].All(c => "Il|i1!".Contains(c)) &&
                    line[0].Right < leftMargin - 2 && Math.Abs(line[1].X - leftMargin) < line[1].Height)
                    texts[0] = "";

                var newLine = new List<WordBox>(line.Count);
                for (int i = 0; i < line.Count; i++)
                {
                    if (texts[i] != line[i].Text) changed = true;
                    if (texts[i].Length == 0) continue;
                    newLine.Add(texts[i] == line[i].Text ? line[i] : line[i] with { Text = texts[i] });
                }
                if (newLine.Count > 0) result.Add(newLine);
            }
            changed |= InsertMissing(result, alternatives, latinTag, spell);
            return changed ? result : best.Words;
        }

        /// <summary>
        /// Слова, которые основной движок пропустил целиком (английский не увидел «музыки» в «Набор музыки»),
        /// а другой движок прочитал как настоящее слово, вставляются на своё место в строке.
        /// </summary>
        private static bool InsertMissing(List<List<WordBox>> lines, List<Alternative> alternatives, string latinTag, bool spell)
        {
            var covered = lines.SelectMany(l => l).ToList();
            bool inserted = false;
            foreach (var alt in alternatives)
            {
                if (covered.Any(w => Intersects(w, alt.Box))) continue;
                var core = Split(alt.Box.Text).Core;
                if (core.Count(char.IsLetter) < 2) continue;

                bool cyrillic = core.Where(char.IsLetter).All(IsCyrillic);
                bool valid = cyrillic
                    ? IsDictionaryWord(core, "ru-RU") == true
                    : spell && IsLatinWord(core) && IsDictionaryWord(core, latinTag) == true;
                if (!valid) continue;

                int lineIndex = -1;
                double bestOverlap = 0;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].Count == 0) continue;
                    double top = lines[i].Min(w => w.Y), bottom = lines[i].Max(w => w.Bottom);
                    double overlap = (Math.Min(bottom, alt.Box.Bottom) - Math.Max(top, alt.Box.Y)) / alt.Box.Height;
                    if (overlap > bestOverlap) (bestOverlap, lineIndex) = (overlap, i);
                }
                if (lineIndex < 0 || bestOverlap < 0.5) continue;

                var line = lines[lineIndex];
                int position = line.FindIndex(w => w.X > alt.Box.X);
                line.Insert(position < 0 ? line.Count : position, alt.Box);
                covered.Add(alt.Box);
                inserted = true;
            }
            return inserted;
        }

        private static bool Intersects(WordBox a, WordBox b) =>
            Math.Min(a.Right, b.Right) > Math.Max(a.X, b.X) && Math.Min(a.Bottom, b.Bottom) > Math.Max(a.Y, b.Y);

        /// <summary>Похожие по начертанию латинские буквы и цифры → кириллица («Ha60p», «На60р» → «Набор»).</summary>
        private static string? ShapeToCyrillic(string core)
        {
            const string from = "AaBbCcEeHKkMmOoPpTtXxYyunr6034";
            const string to = "АаВьСсЕеНКкМмОоРрТтХхУуипгбозч";
            var sb = new StringBuilder(core.Length);
            foreach (char c in core)
            {
                if (IsCyrillic(c)) { sb.Append(c); continue; }
                int i = from.IndexOf(c);
                if (i < 0) return null;
                sb.Append(to[i]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Русская фраза внутри английского текста: короткие слова без «особых» букв («кто», «на») латинский
        /// движок читает как похожие латинские («KTO», «Ha» — а «Ha» ещё и есть в английском словаре).
        /// Если соседнее слово уже оказалось русским, а русский движок прочитал это слово как настоящее
        /// русское слово, берём его.
        /// </summary>
        private static void SpreadCyrillic(IReadOnlyList<WordBox> line, string[] texts, List<Alternative> alternatives)
        {
            if (!SpellCheck.IsAvailable("ru-RU")) return;
            bool progress = true;
            while (progress)
            {
                progress = false;
                for (int i = 0; i < texts.Length; i++)
                {
                    if (texts[i].Any(IsCyrillic)) continue;
                    bool neighbour = (i > 0 && texts[i - 1].Any(IsCyrillic)) || (i + 1 < texts.Length && texts[i + 1].Any(IsCyrillic));
                    if (!neighbour) continue;

                    var alt = alternatives
                        .Where(a => a.Script == "Cyrillic" && Overlap(a.Box, line[i]) >= 0.5)
                        .OrderByDescending(a => Overlap(a.Box, line[i]))
                        .FirstOrDefault();
                    var core = alt == null ? "" : Split(alt.Box.Text).Core;
                    if (core.Length > 0 && core.Where(char.IsLetter).All(IsCyrillic) && core.Any(char.IsLetter) &&
                        IsDictionaryWord(core, "ru-RU") == true)
                    {
                        texts[i] = alt!.Box.Text;
                        progress = true;
                        continue;
                    }

                    // Строчные русские буквы латинский движок читает как заглавные латинские («ЧМ-2026» → «MM-2026»,
                    // «дн.» → «AH.»). Берём слово русского движка, если в нём столько же букв и те же цифры.
                    var latinLetters = texts[i].Where(char.IsLetter).ToList();
                    if (alt != null && core.Any(c => DistinctCyrillic.Contains(c)) && core.Where(char.IsLetter).All(IsCyrillic) &&
                        latinLetters.Count > 0 && latinLetters.All(char.IsUpper) &&
                        core.Count(char.IsLetter) == latinLetters.Count &&
                        new string(core.Where(char.IsDigit).ToArray()) == new string(texts[i].Where(char.IsDigit).ToArray()))
                    {
                        var (ownPrefix, _, ownSuffix) = Split(texts[i]);
                        texts[i] = ownPrefix + core + ownSuffix;
                        progress = true;
                        continue;
                    }

                    // Русский движок это слово не прочитал — пробуем «перевести» похожие буквы («KTO» → «КТО»).
                    var (prefix, own, suffix) = Split(texts[i]);
                    if (own.Length >= 2 && ShapeToCyrillic(own) is { } shaped && IsDictionaryWord(shaped, "ru-RU") == true)
                    {
                        texts[i] = prefix + shaped + suffix;
                        progress = true;
                    }
                }
            }
        }

        /// <summary>
        /// Есть ли слово в словаре. Слова ЗАГЛАВНЫМИ проверка орфографии Windows по умолчанию пропускает,
        /// поэтому проверяем их строчными («KTO» → «kto» — нет такого слова).
        /// </summary>
        private static bool? IsDictionaryWord(string core, string tag)
        {
            // Слова с цифрами проверка орфографии тоже пропускает, а для OCR «8AGE» или «wi11» — не слово.
            if (core.Any(c => char.IsDigit(c) || c == '|'))
                return SpellCheck.IsAvailable(tag) ? false : null;
            var (word, wordCase) = NormalizeCase(core);
            // «YouTube», «McDonald» есть в словаре именно так — проверяем сначала исходное написание.
            if (wordCase == WordCase.Capitalized && SpellCheck.IsWord(core, tag) == true) return true;
            return SpellCheck.IsWord(word, tag);
        }

        private enum WordCase { AsIs, Upper, Capitalized }

        /// <summary>
        /// Слово ЗАГЛАВНЫМИ проверяем строчными, а слово со смешанным регистром из декоративного шрифта
        /// с капителью («CovEnnnt», «DEscRjption») — тоже строчными, а исправленное пишем с заглавной.
        /// </summary>
        private static (string Word, WordCase Case) NormalizeCase(string core)
        {
            var letters = core.Where(char.IsLetter).ToList();
            if (letters.Count > 1 && letters.All(char.IsUpper)) return (core.ToLowerInvariant(), WordCase.Upper);
            if (letters.Count > 2 && letters.Skip(1).Any(char.IsUpper) && letters.Skip(1).Any(char.IsLower))
                return (core.ToLowerInvariant(), WordCase.Capitalized);
            return (core, WordCase.AsIs);
        }

        private static string Choose(WordBox word, List<Alternative> alternatives, string bestScript, string? bestLanguage,
            string latinTag, bool spell, bool latinContext, System.Diagnostics.Stopwatch clock)
        {
            var (prefix, core, suffix) = Split(word.Text);
            if (core.Length == 0) return word.Text;

            // 0. Буквы вперемешку с цифрами, похожими на кириллицу: жирное «б» читается как «6» («Ha60p», «На60р» → «Набор»).
            //    (Слова с цифрами проверка орфографии Windows пропускает, поэтому английским словарём их не проверить.)
            if (core.Any(char.IsDigit) && core.Any(char.IsLetter) &&
                ShapeToCyrillic(core) is { } shaped && IsDictionaryWord(shaped, "ru-RU") == true)
                return prefix + shaped + suffix;

            // Одиночная строчная «i» или «l» — не слово ни в английском, ни в русском: это разделитель «|».
            if (word.Text is "i" or "l" && bestLanguage is "en" or "ru")
                return "|";

            var matches = alternatives
                .Select(a => (a, Overlap: Overlap(a.Box, word)))
                .Where(x => x.Overlap >= 0.5)
                .OrderByDescending(x => x.Overlap)
                .Select(x => x.a)
                .ToList();

            // 1. Кириллическое слово, которое «латинский» движок прочитать не мог («ч» → «q», «привет» → «npnBeT»).
            //    Русский движок и сам иногда «видит» кириллицу в латинице («cofin» → «собп»), поэтому слово берём,
            //    только если оно есть в русском словаре, это одиночная буква или латинский вариант явно бессмыслен.
            //    Одиночная «V», которую русский движок читает как «Ч», — это галочка «✓», а не буква.
            if (bestScript != "Cyrillic" && !core.Any(IsCyrillic) && core is not ("v" or "V") &&
                matches.FirstOrDefault(m => m.Script == "Cyrillic" && IsDistinctCyrillic(Split(m.Box.Text).Core)) is { } cyr &&
                !(core.Length >= 2 && spell && IsDictionaryWord(core, latinTag) == true))
            {
                var cyrCore = Split(cyr.Box.Text).Core;
                bool singleLetter = cyrCore.Length == 1 && core.Length == 1 && core is not ("a" or "A" or "I");
                // Бессмысленное латинское слово, которое исправляется в английское («COncErtED» → «Concerted»),
                // — искажённое английское, а не русское («СОЛСЦТЕД»).
                bool garbledRussian = LooksGarbled(core) && !latinContext &&
                                      !(spell && IsLatinWord(core) && FixByConfusions(core, latinTag) != null);
                if (singleLetter || IsDictionaryWord(cyrCore, "ru-RU") == true || garbledRussian)
                    return cyr.Box.Text;
            }

            // Одиночная строчная буква (кроме «a», «x») в английском тексте — завиток рамки или орнамента («HEAD d»).
            if (bestLanguage == "en" && word.Text.Length == 1 && char.IsLower(word.Text[0]) && word.Text is not ("a" or "x"))
                return "";

            if (!spell || !IsLatinWord(core) || IsDictionaryWord(core, latinTag) != false) return word.Text;

            // 2. Другой движок прочитал это слово правильно.
            foreach (var m in matches)
            {
                var other = Split(m.Box.Text).Core;
                if (other != core && IsLatinWord(other) && IsDictionaryWord(other, latinTag) == true)
                    return prefix + other + suffix;
                // Движки расходятся только в «1/l», «0/o», а слова нет в словаре — цифры, скорее всего, настоящие (ник «P1ayer1»).
                if (other.Any(char.IsDigit) && !core.Any(char.IsDigit) &&
                    string.Equals(other.Replace('1', 'l').Replace('0', 'o'), core.Replace('I', 'l').Replace('O', 'o'),
                        StringComparison.OrdinalIgnoreCase))
                    return prefix + other + suffix;
            }

            // 3. Типичная путаница букв даёт настоящее слово («convnand» → «command», «wi11» → «will»).
            if (clock.ElapsedMilliseconds > TimeBudgetMs || core.Length is < 3 or > 16) return word.Text;
            var corrected = FixByConfusions(core, latinTag);
            return corrected == null ? word.Text : prefix + corrected + suffix;
        }

        private static string? FixByConfusions(string core, string tag)
        {
            var (w, wordCase) = NormalizeCase(core);

            var level1 = Variants(w).Distinct().ToList();
            foreach (var v in level1)
                if (IsValid(v, tag)) return Restore(v, wordCase);
            foreach (var v in level1)
                foreach (var v2 in Variants(v))
                    if (IsValid(v2, tag)) return Restore(v2, wordCase);

            // Дальше — только для явно искажённых слов (смешанный регистр, цифры среди букв).
            if (wordCase != WordCase.Capitalized && !core.Any(char.IsDigit)) return null;

            // Три замены сразу («DE8Qjption»: 8→s, Q→cr, j→i), с ограничением по времени.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var level2 = level1.SelectMany(Variants).Distinct().ToList();
            foreach (var v2 in level2.TakeWhile(_ => clock.ElapsedMilliseconds < 100))
                foreach (var v3 in Variants(v2))
                    if (IsValid(v3, tag)) return Restore(v3, wordCase);

            // Подсказка словаря, если она отличается всего на одну букву («Escription» → «Description»).
            // Подсказки для сильно искажённых слов ненадёжны («dewription» → «decryption»), их не берём.
            foreach (var v in level1.Prepend(w).Where(v => v.Length >= 5 && v.All(c => char.IsLetter(c) || c == '\'')))
                foreach (var s in SpellCheck.Suggest(v, tag, 3))
                    if (s.All(c => char.IsLetter(c) || c == '\'') && EditDistance(v, s.ToLowerInvariant()) == 1)
                        return Restore(s.ToLowerInvariant(), wordCase);
            return null;
        }

        private static int EditDistance(string a, string b)
        {
            var row = Enumerable.Range(0, b.Length + 1).ToArray();
            for (int i = 1; i <= a.Length; i++)
            {
                int diagonal = row[0];
                row[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int above = row[j];
                    row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                    diagonal = above;
                }
            }
            return row[b.Length];
        }

        private static IEnumerable<string> Variants(string s)
        {
            foreach (var (from, to) in Confusions)
            {
                int i = s.IndexOf(from, StringComparison.Ordinal);
                while (i >= 0)
                {
                    yield return s[..i] + to + s[(i + from.Length)..];
                    i = s.IndexOf(from, i + 1, StringComparison.Ordinal);
                }
            }
        }

        private static bool IsValid(string word, string tag) =>
            word.Length >= 2 && char.IsLetter(word[0]) && word.All(c => char.IsLetter(c) || c == '\'') &&
            SpellCheck.IsWord(word, tag) == true;

        private static string Restore(string word, WordCase wordCase) => wordCase switch
        {
            WordCase.Upper => word.ToUpperInvariant(),
            WordCase.Capitalized => char.ToUpperInvariant(word[0]) + word[1..],
            _ => word
        };

        /// <summary>
        /// Слово латиницей: допускаются цифры и «|», которые OCR ставит вместо букв, и апостроф («SAGE'S»).
        /// С цифры может начинаться, только если букв заметно больше («8AGE'8», но не «216e» или «70R»).
        /// </summary>
        private static bool IsLatinWord(string core)
        {
            if (core.Length == 0) return false;
            int letters = 0, digits = 0;
            foreach (char c in core)
            {
                if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' || (char.IsLetter(c) && c is >= 'À' and <= 'ɏ')) letters++;
                else if (char.IsDigit(c) || c == '|') digits++;
                else if (c != '\'') return false;
            }
            return letters >= (char.IsLetter(core[0]) ? 2 : 3) && letters >= digits;
        }

        private static bool IsCyrillic(char c) => c is >= 'Ѐ' and <= 'ӿ';

        /// <summary>Заглавная буква посреди строчных («npnBeT») — так латинский движок читает кириллицу.</summary>
        private static bool LooksGarbled(string core)
        {
            for (int i = 1; i < core.Length - 1; i++)
                if (char.IsUpper(core[i]) && char.IsLower(core[i - 1]) && char.IsLower(core[i + 1])) return true;
            return false;
        }

        private static bool IsDistinctCyrillic(string core) =>
            core.Length > 0 && core.Where(char.IsLetter).All(IsCyrillic) && core.Any(c => DistinctCyrillic.Contains(c));

        /// <summary>Знаки препинания по краям слова: «(command:» → «(», «command», «:».</summary>
        private static (string Prefix, string Core, string Suffix) Split(string text)
        {
            int start = 0, end = text.Length;
            while (start < end && !char.IsLetterOrDigit(text[start])) start++;
            while (end > start && !char.IsLetterOrDigit(text[end - 1])) end--;
            return (text[..start], text[start..end], text[end..]);
        }

        /// <summary>Доля пересечения прямоугольников слов (пересечение / объединение).</summary>
        private static double Overlap(WordBox a, WordBox b)
        {
            double w = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
            double h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
            if (w <= 0 || h <= 0) return 0;
            double inter = w * h;
            return inter / (a.Width * a.Height + b.Width * b.Height - inter);
        }

        private static string ScriptOf(Candidate c) => LanguageDetector.ScriptOfLanguage(LanguageCatalog.Normalize(c.Tag));
    }
}
