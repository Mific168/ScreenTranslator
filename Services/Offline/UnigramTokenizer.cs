using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ScreenTranslator.Services.Offline;

/// <summary>
/// Токенизатор SentencePiece (модель Unigram) для моделей Marian / Opus-MT.
/// Читает tokenizer.json в формате Hugging Face: номер части слова в словаре
/// совпадает с номером токена модели.
/// </summary>
public sealed class UnigramTokenizer : ISubwordTokenizer
{
    private const char Metaspace = '▁'; // «▁» — признак начала слова
    private const double UnknownPenalty = 10.0;

    private readonly string[] _pieces;
    private readonly Dictionary<string, (int Id, double Score)> _lookup;
    private readonly int _maxPieceLength;
    private readonly double _unknownScore;

    public int EosId { get; }
    public int UnkId { get; }
    public int PadId { get; }
    public int VocabSize => _pieces.Length;

    private UnigramTokenizer(string[] pieces, double[] scores, int eosId, int unkId, int padId)
    {
        _pieces = pieces;
        EosId = eosId;
        UnkId = unkId;
        PadId = padId;

        _lookup = new Dictionary<string, (int, double)>(pieces.Length, StringComparer.Ordinal);
        double minScore = 0;
        for (int i = 0; i < pieces.Length; i++)
        {
            if (i == eosId || i == unkId || i == padId || string.IsNullOrEmpty(pieces[i])) continue;
            _lookup.TryAdd(pieces[i], (i, scores[i]));
            if (pieces[i].Length > _maxPieceLength) _maxPieceLength = pieces[i].Length;
            if (scores[i] < minScore) minScore = scores[i];
        }
        _unknownScore = minScore - UnknownPenalty;
    }

    public static UnigramTokenizer Load(string tokenizerJsonPath)
    {
        using var stream = File.OpenRead(tokenizerJsonPath);
        using var doc = JsonDocument.Parse(stream);
        var vocab = doc.RootElement.GetProperty("model").GetProperty("vocab");

        int count = vocab.GetArrayLength();
        var pieces = new string[count];
        var scores = new double[count];
        int i = 0;
        foreach (var item in vocab.EnumerateArray())
        {
            pieces[i] = item[0].GetString() ?? "";
            scores[i] = item[1].GetDouble();
            i++;
        }

        int Find(string token, int fallback)
        {
            int index = Array.IndexOf(pieces, token);
            return index >= 0 ? index : fallback;
        }

        return new UnigramTokenizer(pieces, scores, Find("</s>", 0), Find("<unk>", 1), Find("<pad>", count - 1));
    }

    /// <summary>Текст → номера токенов (без завершающего &lt;/s&gt;).</summary>
    public List<int> Encode(string text)
    {
        var ids = new List<int>();
        text = text.Normalize(NormalizationForm.FormKC);
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            EncodeWord(Metaspace + word, ids);
        return ids;
    }

    /// <summary>Алгоритм Витерби: разбиение слова на части с максимальной суммарной вероятностью.</summary>
    private void EncodeWord(string word, List<int> output)
    {
        int n = word.Length;
        var best = new double[n + 1];
        var prevPos = new int[n + 1];
        var prevId = new int[n + 1];
        for (int i = 1; i <= n; i++) best[i] = double.NegativeInfinity;

        for (int start = 0; start < n; start++)
        {
            if (double.IsNegativeInfinity(best[start])) continue;
            bool singleCharFound = false;
            int maxEnd = Math.Min(n, start + _maxPieceLength);

            for (int end = start + 1; end <= maxEnd; end++)
            {
                if (_lookup.TryGetValue(word.Substring(start, end - start), out var piece))
                {
                    if (end == start + 1) singleCharFound = true;
                    double score = best[start] + piece.Score;
                    if (score > best[end])
                    {
                        best[end] = score;
                        prevPos[end] = start;
                        prevId[end] = piece.Id;
                    }
                }
            }

            if (!singleCharFound)
            {
                // Символа нет в словаре — заменяем его на <unk> (с учётом суррогатных пар).
                int step = char.IsHighSurrogate(word[start]) && start + 1 < n ? 2 : 1;
                double score = best[start] + _unknownScore;
                if (score > best[start + step])
                {
                    best[start + step] = score;
                    prevPos[start + step] = start;
                    prevId[start + step] = UnkId;
                }
            }
        }

        var reversed = new List<int>();
        for (int pos = n; pos > 0; pos = prevPos[pos])
            reversed.Add(prevId[pos]);
        reversed.Reverse();

        // Подряд идущие <unk> объединяем в один.
        foreach (var id in reversed)
        {
            if (id == UnkId && output.Count > 0 && output[^1] == UnkId) continue;
            output.Add(id);
        }
    }

    public int? TokenId(string token) => _lookup.TryGetValue(token, out var piece) ? piece.Id : null;

    /// <summary>Номера токенов → текст.</summary>
    public string Decode(IEnumerable<int> ids)
    {
        var sb = new StringBuilder();
        foreach (var id in ids)
        {
            if (id == EosId || id == PadId || id == UnkId || id < 0 || id >= _pieces.Length) continue;
            sb.Append(_pieces[id]);
        }
        var text = sb.Replace(Metaspace, ' ').ToString().Trim();
        return CleanUpSpaces(text);
    }

    /// <summary>Убирает пробелы перед знаками препинания (как clean_up_tokenization_spaces).</summary>
    private static string CleanUpSpaces(string text) => text
        .Replace(" .", ".").Replace(" ,", ",").Replace(" !", "!").Replace(" ?", "?")
        .Replace(" n't", "n't").Replace(" 'm", "'m").Replace(" 's", "'s").Replace(" 've", "'ve").Replace(" 're", "'re");
}
