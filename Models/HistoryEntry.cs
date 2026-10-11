using System;
using ScreenTranslator.Services;

namespace ScreenTranslator.Models;

public enum HistoryKind
{
    /// <summary>Перевод выделенной области.</summary>
    Region = 0,
    /// <summary>Перевод ответа пользователя.</summary>
    Reply = 1
}

/// <summary>Запись истории переводов.</summary>
public sealed class HistoryEntry
{
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public HistoryKind Kind { get; set; }
    public string SourceText { get; set; } = "";
    public string TranslatedText { get; set; } = "";
    public string? SourceLang { get; set; }
    public string TargetLang { get; set; } = "";

    // ----- Свойства для отображения в списке -----
    public string KindText => Kind == HistoryKind.Region ? "Область экрана" : "Ответ";
    public string KindGlyph => Kind == HistoryKind.Region ? "" : "";
    public string DateText => CreatedAt.ToString("dd.MM.yyyy HH:mm");
    public string LangsText =>
        $"{(string.IsNullOrEmpty(SourceLang) ? "авто" : LanguageCatalog.NameOf(SourceLang))} → {LanguageCatalog.NameOf(TargetLang)}";
    public string SourcePreview => Preview(SourceText);
    public string TranslationPreview =>
        string.IsNullOrEmpty(TranslatedText) ? "(перевод открывался в браузере)" : Preview(TranslatedText);

    public bool Matches(string query) =>
        SourceText.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        TranslatedText.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private static string Preview(string text)
    {
        var oneLine = text.Replace("\r", " ").Replace("\n", " ");
        return oneLine.Length > 200 ? oneLine[..200] + "…" : oneLine;
    }
}
