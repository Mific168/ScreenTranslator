using System;
using System.Linq;
using System.Text;

namespace ScreenTranslator.Services;

/// <summary>
/// Работа с регистром букв. Машинные переводчики плохо переводят текст ЗАГЛАВНЫМИ буквами
/// (принимают слова за названия и аббревиатуры): «EMBALMING ROOM — KICK THE MUMMIES» остаётся
/// без перевода, а «Embalming room — kick the mummies» → «Бальзамирующая комната — пинайте мумии».
/// </summary>
public static class TextCase
{
    /// <summary>Почти все буквы строки — заглавные (и букв достаточно, чтобы это был не значок или код).</summary>
    public static bool IsMostlyUpper(string text)
    {
        int upper = 0, cased = 0;
        foreach (char c in text)
        {
            if (!char.IsLetter(c) || char.ToUpperInvariant(c) == char.ToLowerInvariant(c)) continue;
            cased++;
            if (char.IsUpper(c)) upper++;
        }
        return cased >= 4 && upper >= cased * 0.85;
    }

    /// <summary>
    /// Переводит строки, написанные целиком заглавными, в обычный регистр («Как в предложении»).
    /// Остальные строки не меняются, поэтому отдельные выделенные слова (MUST, API) сохраняются.
    /// </summary>
    public static string NormalizeForTranslation(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Any(char.IsUpper)) return text;

        var lines = text.Split('\n');
        bool changed = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var normalized = IsMostlyUpper(line) ? ToSentenceCase(line) : LowerEmphasizedWords(line);
            if (normalized == line) continue;
            lines[i] = normalized;
            changed = true;
        }
        return changed ? string.Join("\n", lines) : text;
    }

    /// <summary>Частые слова, которые выделяют заглавными для акцента («you MUST», «do NOT»).</summary>
    private static readonly System.Collections.Generic.HashSet<string> EmphasisWords = new(StringComparer.Ordinal)
    {
        "MUST", "NOT", "ALL", "ONLY", "NEVER", "ALWAYS", "NO", "DO", "DON'T", "FREE", "NEW", "NOW", "NOTE",
        "STOP", "YOU", "YOUR", "AND", "OR", "THE", "BE", "IS", "ARE", "WILL", "CAN", "CANNOT", "VERY", "EVERY",
        "BEFORE", "AFTER", "ONE", "TWO", "OFF", "ON", "OUT", "BACK", "NEXT", "LAST", "BEST", "MORE", "LESS"
    };

    /// <summary>
    /// В обычной строке переводит в нижний регистр слова, выделенные заглавными для акцента:
    /// «MUST be interrupted» → «must be interrupted». Аббревиатуры (API, USB, HTML) не трогаем:
    /// меняются только слова из списка выше и слова в заглавных длиннее пяти букв.
    /// </summary>
    private static string LowerEmphasizedWords(string line) =>
        EmphasizedWord.Replace(line, m =>
            m.Value.Length > 5 || EmphasisWords.Contains(m.Value) ? m.Value.ToLowerInvariant() : m.Value);

    private static readonly System.Text.RegularExpressions.Regex EmphasizedWord =
        new(@"(?<![\p{L}\d])\p{Lu}{2,}(?:'\p{Lu}+)?(?![\p{L}\d])", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string ToSentenceCase(string line)
    {
        var sb = new StringBuilder(line.Length);
        bool capitalizeNext = true;
        int wordStart = 0;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (!char.IsLetterOrDigit(c)) wordStart = i + 1;

            if (char.IsLetter(c))
            {
                // Слова с цифрами (MP3, X2, 4K) оставляем как есть.
                bool wordHasDigit = WordHasDigit(line, wordStart);
                if (wordHasDigit) sb.Append(c);
                else sb.Append(capitalizeNext ? char.ToUpper(c) : char.ToLower(c));
                capitalizeNext = false;
            }
            else
            {
                sb.Append(c);
                if (".!?".Contains(c)) capitalizeNext = true;
            }
        }
        return sb.ToString();
    }

    private static bool WordHasDigit(string line, int start)
    {
        for (int i = start; i < line.Length && char.IsLetterOrDigit(line[i]); i++)
            if (char.IsDigit(line[i])) return true;
        return false;
    }
}
