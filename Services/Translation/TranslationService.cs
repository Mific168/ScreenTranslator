using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ScreenTranslator.Models;
using ScreenTranslator.Services.Offline;

namespace ScreenTranslator.Services.Translation;

/// <summary>
/// Выбирает провайдера по настройкам, применяет таймаут 10 секунд
/// и превращает любые исключения в понятный результат с текстом ошибки.
/// </summary>
public sealed class TranslationService
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Офлайн-переводу нужно больше времени на длинный текст и загрузку модели в память.</summary>
    public static readonly TimeSpan OfflineTimeout = TimeSpan.FromSeconds(120);

    private readonly OfflineModelManager _offlineModels;

    public TranslationService(OfflineModelManager offlineModels)
    {
        _offlineModels = offlineModels;
    }

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        ConnectTimeout = RequestTimeout
    })
    {
        Timeout = Timeout.InfiniteTimeSpan // таймаут задаётся через CancellationToken
    };

    static TranslationService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("ScreenTranslator/1.0");
    }

    /// <param name="sourceLanguage">Язык исходного текста или null — пусть определит провайдер.</param>
    /// <param name="sourceHint">
    /// Язык, определённый самим приложением (по OCR и тексту). LibreTranslate часто ошибается
    /// с автоопределением на коротких фразах («Agat Atelier» → французский), поэтому для него
    /// подсказка используется вместо автоопределения. Офлайн-переводчику язык нужен всегда.
    /// </param>
    public Task<TranslationResult> TranslateAsync(string text, string? sourceLanguage, string targetLanguage,
        CancellationToken ct, string? sourceHint = null)
    {
        var settings = SettingsService.Current;
        // Google определяет язык надёжнее, чем подсказка по OCR (латиница — не обязательно английский).
        if (sourceLanguage == null && sourceHint != null && sourceHint != targetLanguage &&
            settings.Provider != TranslationProviderKind.Online)
            sourceLanguage = sourceHint;

        ITranslationProvider provider = settings.Provider switch
        {
            TranslationProviderKind.LibreTranslate => new LibreTranslateProvider(Http, settings.LibreUrl, SettingsService.GetLibreKey()),
            TranslationProviderKind.Online => new GoogleOnlineProvider(Http),
            _ => new OfflineProvider(_offlineModels)
        };

        return TranslateWithAsync(provider, text, sourceLanguage, targetLanguage, ct);
    }

    /// <summary>Перевод через конкретного провайдера (используется и для проверки в настройках).</summary>
    public static async Task<TranslationResult> TranslateWithAsync(ITranslationProvider provider, string text,
        string? sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return TranslationResult.Fail(provider.Name, TranslationError.ServerError, "Нет текста для перевода.");

        // Строки ЗАГЛАВНЫМИ переводчики оставляют без перевода — приводим их к обычному регистру.
        text = TextCase.NormalizeForTranslation(text);

        var limit = provider is OfflineProvider ? OfflineTimeout : RequestTimeout;
        using var timeout = new CancellationTokenSource(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await provider.TranslateAsync(text, sourceLanguage, targetLanguage, linked.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return TranslationResult.Fail(provider.Name, TranslationError.Cancelled, "Перевод отменён.");
        }
        catch (OperationCanceledException ex)
        {
            Log.Error($"Таймаут {provider.Name}", ex);
            return TranslationResult.Fail(provider.Name, TranslationError.Timeout,
                provider is OfflineProvider
                    ? $"Офлайн-перевод не уложился в {limit.TotalSeconds:0} секунд — текст слишком длинный. Выделите фрагмент поменьше."
                    : $"{provider.Name} не ответил за {limit.TotalSeconds:0} секунд. Проверьте подключение к интернету.");
        }
        catch (HttpRequestException ex)
        {
            Log.Error($"Сетевая ошибка {provider.Name}", ex);
            return TranslationResult.Fail(provider.Name, TranslationError.NoConnection,
                $"Нет подключения к интернету или сервер {provider.Name} недоступен.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IndexOutOfRangeException or KeyNotFoundException)
        {
            Log.Error($"Неожиданный ответ {provider.Name}", ex);
            return TranslationResult.Fail(provider.Name, TranslationError.ServerError,
                $"{provider.Name} вернул неожиданный ответ.");
        }
        catch (Exception ex)
        {
            Log.Error($"Ошибка перевода {provider.Name}", ex);
            return TranslationResult.Fail(provider.Name, TranslationError.ServerError,
                $"Ошибка перевода: {ex.Message}");
        }
    }

    public static ITranslationProvider CreateLibre(string url, string key) => new LibreTranslateProvider(Http, url, key);

    public static ITranslationProvider CreateOnline() => new GoogleOnlineProvider(Http);
}
