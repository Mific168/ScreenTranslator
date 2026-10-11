using System.Threading;
using System.Threading.Tasks;

namespace ScreenTranslator.Services.Translation;

public interface ITranslationProvider
{
    string Name { get; }

    /// <param name="sourceLanguage">Внутренний код языка или null для автоопределения.</param>
    /// <param name="targetLanguage">Внутренний код языка перевода.</param>
    Task<TranslationResult> TranslateAsync(string text, string? sourceLanguage, string targetLanguage, CancellationToken ct);
}
