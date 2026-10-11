using System.Text.Json.Serialization;
using System.Windows.Input;

namespace ScreenTranslator.Models;

/// <summary>Где выполняется перевод.</summary>
public enum TranslationProviderKind
{
    /// <summary>Встроенный офлайн-переводчик (модели Opus-MT и NLLB-200 в приложении).</summary>
    Offline,
    LibreTranslate,
    /// <summary>«Перевод онлайн» — бесплатный адрес Google Переводчика, без ключа.</summary>
    Online,

    // Устаревшие значения: DeepL API, Yandex Cloud API и режим «только браузер» убраны из приложения.
    // Оставлены, чтобы старые settings.json читались; при загрузке заменяются на офлайн-перевод.
    DeepL,
    YandexCloud,
    BrowserOnly
}

/// <summary>Веб-переводчик, который открывается в браузере (кнопки «Google» и «Яндекс» в окне перевода).</summary>
public enum WebTranslatorKind
{
    Google,
    Yandex
}

/// <summary>Где открывается окно перевода.</summary>
public enum ResultPlacement
{
    /// <summary>Под выделенной областью (если места нет — над ней или сбоку).</summary>
    NearSelection,
    /// <summary>В зоне экрана, выбранной пользователем в настройках.</summary>
    CustomZone,
    /// <summary>Там, куда пользователь перетащил окно в прошлый раз.</summary>
    LastPosition,
    /// <summary>По центру монитора.</summary>
    ScreenCenter
}

/// <summary>Последняя выделенная область (для режима повтора).</summary>
public sealed class SavedRegion
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Имя устройства монитора, например \\.\DISPLAY1.</summary>
    public string MonitorDevice { get; set; } = "";
    public int MonitorX { get; set; }
    public int MonitorY { get; set; }
    public int MonitorWidth { get; set; }
    public int MonitorHeight { get; set; }
    public uint MonitorDpi { get; set; }

    [JsonIgnore]
    public PixelRect Rect => new(X, Y, Width, Height);

    [JsonIgnore]
    public PixelRect MonitorBounds => new(MonitorX, MonitorY, MonitorWidth, MonitorHeight);

    public static SavedRegion From(PixelRect rect, MonitorInfo monitor) => new()
    {
        X = rect.X,
        Y = rect.Y,
        Width = rect.Width,
        Height = rect.Height,
        MonitorDevice = monitor.DeviceName,
        MonitorX = monitor.Bounds.X,
        MonitorY = monitor.Bounds.Y,
        MonitorWidth = monitor.Bounds.Width,
        MonitorHeight = monitor.Bounds.Height,
        MonitorDpi = monitor.Dpi
    };
}

/// <summary>Все настройки приложения. Хранятся в %AppData%\ScreenTranslator\settings.json.</summary>
public sealed class AppSettings
{
    // Горячие клавиши
    public HotkeySetting SelectHotkey { get; set; } = new(ModifierKeys.Control | ModifierKeys.Shift, Key.T);
    public HotkeySetting RepeatHotkey { get; set; } = new(ModifierKeys.Control | ModifierKeys.Shift, Key.R);

    // Браузер: "default" — браузер по умолчанию, иначе имя ключа в StartMenuInternet
    public string BrowserId { get; set; } = BrowserInfo.DefaultId;
    public string BrowserName { get; set; } = "Браузер по умолчанию";
    public string BrowserPath { get; set; } = "";

    // Перевод в окне
    public TranslationProviderKind Provider { get; set; } = TranslationProviderKind.Offline;
    /// <summary>Офлайн-перевод: использовать NLLB-200 (качественнее, медленнее) вместо Opus-MT, если она установлена.</summary>
    public bool OfflinePreferQuality { get; set; }
    public string LibreUrl { get; set; } = "https://libretranslate.com";
    /// <summary>Ключ LibreTranslate, зашифрованный DPAPI (Base64).</summary>
    public string? LibreKeyProtected { get; set; }
    /// <summary>Ключ OpenRouter, зашифрованный DPAPI (Base64).</summary>
    public string? OpenRouterKeyProtected { get; set; }

    // Языки
    /// <summary>Родной язык пользователя (код из LanguageCatalog).</summary>
    public string NativeLanguage { get; set; } = "ru";
    /// <summary>"auto" или тег языка OCR, например "en-US".</summary>
    public string OcrLanguage { get; set; } = "auto";

    // Поведение
    public bool CloseOnDeactivate { get; set; } = true;
    public bool AutoCopyReply { get; set; } = true;
    public bool AutoStart { get; set; }
    public bool HistoryEnabled { get; set; } = true;
    public bool FirstRunCompleted { get; set; }

    /// <summary>Пояснение к кнопке «DeepSeek» (нужен вход в аккаунт) уже показано.</summary>
    public bool DeepSeekNoticeShown { get; set; }

    // Режим повтора
    public SavedRegion? LastRegion { get; set; }

    // Размещение окна перевода
    public ResultPlacement ResultPlacement { get; set; } = ResultPlacement.NearSelection;
    /// <summary>Зона экрана для режима CustomZone.</summary>
    public SavedRegion? ResultZone { get; set; }
    /// <summary>Левый верхний угол окна после последнего перетаскивания (для режима LastPosition).</summary>
    public SavedRegion? LastResultPosition { get; set; }
    /// <summary>Ширина окна перевода, заданная пользователем (DIP); null — по умолчанию.</summary>
    public double? ResultWidth { get; set; }
    /// <summary>Высота окна перевода, заданная пользователем (DIP); null — по содержимому.</summary>
    public double? ResultHeight { get; set; }
}
