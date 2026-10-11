using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ScreenTranslator.Services;

/// <summary>
/// Простой офлайн-детектор языка: по системе письма, характерным буквам и частым словам.
/// Используется, когда провайдер перевода не определил язык (офлайн-перевод, кнопки Google/Яндекс,
/// ошибка сети) и для выбора лучшего результата OCR.
/// </summary>
public static partial class LanguageDetector
{
    /// <param name="Code">Код языка (LanguageCatalog) или null.</param>
    /// <param name="Confidence">Уверенность 0..1.</param>
    /// <param name="Purity">Доля букв основной системы письма 0..1 (низкая — «мусор» OCR).</param>
    public readonly record struct Detection(string? Code, double Confidence, double Purity);

    private enum Script { Latin, Cyrillic, Greek, Arabic, Hebrew, Han, Kana, Hangul, Thai, Devanagari, Other }

    private sealed record LatinProfile(string Code, HashSet<string> Words, string StrongChars, string WeakChars);

    private static LatinProfile P(string code, string words, string strong = "", string weak = "") =>
        new(code, new HashSet<string>(words.Split(' ', StringSplitOptions.RemoveEmptyEntries)), strong, weak);

    private static readonly LatinProfile[] LatinProfiles =
    {
        P("en", "the and of to is in that it for you with on are this be was have not your from at or can will an by we all if my do what please click are there their which"),
        P("de", "der die und das ist nicht ich sie es ein eine zu den mit von auf für dem sich auch wir sind oder bitte wird werden kann", "ß", "äöü"),
        P("fr", "le la les et est des une un du que pour pas dans vous je il sur avec ce qui au sont nous ne cette mais", "œç", "èêàëùûîô"),
        P("es", "el la los las y de que es en un una por para con no del se lo como más pero su al está gracias muy también", "ñ¿¡", "áéíóú"),
        P("it", "il la di che è e un una per non sono del della con si mi ho gli le questo anche ma più nel", "", "àèìòù"),
        P("pt", "o a os as de que é não um uma para com do da em no na se por mais você obrigado isso são está", "ãõ", "çáéêó"),
        P("nl", "de het een en van is dat niet ik je op te met voor zijn er maar ook wat deze wordt", "ĳ", ""),
        P("pl", "i w nie na się z do to że jest jak co ale po tak dla czy jestem mnie są", "ąęłńśźż", "ćó"),
        P("cs", "a je se na v že to ne jsem s z do jak ale pro by tak co už jsou", "ěřů", "čšžýáí"),
        P("sk", "a je sa na v že to nie som s z do ako ale pre by tak čo už sú", "ľĺŕô", "čšžýä"),
        P("tr", "ve bir bu da de için ile ne çok ben sen var değil mi gibi daha olarak", "ğış", "çöü"),
        P("sv", "och att det är som en på i för med inte jag du har av den till vi", "", "åäö"),
        P("da", "og at det er en på i for med ikke jeg du har af den til vi som", "æø", "å"),
        P("nb", "og å det er en på i for med ikke jeg du har av den til vi som", "æø", "å"),
        P("fi", "ja on ei se että oli hän mutta kun niin minä sinä olen tämä kanssa", "", "äö"),
        P("ro", "și de la în nu cu o un este ce pe să care mai pentru sunt", "șțăȘȚ", "âî"),
        P("hu", "a az és hogy nem egy is van meg de már csak ez el mint", "őű", "áéóöü"),
        P("id", "yang dan di ini itu dengan untuk tidak ada saya dari akan ke kamu"),
        P("vi", "và của là có không được tôi bạn cho này những một", "ơưđạảấầẩậắằặẹẻẽếềểễệịọỏốồổỗộớờởỡợụủứừửữựỳỵỷỹ", "ăâêô"),
        P("lv", "un ir ar uz par kas no lai bet arī", "āēīūģķļņ", "šž"),
        P("lt", "ir yra su į kad ne tai bet už iš", "ąčęėįųū", "šž"),
        P("et", "ja on ei et see oli kui aga mis ka", "õ", "äöü"),
    };

    private static readonly HashSet<char> TraditionalOnly = new("們個這說來時會對國學與為無實點還後開關長見頭問體經動電話東車書語讀錢門間聽讓給從現");
    private static readonly HashSet<char> SimplifiedOnly = new("们个这说来时会对国学与为无实点还后开关长见头问体经动电话东车书语读钱门间听让给从现");

    /// <summary>Слово из букв целиком: буквы, прилипшие к цифрам («216e», «32h»), словом не считаются.</summary>
    [GeneratedRegex(@"(?<![\p{L}\p{N}])\p{L}+(?![\p{L}\p{N}])")]
    private static partial Regex WordRegex();

    /// <summary>Основная система письма текста ("Latin", "Cyrillic", "Han" …) или null.</summary>
    public static string? DominantScript(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var counts = new int[(int)Script.Other + 1];
        foreach (char c in text)
            if (char.IsLetter(c)) counts[(int)GetScript(c)]++;
        if (counts[(int)Script.Kana] > 0) counts[(int)Script.Han] += counts[(int)Script.Kana];

        int best = -1;
        for (int i = 0; i < (int)Script.Other; i++)
            if (i != (int)Script.Kana && counts[i] > 0 && (best < 0 || counts[i] > counts[best])) best = i;
        return best < 0 ? null : ((Script)best).ToString();
    }

