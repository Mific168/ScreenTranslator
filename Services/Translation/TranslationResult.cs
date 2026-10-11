namespace ScreenTranslator.Services.Translation;

public enum TranslationError
{
    None,
    NotConfigured,
    InvalidKey,
    QuotaExceeded,
    RateLimited,
    NoConnection,
    Timeout,
    UnsupportedLanguage,
    TextTooLong,
    ServerError,
    Cancelled
}

/// <param name="DetectedSourceLanguage">Язык исходного текста, определённый провайдером (внутренний код).</param>
public sealed record TranslationResult(
    bool Success,
    string Text,
    string? DetectedSourceLanguage,
    TranslationError Error,
    string? ErrorMessage,
    string Provider)
{
    public static TranslationResult Ok(string provider, string text, string? detected) =>
        new(true, text, detected, TranslationError.None, null, provider);

    public static TranslationResult Fail(string provider, TranslationError error, string message) =>
        new(false, "", null, error, message, provider);
}
