using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ScreenTranslator.Services.Offline;

/// <summary>
/// Нейросетевой переводчик «кодировщик — декодер» в формате ONNX, выполняется локально через ONNX Runtime.
/// Поддерживает модели:
///  • Marian / Opus-MT — одно направление (en→ru), токенизатор SentencePiece Unigram;
///  • NLLB-200 / M2M100 — многоязычные: язык исходного текста задаётся токеном в начале входа
///    («eng_Latn»), язык перевода — первым токеном, который принудительно выдаёт декодер («rus_Cyrl»).
/// Декодер работает по одному токену с кэшем ключей/значений внимания; поиск — лучевой.
/// </summary>
public sealed partial class Seq2SeqTranslator : IDisposable
{
    public const string EncoderFile = "encoder_model_quantized.onnx";
    public const string DecoderFile = "decoder_model_merged_quantized.onnx";
    public const string TokenizerFile = "tokenizer.json";
    public const string ConfigFile = "config.json";

    /// <summary>Длинные «предложения» (текст без точек) переводим кусками.</summary>
    private const int MaxInputTokens = 400;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly ISubwordTokenizer _tokenizer;
    private readonly int _layers;
    private readonly int _heads;
    private readonly int _headDim;
    private readonly long _decoderStartId;
    private readonly int _eosId;
    private readonly int _padId;
    private readonly bool _useCacheIsBool;

    /// <summary>Многоязычная модель (NLLB): нужны коды языков при каждом переводе.</summary>
    public bool IsMultilingual { get; }

    /// <summary>Число лучей поиска (1 — жадный поиск).</summary>
    public int BeamWidth { get; set; } = 4;