    /// <summary>Система письма, которой обычно пишут на языке (для выбора движка OCR).</summary>
    public static string ScriptOfLanguage(string? code) => code switch
    {
        "ru" or "uk" or "be" or "bg" or "kk" or "sr" or "mk" => nameof(Script.Cyrillic),
        "zh" or "zh-TW" or "ja" => nameof(Script.Han),
        "ko" => nameof(Script.Hangul),
        "el" => nameof(Script.Greek),
        "he" => nameof(Script.Hebrew),
        "ar" => nameof(Script.Arabic),
        "th" => nameof(Script.Thai),
        "hi" => nameof(Script.Devanagari),
        _ => nameof(Script.Latin)
    };

    public static Detection Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;

        var counts = new int[(int)Script.Other + 1];
        int total = 0;
        foreach (char c in text)
        {
            if (!char.IsLetter(c)) continue;
            counts[(int)GetScript(c)]++;
            total++;
        }
        if (total == 0) return default;

        int kana = counts[(int)Script.Kana];
        int han = counts[(int)Script.Han];
        if (kana > 0 && kana + han >= total * 0.3)
            return new Detection("ja", 0.95, (kana + han) / (double)total);

        var dominant = Script.Latin;
        for (int i = 0; i < (int)Script.Other; i++)
            if (counts[i] > counts[(int)dominant]) dominant = (Script)i;

        double purity = counts[(int)dominant] / (double)total;

        return dominant switch
        {
            Script.Han => DetectChinese(text, purity),
            Script.Hangul => new Detection("ko", 0.95, purity),
            Script.Greek => new Detection("el", 0.95, purity),
            Script.Hebrew => new Detection("he", 0.95, purity),
            Script.Arabic => new Detection("ar", 0.8, purity),
            Script.Thai => new Detection("th", 0.95, purity),
            Script.Devanagari => new Detection("hi", 0.8, purity),
            Script.Cyrillic => DetectCyrillic(text, purity),
            _ => DetectLatin(text, purity)
        };
    }

    private static Script GetScript(char c) => c switch
    {
        >= 'A' and <= 'Z' or >= 'a' and <= 'z' => Script.Latin,
        >= 'À' and <= 'ɏ' or >= 'Ḁ' and <= 'ỿ' => Script.Latin,
        >= 'Ѐ' and <= 'ӿ' => Script.Cyrillic,
        >= 'Ͱ' and <= 'Ͽ' => Script.Greek,
        >= '֐' and <= '׿' => Script.Hebrew,
        >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ' => Script.Arabic,
        >= 'ऀ' and <= 'ॿ' => Script.Devanagari,
        >= '฀' and <= '๿' => Script.Thai,
        >= '぀' and <= 'ヿ' => Script.Kana,
        >= '一' and <= '鿿' or >= '㐀' and <= '䶿' => Script.Han,
        >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ' => Script.Hangul,
        _ => Script.Other
    };

    private static Detection DetectChinese(string text, double purity)
    {
        int trad = text.Count(TraditionalOnly.Contains);
        int simp = text.Count(SimplifiedOnly.Contains);
        return new Detection(trad > simp ? "zh-TW" : "zh", 0.9, purity);
    }

    private static Detection DetectCyrillic(string text, double purity)
    {
        var lower = text.ToLowerInvariant();
        if (lower.IndexOfAny("әғқңөұүһ".ToCharArray()) >= 0) return new Detection("kk", 0.9, purity);
        if (lower.IndexOfAny("іїєґ".ToCharArray()) >= 0)
            return lower.Contains('ў') ? new Detection("be", 0.9, purity) : new Detection("uk", 0.9, purity);
        if (lower.Contains('ў')) return new Detection("be", 0.9, purity);

        bool hasRussianOnly = lower.IndexOfAny("ыэё".ToCharArray()) >= 0;
        int hardSigns = lower.Count(c => c == 'ъ');
        if (!hasRussianOnly && hardSigns >= 2) return new Detection("bg", 0.6, purity);

        return new Detection("ru", 0.85, purity);
    }

    private static Detection DetectLatin(string text, double purity)
    {
        var lower = text.ToLowerInvariant();
        var words = WordRegex().Matches(lower).Select(m => m.Value).ToList();
        if (words.Count == 0) return new Detection("en", 0.2, purity);

        var scores = new List<(string Code, double Score)>();
        foreach (var profile in LatinProfiles)
        {
            // Однобуквенные слова («i», «e», «a») есть во многих языках — это слабый признак.
            double score = words.Where(profile.Words.Contains).Sum(w => w.Length == 1 ? 0.5 : 1.0);
            foreach (char c in lower)
            {
                if (profile.StrongChars.Contains(c)) score += 2;
                else if (profile.WeakChars.Contains(c)) score += 0.5;
            }
            scores.Add((profile.Code, score));
        }

        var ordered = scores.OrderByDescending(s => s.Score).ToList();
        var best = ordered[0];
        var second = ordered.Count > 1 ? ordered[1].Score : 0;

        if (best.Score < 1) return new Detection("en", 0.2, purity);

        double confidence = best.Score >= 2
            ? Math.Min(1.0, 0.4 + (best.Score - second) / best.Score)
            : 0.35;
        return new Detection(best.Code, confidence, purity);
    }
}
