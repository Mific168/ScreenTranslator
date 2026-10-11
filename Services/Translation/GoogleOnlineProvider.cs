using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenTranslator.Services.Translation;

/// <summary>
/// «Перевод онлайн» — бесплатный адрес Google Переводчика (translate.googleapis.com, client=gtx),
/// которым пользуются расширения браузеров. Ключ не нужен, но адрес неофициальный:
/// при частых запросах Google может временно ограничить доступ.
/// </summary>
public sealed class GoogleOnlineProvider : ITranslationProvider
{
    private const string Endpoint = "https://translate.googleapis.com/translate_a/single";
    private const string FallbackEndpoint = "https://clients5.google.com/translate_a/t";

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    /// <summary>Текст передаётся в адресе запроса, поэтому длинный текст отправляется частями.</summary>
    private const int MaxChunkLength = 1500;

    private readonly HttpClient _http;

    public GoogleOnlineProvider(HttpClient http)
    {
        _http = http;
    }

    public string Name => "Перевод онлайн";

    public async Task<TranslationResult> TranslateAsync(string text, string? sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        var sl = LanguageCatalog.ToGoogle(sourceLanguage);
        var tl = LanguageCatalog.ToGoogle(targetLanguage);

        var result = new StringBuilder();
        string? detected = null;
        foreach (var (chunk, separator) in SplitText(text))
        {
            result.Append(separator);
            if (chunk.Trim().Length == 0)
            {
                result.Append(chunk);
                continue;
            }

            var q = Uri.EscapeDataString(chunk);
            var (status, json) = await GetAsync($"{Endpoint}?client=gtx&sl={sl}&tl={tl}&dt=t&q={q}", ct);
            bool fallback = false;
            if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                // Основной адрес временно ограничен — пробуем адрес расширения Chrome.
                (status, json) = await GetAsync($"{FallbackEndpoint}?client=dict-chrome-ex&sl={sl}&tl={tl}&q={q}", ct);
                fallback = true;
            }

            if (status != HttpStatusCode.OK)
                return MapError(status);

            var (translated, lang) = fallback ? ParseFallback(json) : Parse(json);
            result.Append(translated);
            detected ??= lang;
        }

        return TranslationResult.Ok(Name, result.ToString().Trim(),
            sourceLanguage ?? LanguageCatalog.Normalize(detected));
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // С заголовком обычного браузера Google реже принимает запросы за автоматические.
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        using var response = await _http.SendAsync(request, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Ответ запасного адреса: [["перевод","en"]] или ["перевод"].</summary>
    private static (string Text, string? Language) ParseFallback(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var first = doc.RootElement[0];
        return first.ValueKind == JsonValueKind.Array
            ? (first[0].GetString() ?? "", first.GetArrayLength() > 1 ? first[1].GetString() : null)
            : (first.GetString() ?? "", null);
    }

    /// <summary>
    /// Ответ — массив: [0] — сегменты [перевод, оригинал, …], [2] — определённый язык.
    /// </summary>
    private static (string Text, string? Language) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var sb = new StringBuilder();
        if (root[0].ValueKind == JsonValueKind.Array)
            foreach (var segment in root[0].EnumerateArray())
                if (segment.ValueKind == JsonValueKind.Array && segment[0].ValueKind == JsonValueKind.String)
                    sb.Append(segment[0].GetString());

        string? language = root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String
            ? root[2].GetString()
            : null;
        return (sb.ToString(), language);
    }

    /// <summary>
    /// Делит текст на части по строкам; слишком длинные строки — по предложениям и пробелам.
    /// Separator — что поставить перед переводом части: перенос строки или пробел внутри абзаца.
    /// </summary>
    private static IEnumerable<(string Text, string Separator)> SplitText(string text)
    {
        var current = new StringBuilder();
        string currentSeparator = "";
        bool started = false;
        foreach (var (piece, pieceSeparator) in SplitLines(text))
        {
            if (!started)
            {
                currentSeparator = pieceSeparator;
                started = true;
            }
            else if (current.Length + pieceSeparator.Length + piece.Length > MaxChunkLength)
            {
                yield return (current.ToString(), currentSeparator);
                current.Clear();
                currentSeparator = pieceSeparator;
            }
            else
            {
                current.Append(pieceSeparator);
            }
            current.Append(piece);
        }
        if (started) yield return (current.ToString(), currentSeparator);
    }

    private static IEnumerable<(string Piece, string Separator)> SplitLines(string text)
    {
        bool first = true;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var separator = first ? "" : "\n";
            first = false;
            var rest = line;
            while (rest.Length > MaxChunkLength)
            {
                var window = rest[..MaxChunkLength];
                var cut = window.LastIndexOfAny(new[] { '.', '!', '?', ';' });
                if (cut < MaxChunkLength / 2) cut = window.LastIndexOf(' ');
                cut = cut < MaxChunkLength / 2 ? MaxChunkLength : cut + 1;
                yield return (rest[..cut].TrimEnd(), separator);
                rest = rest[cut..].TrimStart();
                separator = " ";
            }
            yield return (rest, separator);
        }
    }

    private TranslationResult MapError(HttpStatusCode status) => (int)status switch
    {
        429 or 503 => TranslationResult.Fail(Name, TranslationError.RateLimited,
            "Google временно ограничил запросы с вашего адреса. Подождите несколько минут " +
            "или переведите офлайн."),
        413 or 414 => TranslationResult.Fail(Name, TranslationError.TextTooLong,
            "Текст слишком длинный для перевода онлайн. Выделите фрагмент поменьше."),
        _ => TranslationResult.Fail(Name, TranslationError.ServerError,
            $"Сервис перевода онлайн вернул ошибку {(int)status}. Попробуйте позже или переведите офлайн.")
    };
}
