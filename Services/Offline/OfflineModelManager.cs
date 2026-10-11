using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ScreenTranslator.Services.Offline;

/// <summary>Состояние загрузки модели (для окна настроек).</summary>
public sealed record ModelDownloadState(string ModelId, double Progress, bool IsDownloading, string? Error);

/// <summary>
/// Управляет офлайн-моделями: загрузка и удаление, загрузка в память по требованию
/// и выгрузка после простоя (чтобы в фоне приложение занимало мало памяти).
/// </summary>
public sealed class OfflineModelManager
{
    /// <summary>Через сколько минут простоя модели выгружаются из памяти.</summary>
    private static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(3);

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly Dictionary<string, Seq2SeqTranslator> _loaded = new();
    private readonly object _sync = new();
    private readonly Timer _unloadTimer;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _downloads = new();
    private readonly ConcurrentDictionary<string, ModelDownloadState> _states = new();
    private int _activeTranslations;

    /// <summary>Изменилось состояние модели (загрузка, прогресс, установка, удаление). Вызывается в UI-потоке.</summary>
    public event Action<ModelDownloadState>? StateChanged;

    static OfflineModelManager()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("ScreenTranslator/1.0");
    }

    public OfflineModelManager()
    {
        _unloadTimer = new Timer(_ => UnloadAll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public ModelDownloadState? GetState(string modelId) => _states.TryGetValue(modelId, out var s) ? s : null;

    public bool IsDownloading(string modelId) => _downloads.ContainsKey(modelId);

    // =====================================================================
    //  Перевод
    // =====================================================================

    /// <summary>
    /// Переводит текст по плану: одной моделью, цепочкой через английский или многоязычной NLLB.
    /// Выполняется в фоновом потоке.
    /// </summary>
    public Task<string> TranslateAsync(string text, IReadOnlyList<TranslationStep> plan, CancellationToken ct) =>
        Task.Run(() =>
        {
            Interlocked.Increment(ref _activeTranslations);
            try
            {
                var current = text;
                foreach (var step in plan)
                {
                    ct.ThrowIfCancellationRequested();
                    var translator = GetTranslator(step.Model);
                    current = translator.Translate(current, ct, step.SourceToken, step.TargetToken);
                }
                return current;
            }
            finally
            {
                Interlocked.Decrement(ref _activeTranslations);
                ScheduleUnload();
            }
        }, ct);

    private Seq2SeqTranslator GetTranslator(OfflineModelInfo model)
    {
        lock (_sync)
        {
            if (_loaded.TryGetValue(model.Id, out var translator)) return translator;
            translator = Seq2SeqTranslator.Load(model.Directory);
            _loaded[model.Id] = translator;
            return translator;
        }
    }

    private void ScheduleUnload() => _unloadTimer.Change(IdleUnload, Timeout.InfiniteTimeSpan);

    public void UnloadAll()
    {
        lock (_sync)
        {
            if (_loaded.Count == 0) return;
            if (Volatile.Read(ref _activeTranslations) > 0)
            {
                ScheduleUnload(); // идёт перевод — выгрузим позже
                return;
            }
            foreach (var translator in _loaded.Values) translator.Dispose();
            _loaded.Clear();
        }
        MemoryHelper.Trim();
    }

    private void Unload(string modelId)
    {
        lock (_sync)
        {
            if (_loaded.Remove(modelId, out var translator)) translator.Dispose();
        }
    }

    // =====================================================================
    //  Установка и удаление
    // =====================================================================

    /// <summary>Скачивает модель. Повторный вызов во время загрузки ничего не делает.</summary>
    public async Task<bool> DownloadAsync(OfflineModelInfo model)
    {
        if (model.IsInstalled) return true;
        var cts = new CancellationTokenSource();
        if (!_downloads.TryAdd(model.Id, cts)) return false;

        var tempDir = model.Directory + ".download";
        try
        {
            Report(new ModelDownloadState(model.Id, 0, true, null));
            Directory.CreateDirectory(tempDir);

            // Веса файлов для общего прогресса (примерные размеры в МБ).
            var weights = new Dictionary<string, double>
            {
                [Seq2SeqTranslator.ConfigFile] = 0.1,
                [Seq2SeqTranslator.TokenizerFile] = model.IsMultilingual ? 17 : 7,
                [Seq2SeqTranslator.EncoderFile] = model.IsMultilingual ? 400 : 49,
                [Seq2SeqTranslator.DecoderFile] = model.IsMultilingual ? 454 : 56
            };
            double total = weights.Values.Sum(), done = 0;

            foreach (var (remote, local) in OfflineModelCatalog.Files)
            {
                var target = Path.Combine(tempDir, local);
                double weight = weights[local];
                double startDone = done;
                if (!File.Exists(target))
                {
                    await DownloadFileAsync($"https://huggingface.co/{model.Repository}/resolve/main/{remote}", target,
                        fraction => Report(new ModelDownloadState(model.Id, (startDone + weight * fraction) / total, true, null)),
                        cts.Token);
                }
                done += weight;
            }

            // Проверяем, что модель открывается, прежде чем считать её установленной.
            using (Seq2SeqTranslator.Load(tempDir)) { }

            File.WriteAllText(Path.Combine(tempDir, OfflineModelCatalog.CompleteMarker), DateTime.Now.ToString("O"));
            if (Directory.Exists(model.Directory)) Directory.Delete(model.Directory, recursive: true);
            Directory.Move(tempDir, model.Directory);

            Report(new ModelDownloadState(model.Id, 1, false, null));
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempDir);
            Report(new ModelDownloadState(model.Id, 0, false, null));
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось скачать модель {model.Id}", ex);
            TryDelete(tempDir);
            var message = ex is HttpRequestException
                ? "Нет подключения к интернету или сервер Hugging Face недоступен."
                : ex.Message;
            Report(new ModelDownloadState(model.Id, 0, false, message));
            return false;
        }
        finally
        {
            _downloads.TryRemove(model.Id, out _);
            cts.Dispose();
        }
    }

    public void CancelDownload(string modelId)
    {
        if (_downloads.TryGetValue(modelId, out var cts)) cts.Cancel();
    }

    public void Delete(OfflineModelInfo model)
    {
        CancelDownload(model.Id);
        Unload(model.Id);
        TryDelete(model.Directory);
        Report(new ModelDownloadState(model.Id, 0, false, null));
    }

    private static async Task DownloadFileAsync(string url, string target, Action<double> progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long? length = response.Content.Headers.ContentLength;

        var part = target + ".part";
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(part))
        {
            var buffer = new byte[81920];
            long received = 0;
            int read;
            var lastReport = DateTime.MinValue;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                if (length > 0 && (DateTime.Now - lastReport).TotalMilliseconds > 150)
                {
                    progress(received / (double)length.Value);
                    lastReport = DateTime.Now;
                }
            }
        }
        File.Move(part, target, overwrite: true);
        progress(1);
    }

    private void Report(ModelDownloadState state)
    {
        _states[state.ModelId] = state;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        dispatcher.BeginInvoke(() => StateChanged?.Invoke(state));
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось удалить {directory}", ex);
        }
    }
}
