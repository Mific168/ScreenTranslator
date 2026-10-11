using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ScreenTranslator.Services.Offline;

/// <summary>
/// Токенизатор SentencePiece BPE в формате Hugging Face tokenizer.json (модели NLLB-200, M2M100):
/// словарь «часть слова → номер» и упорядоченный список слияний.
/// </summary>
public sealed class BpeTokenizer : ISubwordTokenizer
{
    private const char Metaspace = '▁';

    private readonly Dictionary<string, int> _vocab;
    private readonly string[] _pieces;
    private readonly Dictionary<(string, string), int> _mergeRanks;
    private readonly Dictionary<string, int> _specialTokens;
    private readonly HashSet<int> _specialIds;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<int>> _cache = new(StringComparer.Ordinal);

    public int EosId { get; }
    public int PadId { get; }
    public int UnkId { get; }

    private BpeTokenizer(Dictionary<string, int> vocab, Dictionary<(string, string), int> mergeRanks, Dictionary<string, int> special)
    {
        _vocab = vocab;
        _mergeRanks = mergeRanks;
        _specialTokens = special;

        int max = 0;
        foreach (var id in vocab.Values) max = Math.Max(max, id);
        foreach (var id in special.Values) max = Math.Max(max, id);
        _pieces = new string[max + 1];
        foreach (var (piece, id) in vocab) _pieces[id] = piece;
        foreach (var (piece, id) in special) _pieces[id] = piece;

        _specialIds = new HashSet<int>(special.Values);
        EosId = TokenId("</s>") ?? 2;
        PadId = TokenId("<pad>") ?? 1;
        UnkId = TokenId("<unk>") ?? 3;
    }

    public static BpeTokenizer Load(string tokenizerJsonPath)
    {
        using var stream = File.OpenRead(tokenizerJsonPath);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var model = root.GetProperty("model");

        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in model.GetProperty("vocab").EnumerateObject())
            vocab[item.Name] = item.Value.GetInt32();

        var merges = new Dictionary<(string, string), int>();
        int rank = 0;
        foreach (var item in model.GetProperty("merges").EnumerateArray())
        {
            string? left, right;
            if (item.ValueKind == JsonValueKind.Array)
            {
                left = item[0].GetString();
                right = item[1].GetString();
            }
            else
            {
                var text = item.GetString() ?? "";
                int space = text.IndexOf(' ', 1);
                if (space <= 0) continue;
                left = text[..space];
                right = text[(space + 1)..];
            }
            if (left != null && right != null) merges.TryAdd((left, right), rank++);
        }

        var special = new Dictionary<string, int>(StringComparer.Ordinal);
        if (root.TryGetProperty("added_tokens", out var added))
            foreach (var token in added.EnumerateArray())
                special[token.GetProperty("content").GetString() ?? ""] = token.GetProperty("id").GetInt32();

        return new BpeTokenizer(vocab, merges, special);
    }

    public int? TokenId(string token) =>
        _specialTokens.TryGetValue(token, out var id) ? id : _vocab.TryGetValue(token, out id) ? id : null;

    public List<int> Encode(string text)
    {
        var ids = new List<int>();
        text = text.Normalize(NormalizationForm.FormKC);
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var key = Metaspace + word;
            if (!_cache.TryGetValue(key, out var wordIds))
            {
                wordIds = EncodeWord(key);
                if (_cache.Count < 50_000) _cache.TryAdd(key, wordIds);
            }
            ids.AddRange(wordIds);
        }
        return ids;
    }

    /// <summary>BPE: начинаем с отдельных символов и сливаем пары по порядку из списка слияний.</summary>
    private List<int> EncodeWord(string word)
    {
        var symbols = new List<string>();
        for (int i = 0; i < word.Length; i++)
        {
            if (char.IsHighSurrogate(word[i]) && i + 1 < word.Length)
            {
                symbols.Add(word.Substring(i, 2));
                i++;
            }
            else
            {
                symbols.Add(word[i].ToString());
            }
        }

        while (symbols.Count > 1)
        {
            int bestRank = int.MaxValue, bestIndex = -1;
            for (int i = 0; i < symbols.Count - 1; i++)
            {
                if (_mergeRanks.TryGetValue((symbols[i], symbols[i + 1]), out var r) && r < bestRank)
                {
                    bestRank = r;
                    bestIndex = i;
                }
            }
            if (bestIndex < 0) break;

            var merged = symbols[bestIndex] + symbols[bestIndex + 1];
            var left = symbols[bestIndex];
            var right = symbols[bestIndex + 1];
            // Сливаем все вхождения этой пары.
            var next = new List<string>(symbols.Count);
            for (int i = 0; i < symbols.Count; i++)
            {
                if (i < symbols.Count - 1 && symbols[i] == left && symbols[i + 1] == right)
                {
                    next.Add(merged);
                    i++;
                }
                else
                {
                    next.Add(symbols[i]);
                }
            }
            symbols = next;
        }

        var ids = new List<int>(symbols.Count);
        foreach (var symbol in symbols)
        {
            if (_vocab.TryGetValue(symbol, out var id)) ids.Add(id);
            else if (ids.Count == 0 || ids[^1] != UnkId) ids.Add(UnkId); // fuse_unk
        }
        return ids;
    }

    public string Decode(IEnumerable<int> ids)
    {
        var sb = new StringBuilder();
        foreach (var id in ids)
        {
            if (id < 0 || id >= _pieces.Length || _specialIds.Contains(id) || _pieces[id] == null) continue;
            sb.Append(_pieces[id]);
        }
        return sb.Replace(Metaspace, ' ').ToString().Trim();
    }
}
