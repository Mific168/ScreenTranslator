using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenTranslator.Services.Translation;

/// <summary>
/// LibreTranslate (https://github.com/LibreTranslate/LibreTranslate) — публичный или собственный сервер.
/// </summary>
public sealed class LibreTranslateProvider : ITranslationProvider
{
    // Список языков сервера кешируется: коды отличаются между версиями (zh / zh-Hans и т.п.).
    private static readonly ConcurrentDictionary<string, HashSet<string>> LanguagesCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    public LibreTranslateProvider(HttpClient http, string baseUrl, string apiKey)
    {
        _http = http;
        _baseUrl = NormalizeUrl(baseUrl);
        _apiKey = apiKey.Trim();
    }

    public string Name => "LibreTranslate";

    /// <summary>
    /// Добавляет протокол, если его нет. Локальные серверы (localhost, 127.0.0.1, адреса домашней сети,
    /// Docker-контейнер) обычно работают по http, публичные — по https.
    /// </summary>
    public static string NormalizeUrl(string url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) return "";
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var host = url.Split('/', ':')[0];
            url = (IsLocalHost(host) ? "http://" : "https://") + url;
        }
        return url.TrimEnd('/');
    }

    /// <summary>Сервер на этом компьютере или в локальной сети.</summary>
    public static bool IsLocalHost(string host)
    {
        host = host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".home", StringComparison.OrdinalIgnoreCase))
            return true;

        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            if (System.Net.IPAddress.IsLoopback(ip)) return true;
            var b = ip.GetAddressBytes();
            return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168));
        }

        // Имя без точки (например, «nas» или «server») — компьютер в локальной сети.
        return !host.Contains('.');
    }

    public async Task<TranslationResult> TranslateAsync(string text, string? sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_baseUrl) || !Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri))
            return TranslationResult.Fail(Name, TranslationError.NotConfigured,
                "Не указан адрес сервера LibreTranslate. Укажите его в настройках.");

        bool localHttps = uri.Scheme == Uri.UriSchemeHttps && IsLocalHost(uri.Host);
        try
        {
            return await TranslateCoreAsync(text, sourceLanguage, targetLanguage, ct);
        }
        catch (Exception ex) when (localHttps && ex is HttpRequestException or OperationCanceledException)
        {
            // Частая ошибка: локальный сервер (Docker) работает по http, а в настройках указан https —
            // соединение не устанавливается или зависает до таймаута.
            Log.Error($"LibreTranslate: не удалось подключиться к {_baseUrl}", ex);
            var httpUrl = "http://" + _baseUrl["https://".Length..];
            return TranslationResult.Fail(Name, TranslationError.NoConnection,
                $"Не удалось подключиться к {_baseUrl}. Локальный сервер LibreTranslate обычно работает по http — " +
                $"укажите в настройках адрес {httpUrl}");
        }
    }

    private async Task<TranslationResult> TranslateCoreAsync(string text, string? sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        var serverLanguages = await GetLanguagesAsync(ct);

        var target = LanguageCatalog.ToLibre(targetLanguage, serverLanguages);
        if (target == null)
            return TranslationResult.Fail(Name, TranslationError.UnsupportedLanguage,
                $"Сервер LibreTranslate не поддерживает язык «{LanguageCatalog.NameOf(targetLanguage)}».");

        var source = sourceLanguage == null ? "auto" : LanguageCatalog.ToLibre(sourceLanguage, serverLanguages) ?? "auto";

        var body = new Dictionary<string, string>
        {
            ["q"] = text,
            ["source"] = source,
            ["target"] = target,
            ["format"] = "text"
        };
        if (!string.IsNullOrEmpty(_apiKey)) body["api_key"] = _apiKey;

        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/translate")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return MapError((int)response.StatusCode, json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var translated = root.GetProperty("translatedText").GetString() ?? "";
        string? detected = null;
        if (root.TryGetProperty("detectedLanguage", out var det) && det.ValueKind == JsonValueKind.Object &&
            det.TryGetProperty("language", out var lang))
            detected = lang.GetString();

        return TranslationResult.Ok(Name, translated, LanguageCatalog.Normalize(detected) ?? (sourceLanguage));
    }

    private async Task<HashSet<string>?> GetLanguagesAsync(CancellationToken ct)
    {
        if (LanguagesCache.TryGetValue(_baseUrl, out var cached)) return cached;
        try
        {
            using var response = await _http.GetAsync(_baseUrl + "/languages", ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in doc.RootElement.EnumerateArray())
                if (item.TryGetProperty("code", out var code) && code.GetString() is { } c)
                    codes.Add(c);
            LanguagesCache[_baseUrl] = codes;
            return codes;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            // Без списка языков используем коды по умолчанию.
            return null;
        }
    }

    private TranslationResult MapError(int status, string body)
    {
        string? apiMessage = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e)) apiMessage = e.GetString();
        }
        catch (JsonException)
        {
            // тело ответа не JSON
        }

        bool aboutKey = apiMessage != null && apiMessage.Contains("key", StringComparison.OrdinalIgnoreCase);
        return status switch
        {
            400 when aboutKey => TranslationResult.Fail(Name, TranslationError.InvalidKey,
                "Сервер LibreTranslate требует API-ключ. Укажите ключ в настройках или используйте другой сервер."),
            400 => TranslationResult.Fail(Name, TranslationError.UnsupportedLanguage,
                "LibreTranslate не смог выполнить перевод" + (apiMessage != null ? $": {apiMessage}" : ".")),
            401 or 403 => TranslationResult.Fail(Name, TranslationError.InvalidKey,
                "Неверный или отсутствующий API-ключ LibreTranslate."),
            429 => TranslationResult.Fail(Name, TranslationError.RateLimited,
                "Превышен лимит запросов к серверу LibreTranslate. Подождите и повторите."),
            >= 500 => TranslationResult.Fail(Name, TranslationError.ServerError,
                $"Сервер LibreTranslate вернул ошибку {status}" + (apiMessage != null ? $": {apiMessage}" : ".")),
            _ => TranslationResult.Fail(Name, TranslationError.ServerError,
                $"LibreTranslate вернул ошибку {status}" + (apiMessage != null ? $": {apiMessage}" : "."))
        };
    }
}