    private Seq2SeqTranslator(string directory)
    {
        string modelType;
        using (var configStream = File.OpenRead(Path.Combine(directory, ConfigFile)))
        using (var config = JsonDocument.Parse(configStream))
        {
            var root = config.RootElement;
            modelType = root.TryGetProperty("model_type", out var type) ? type.GetString() ?? "" : "";
            _layers = root.GetProperty("decoder_layers").GetInt32();
            _heads = root.GetProperty("decoder_attention_heads").GetInt32();
            _headDim = root.GetProperty("d_model").GetInt32() / _heads;
            _decoderStartId = root.GetProperty("decoder_start_token_id").GetInt64();
            _eosId = root.GetProperty("eos_token_id").GetInt32();
            _padId = root.GetProperty("pad_token_id").GetInt32();
        }

        IsMultilingual = modelType is "m2m_100" or "nllb" or "nllb-moe";
        var tokenizerPath = Path.Combine(directory, TokenizerFile);
        _tokenizer = IsMultilingual ? BpeTokenizer.Load(tokenizerPath) : UnigramTokenizer.Load(tokenizerPath);

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            // Пул памяти ONNX Runtime ускоряет шаг декодера большой NLLB в 2,4 раза (161 → 68 мс),
            // а у маленьких Opus-MT почти ничего не даёт, но резервирует сотни мегабайт — там выключен.
            EnableCpuMemArena = IsMultilingual,
            EnableMemoryPattern = false
        };
        _encoder = new InferenceSession(Path.Combine(directory, EncoderFile), options);
        _decoder = new InferenceSession(Path.Combine(directory, DecoderFile), options);
        _useCacheIsBool = _decoder.InputMetadata.TryGetValue("use_cache_branch", out var meta) &&
                          meta.ElementType == typeof(bool);
    }

    public static Seq2SeqTranslator Load(string directory) => new(directory);

    /// <summary>Есть ли в словаре модели токен языка (например, «rus_Cyrl»).</summary>
    public bool HasLanguageToken(string token) => _tokenizer.TokenId(token) != null;

    /// <summary>Переводит текст, сохраняя разбиение на строки. Каждая строка переводится по предложениям.</summary>
    /// <param name="sourceLanguageToken">Только для многоязычных моделей: код языка текста («eng_Latn»).</param>
    /// <param name="targetLanguageToken">Только для многоязычных моделей: код языка перевода («rus_Cyrl»).</param>
    public string Translate(string text, CancellationToken ct, string? sourceLanguageToken = null, string? targetLanguageToken = null)
    {
        int? sourceId = null, targetId = null;
        if (IsMultilingual)
        {
            sourceId = sourceLanguageToken == null ? null : _tokenizer.TokenId(sourceLanguageToken);
            targetId = targetLanguageToken == null ? null : _tokenizer.TokenId(targetLanguageToken);
            if (sourceId == null || targetId == null)
                throw new InvalidOperationException($"Модель не поддерживает языки {sourceLanguageToken} → {targetLanguageToken}.");
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var result = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i > 0) result.Append(Environment.NewLine);
            var line = lines[i].Trim();
            if (line.Length == 0) continue;

            var parts = SplitSentences(line).Select(s => TranslateSentence(s, sourceId, targetId, ct)).Where(s => s.Length > 0);
            result.Append(string.Join(" ", parts));
        }
        return result.ToString().Trim();
    }

    [GeneratedRegex(@"(?<=[.!?…。！？])\s+(?=\S)")]
    private static partial Regex SentenceBoundary();

    private static IEnumerable<string> SplitSentences(string line) =>
        SentenceBoundary().Split(line).Select(s => s.Trim()).Where(s => s.Length > 0);

    private string TranslateSentence(string sentence, int? sourceId, int? targetId, CancellationToken ct)
    {
        var tokens = _tokenizer.Encode(sentence);
        if (tokens.Count == 0) return "";

        if (tokens.Count > MaxInputTokens)
        {
            var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int half = words.Length / 2;
            if (half > 0)
                return TranslateSentence(string.Join(' ', words[..half]), sourceId, targetId, ct) + " " +
                       TranslateSentence(string.Join(' ', words[half..]), sourceId, targetId, ct);
            tokens = tokens.Take(MaxInputTokens).ToList();
        }

        // Формат входа: [код языка] токены </s>
        if (sourceId != null) tokens.Insert(0, sourceId.Value);
        tokens.Add(_tokenizer.EosId);

        var output = Generate(tokens, Math.Max(1, BeamWidth), targetId, ct);
        if (targetId != null && output.Count > 0 && output[0] == targetId) output.RemoveAt(0);
        return _tokenizer.Decode(output);
    }

    // =====================================================================
    //  Генерация
    // =====================================================================

    private sealed class Hypothesis
    {
        public List<int> Tokens = new();
        public double Score;
    }

    /// <summary>
    /// Лучевой поиск: держим несколько лучших вариантов перевода и выбираем лучший целиком
    /// (оценка нормируется на длину, как в Hugging Face: length_penalty = 1).
    /// </summary>
    /// <param name="forcedFirstToken">Токен, который декодер обязан выдать первым (язык перевода NLLB).</param>
    private List<int> Generate(List<int> inputIds, int beams, int? forcedFirstToken, CancellationToken ct)
    {
        int sourceLength = inputIds.Count;
        var encoderHidden1 = Encode(inputIds, out var mask1);

        var alive = new List<Hypothesis> { new() };
        var finished = new List<(List<int> Tokens, double Score)>();
        var past = EmptyPast(1);
        var encoderHidden = encoderHidden1;
        var mask = mask1;
        int maxNew = Math.Min(400, sourceLength * 3 + 16);

        for (int step = 0; step < maxNew && alive.Count > 0; step++)
        {
            ct.ThrowIfCancellationRequested();
            int batch = alive.Count;

            if (encoderHidden.Dimensions[0] != batch)
            {
                encoderHidden = Repeat(encoderHidden1, batch);
                mask = RepeatLong(mask1, batch);
            }

            var lastTokens = alive.Select(h => h.Tokens.Count == 0 ? _decoderStartId : h.Tokens[^1]).ToArray();
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("encoder_attention_mask", mask),
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(lastTokens, new[] { batch, 1 })),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encoderHidden)
            };
            foreach (var (name, tensor) in past)
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, tensor));
            inputs.Add(UseCacheInput(step > 0));

            var candidates = new List<(int Beam, int Token, double Score)>();
            var presents = new Dictionary<string, DenseTensor<float>>();
            using (var results = _decoder.Run(inputs))
            {
                if (step == 0 && forcedFirstToken != null)
                {
                    candidates.Add((0, forcedFirstToken.Value, 0));
                }
                else
                {
                    var logits = (DenseTensor<float>)results.First(r => r.Name == "logits").AsTensor<float>();
                    int vocab = logits.Dimensions[2];
                    for (int b = 0; b < batch; b++)
                        LogSoftmaxTopK(logits.Buffer.Span.Slice(b * vocab, vocab), beams * 2, alive[b].Score, b, candidates);
                }

                foreach (var r in results)
                {
                    if (!r.Name.StartsWith("present.", StringComparison.Ordinal)) continue;
                    var key = "past_key_values." + r.Name["present.".Length..];
                    // Кэш кодировщика декодер вычисляет на первом шаге, дальше он не меняется.
                    if (step > 0 && key.Contains(".encoder.", StringComparison.Ordinal)) continue;
                    presents[key] = (DenseTensor<float>)r.AsTensor<float>().Clone();
                }
            }

            // Лучшие кандидаты: завершённые — в finished, остальные — новые лучи.
            var nextAlive = new List<Hypothesis>();
            var sourceBeams = new List<int>();
            foreach (var c in candidates.OrderByDescending(c => c.Score))
            {
                var tokens = alive[c.Beam].Tokens;
                if (c.Token == _eosId)
                {
                    int length = tokens.Count - (forcedFirstToken != null ? 1 : 0);
                    if (length > 0) finished.Add((tokens, c.Score / (tokens.Count + 1)));
                }
                else
                {
                    var hypothesis = new Hypothesis { Tokens = new List<int>(tokens) { c.Token }, Score = c.Score };
                    if (IsLooping(hypothesis.Tokens)) continue;
                    nextAlive.Add(hypothesis);
                    sourceBeams.Add(c.Beam);
                }
                if (nextAlive.Count == beams) break;
            }

            // Готово, когда лучшие завершённые варианты уже не догнать.
            if (finished.Count >= beams)
            {
                double worstFinished = finished.OrderByDescending(f => f.Score).Take(beams).Min(f => f.Score);
                double bestAlive = nextAlive.Count == 0 ? double.NegativeInfinity : nextAlive.Max(h => h.Score) / (step + 1);
                if (worstFinished >= bestAlive) break;
            }

            // Переставляем кэш по выбранным лучам (кэш кодировщика после первого шага переносим прежний).
            var nextPast = new Dictionary<string, DenseTensor<float>>();
            foreach (var (key, tensor) in past)
                nextPast[key] = Gather(presents.TryGetValue(key, out var present) ? present : tensor, sourceBeams);
            past = nextPast;
            alive = nextAlive;
        }

        foreach (var h in alive)
            finished.Add((h.Tokens, h.Score / Math.Max(1, h.Tokens.Count)));

        return finished.Count == 0 ? new List<int>() : finished.OrderByDescending(f => f.Score).First().Tokens;
    }

    private DenseTensor<float> Encode(List<int> inputIds, out DenseTensor<long> mask)
    {
        int length = inputIds.Count;
        var ids = new DenseTensor<long>(inputIds.Select(i => (long)i).ToArray(), new[] { 1, length });
        mask = new DenseTensor<long>(Enumerable.Repeat(1L, length).ToArray(), new[] { 1, length });
        using var encoded = _encoder.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", ids),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask)
        });
        return (DenseTensor<float>)encoded.First().AsTensor<float>().Clone();
    }

    private Dictionary<string, DenseTensor<float>> EmptyPast(int batch)
    {
        var past = new Dictionary<string, DenseTensor<float>>();
        for (int layer = 0; layer < _layers; layer++)
            foreach (var part in new[] { "decoder.key", "decoder.value", "encoder.key", "encoder.value" })
                past[$"past_key_values.{layer}.{part}"] = new DenseTensor<float>(new[] { batch, _heads, 0, _headDim });
        return past;
    }

    private NamedOnnxValue UseCacheInput(bool value) => _useCacheIsBool
        ? NamedOnnxValue.CreateFromTensor("use_cache_branch", new DenseTensor<bool>(new[] { value }, new[] { 1 }))
        : NamedOnnxValue.CreateFromTensor("use_cache_branch", new DenseTensor<long>(new[] { value ? 1L : 0L }, new[] { 1 }));

    /// <summary>Добавляет в candidates topK лучших токенов луча с логарифмом вероятности.</summary>
    private void LogSoftmaxTopK(ReadOnlySpan<float> logits, int topK, double beamScore, int beam,
        List<(int Beam, int Token, double Score)> candidates)
    {
        float max = float.NegativeInfinity;
        for (int i = 0; i < logits.Length; i++)
            if (i != _padId && logits[i] > max) max = logits[i];
        double sum = 0;
        for (int i = 0; i < logits.Length; i++)
            if (i != _padId) sum += Math.Exp(logits[i] - max);
        double logSum = Math.Log(sum) + max;

        var best = new List<(int Token, float Logit)>(topK + 1);
        for (int i = 0; i < logits.Length; i++)
        {
            if (i == _padId) continue;
            float v = logits[i];
            if (best.Count < topK || v > best[^1].Logit)
            {
                int pos = best.Count;
                while (pos > 0 && best[pos - 1].Logit < v) pos--;
                best.Insert(pos, (i, v));
                if (best.Count > topK) best.RemoveAt(best.Count - 1);
            }
        }
        foreach (var (token, logit) in best)
            candidates.Add((beam, token, beamScore + logit - logSum));
    }

    /// <summary>Выбирает строки тензора [batch, ...] по индексам лучей.</summary>
    private static DenseTensor<float> Gather(DenseTensor<float> source, List<int> rows)
    {
        var dims = source.Dimensions.ToArray();
        int rowSize = (int)(source.Length / dims[0]);
        dims[0] = rows.Count;
        var result = new float[rowSize * rows.Count];
        var span = source.Buffer.Span;
        for (int i = 0; i < rows.Count; i++)
            span.Slice(rows[i] * rowSize, rowSize).CopyTo(result.AsSpan(i * rowSize, rowSize));
        return new DenseTensor<float>(result, dims);
    }

    private static DenseTensor<float> Repeat(DenseTensor<float> source, int count) =>
        Gather(source, Enumerable.Repeat(0, count).ToList());

    private static DenseTensor<long> RepeatLong(DenseTensor<long> source, int count)
    {
        var dims = source.Dimensions.ToArray();
        int rowSize = (int)(source.Length / dims[0]);
        dims[0] = count;
        var result = new long[rowSize * count];
        for (int i = 0; i < count; i++)
            source.Buffer.Span.Slice(0, rowSize).CopyTo(result.AsSpan(i * rowSize, rowSize));
        return new DenseTensor<long>(result, dims);
    }

    /// <summary>Защита от зацикливания: один и тот же фрагмент повторился три раза подряд.</summary>
    private static bool IsLooping(List<int> tokens)
    {
        for (int length = 1; length <= 6; length++)
        {
            if (tokens.Count < length * 3) break;
            bool repeated = true;
            for (int i = 0; i < length && repeated; i++)
            {
                int a = tokens[^(i + 1)];
                if (tokens[^(i + 1 + length)] != a || tokens[^(i + 1 + 2 * length)] != a) repeated = false;
            }
            if (repeated) return true;
        }
        return false;
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
    }
}
