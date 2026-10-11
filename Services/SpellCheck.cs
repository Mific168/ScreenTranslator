using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ScreenTranslator.Services;

/// <summary>
/// Встроенная в Windows проверка орфографии (Spell Checking API, офлайн). Используется после OCR,
/// чтобы отличить настоящее слово от ошибки распознавания («convnand» → «command»).
/// Если словаря для языка нет, проверка просто не выполняется.
/// </summary>
public static class SpellCheck
{
    private static readonly object Sync = new();
    private static readonly ConcurrentDictionary<string, ISpellChecker?> Checkers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<(string, string), bool> Cache = new();
    private static ISpellCheckerFactory? _factory;
    private static bool _factoryFailed;

    /// <summary>Есть ли словарь для языка (тег вида "en-US").</summary>
    public static bool IsAvailable(string languageTag) => GetChecker(languageTag) != null;

    /// <summary>
    /// true — слово есть в словаре; null — словаря для языка нет. Вызывать из фонового потока (MTA).
    /// </summary>
    public static bool? IsWord(string word, string languageTag)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;
        var checker = GetChecker(languageTag);
        if (checker == null) return null;
        if (Cache.TryGetValue((languageTag, word), out var cached)) return cached;

        bool valid;
        lock (Sync)
        {
            try
            {
                if (checker.Check(word, out var errors) != 0 || errors == null) return null;
                try
                {
                    // S_OK и ошибка — слово неизвестно; S_FALSE — ошибок нет.
                    valid = errors.Next(out var error) != 0 || error == IntPtr.Zero;
                    if (error != IntPtr.Zero) Marshal.Release(error);
                }
                finally
                {
                    Marshal.ReleaseComObject(errors);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                return null;
            }
        }
        Cache[(languageTag, word)] = valid;
        return valid;
    }

    /// <summary>Варианты исправления слова из словаря Windows (как в подсказках Word). Вызывать из фонового потока.</summary>
    public static IReadOnlyList<string> Suggest(string word, string languageTag, int max = 5)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(word)) return result;
        var checker = GetChecker(languageTag);
        if (checker == null) return result;

        lock (Sync)
        {
            try
            {
                if (checker.Suggest(word, out var suggestions) != 0 || suggestions == null) return result;
                try
                {
                    var buffer = new string[1];
                    while (result.Count < max && suggestions.Next(1, buffer, IntPtr.Zero) == 0)
                        result.Add(buffer[0]);
                }
                finally
                {
                    Marshal.ReleaseComObject(suggestions);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // подсказок нет — не страшно
            }
        }
        return result;
    }

    private static ISpellChecker? GetChecker(string languageTag)
    {
        if (Checkers.TryGetValue(languageTag, out var existing)) return existing;
        lock (Sync)
        {
            if (Checkers.TryGetValue(languageTag, out existing)) return existing;
            ISpellChecker? checker = null;
            try
            {
                if (!_factoryFailed)
                {
                    _factory ??= (ISpellCheckerFactory)new SpellCheckerFactoryClass();
                    if (_factory.IsSupported(languageTag, out int supported) == 0 && supported != 0 &&
                        _factory.CreateSpellChecker(languageTag, out var created) == 0)
                        checker = created;
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                _factoryFailed = true;
                Log.Error("Проверка орфографии Windows недоступна", ex);
            }
            Checkers[languageTag] = checker;
            return checker;
        }
    }

    // ---------- COM-интерфейсы (spellcheck.h) ----------

    [ComImport, Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")]
    private class SpellCheckerFactoryClass
    {
    }

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        [PreserveSig] int get_SupportedLanguages(out IntPtr value);
        [PreserveSig] int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out int value);
        [PreserveSig] int CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out ISpellChecker value);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        [PreserveSig] int get_LanguageTag(out IntPtr value);
        [PreserveSig] int Check([MarshalAs(UnmanagedType.LPWStr)] string text, out IEnumSpellingError value);
        [PreserveSig] int Suggest([MarshalAs(UnmanagedType.LPWStr)] string word, out IEnumString value);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        [PreserveSig] int Next(out IntPtr value);
    }
}
