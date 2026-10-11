using System.Collections.Generic;

namespace ScreenTranslator.Services.Offline;

/// <summary>Токенизатор нейросетевой модели перевода: текст ↔ номера токенов.</summary>
public interface ISubwordTokenizer
{
    int EosId { get; }
    int PadId { get; }
    int UnkId { get; }

    /// <summary>Текст → номера токенов (без служебных токенов в начале и конце).</summary>
    List<int> Encode(string text);

    /// <summary>Номера токенов → текст (служебные токены пропускаются).</summary>
    string Decode(IEnumerable<int> ids);

    /// <summary>Номер служебного токена (например, кода языка «rus_Cyrl») или null.</summary>
    int? TokenId(string token);
}
