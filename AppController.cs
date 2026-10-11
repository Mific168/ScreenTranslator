using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenTranslator.Models;
using ScreenTranslator.Native;
using ScreenTranslator.Services;
using ScreenTranslator.Services.Offline;
using ScreenTranslator.Services.Translation;
using ScreenTranslator.Views;

namespace ScreenTranslator;

/// <summary>
/// Центральный объект приложения: значок в трее, горячие клавиши и основные сценарии
/// (выделение области, повтор, история, настройки).
/// </summary>
public sealed class AppController : IDisposable
{
    private const int HotkeySelectId = 1;
    private const int HotkeyRepeatId = 2;

    private static AppController? _instance;
    public static AppController Instance => _instance ??= new AppController();

    public OcrService Ocr { get; } = new();
    public OfflineModelManager OfflineModels { get; } = new();
    public TranslationService Translation { get; }
    public HistoryService History { get; } = new();

    private AppSettings Settings => SettingsService.Current;

    private MessageWindow? _messageWindow;
    private HotkeyService? _hotkeys;
    private TrayIcon? _tray;
    private MenuItem? _selectMenuItem;
    private MenuItem? _repeatMenuItem;
    private ResultWindow? _result;
    private SettingsWindow? _settingsWindow;
    private HistoryWindow? _historyWindow;
    private DispatcherTimer? _trimTimer;
    private bool _busy;

    private AppController()
    {
        Translation = new TranslationService(OfflineModels);
    }

    /// <param name="silent">Запуск при входе в Windows — без приветственного уведомления.</param>
    /// <param name="installModels">Модели, выбранные в установщике (например, "en-ru", "ru-en").</param>
    /// <param name="nativeLanguage">Родной язык, выбранный в установщике.</param>
    public void Start(bool silent, IReadOnlyList<string>? installModels = null, string? nativeLanguage = null)
    {
        _messageWindow = new MessageWindow();
        _messageWindow.AddHook(WndProc);

        _hotkeys = new HotkeyService(_messageWindow);
        _hotkeys.Pressed += id =>
        {
            // Выходим из оконной процедуры, прежде чем показывать окна.
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (id == HotkeySelectId) StartSelection();
                else if (id == HotkeyRepeatId) RepeatLastRegion();
            });
        };

        _tray = new TrayIcon(_messageWindow, BuildTrayMenu(), "Экранный переводчик");
        _tray.DoubleClick += StartSelection;

        var failed = RegisterHotkeys();
        UpdateTrayTexts();

        // Автозапуск может включить установщик — синхронизируем флаг в настройках с реестром.
        bool autoStartInRegistry = AutoStartService.IsEnabled();
        if (Settings.AutoStart != autoStartInRegistry)
        {
            Settings.AutoStart = autoStartInRegistry;
            SettingsService.Save();
        }
        if (Settings.AutoStart) AutoStartService.Apply(true); // обновляем путь, если exe перенесли

        if (installModels != null)
        {
            RunInstallerSetup(installModels, nativeLanguage);
        }
        else if (!Settings.FirstRunCompleted)
        {
            ShowSettings(firstRun: true);
        }
        else if (failed.Count > 0)
        {
            ReportHotkeyFailure(failed);
        }
        else if (!silent)
        {
            _tray.ShowBalloon("Экранный переводчик работает",
                $"Нажмите {Settings.SelectHotkey}, чтобы перевести область экрана. Значок — в области уведомлений.");
        }

        ScheduleTrim();
    }

    /// <summary>
    /// Первый запуск после установщика: включает офлайн-перевод, применяет выбранный родной язык
    /// и скачивает выбранные языковые модели с окном прогресса.
    /// </summary>
    private void RunInstallerSetup(IReadOnlyList<string> modelIds, string? nativeLanguage)
    {
        if (LanguageCatalog.Get(nativeLanguage) != null) Settings.NativeLanguage = nativeLanguage!;
        Settings.Provider = TranslationProviderKind.Offline;
        Settings.FirstRunCompleted = true;
        SettingsService.Save();

        var models = modelIds.Select(OfflineModelCatalog.FindById).Where(m => m != null).Select(m => m!).ToList();
        if (models.Any(m => m.IsMultilingual))
        {
            // В установщике выбрали качественную модель — переводим ею.
            Settings.OfflinePreferQuality = true;
            SettingsService.Save();
        }
        if (models.Count == 0 || models.All(m => m.IsInstalled))
        {
            _tray?.ShowBalloon("Экранный переводчик установлен",
                $"Нажмите {Settings.SelectHotkey} и выделите текст на экране. Значок — в области уведомлений.");
            return;
        }

        var window = new ModelSetupWindow(models);
        window.Show();
        window.Activate();
    }

    /// <summary>Итог фоновой загрузки моделей, когда окно прогресса было скрыто.</summary>
    public void NotifyModelSetupFinished(bool success)
    {
        if (success)
            _tray?.ShowBalloon("Офлайн-переводчик готов",
                $"Языковые модели установлены. Нажмите {Settings.SelectHotkey} и выделите текст — перевод работает без интернета.");
        else
            _tray?.ShowBalloon("Не все модели скачаны",
                "Проверьте подключение к интернету и установите модели в настройках: «Перевод в окне».", isError: true);
    }

    // =====================================================================
    //  Трей и горячие клавиши
    // =====================================================================

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();

        _selectMenuItem = new MenuItem { Header = "Перевести область экрана", FontWeight = FontWeights.SemiBold };
        _selectMenuItem.Click += (_, _) => StartSelection();

        _repeatMenuItem = new MenuItem { Header = "Повторить последнюю область" };
        _repeatMenuItem.Click += (_, _) => RepeatLastRegion();

        var history = new MenuItem { Header = "История переводов" };
        history.Click += (_, _) => ShowHistory();

        var settings = new MenuItem { Header = "Настройки" };
        settings.Click += (_, _) => ShowSettings();

        var exit = new MenuItem { Header = "Выход" };
        exit.Click += (_, _) => Exit();

        menu.Items.Add(_selectMenuItem);
        menu.Items.Add(_repeatMenuItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(history);
        menu.Items.Add(settings);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        return menu;
    }

    private void UpdateTrayTexts()
    {
        if (_selectMenuItem != null) _selectMenuItem.InputGestureText = Settings.SelectHotkey.ToString();
        if (_repeatMenuItem != null) _repeatMenuItem.InputGestureText = Settings.RepeatHotkey.ToString();
        _tray?.SetTooltip($"Экранный переводчик — {Settings.SelectHotkey}");
    }

    /// <returns>Список сочетаний, которые не удалось зарегистрировать.</returns>
    private List<string> RegisterHotkeys()
    {
        var failed = new List<string>();
        if (_hotkeys == null) return failed;
        if (!_hotkeys.Register(HotkeySelectId, Settings.SelectHotkey)) failed.Add(Settings.SelectHotkey.ToString());
        if (!_hotkeys.Register(HotkeyRepeatId, Settings.RepeatHotkey)) failed.Add(Settings.RepeatHotkey.ToString());
        return failed;
    }

    private void ReportHotkeyFailure(List<string> failed)
    {
        _tray?.ShowBalloon("Горячая клавиша занята",
            $"Не удалось назначить {string.Join(" и ", failed)}: сочетание занято другой программой. " +
            "Выберите другое в настройках. Перевод доступен и через меню значка.", isError: true);
    }

    /// <summary>Временно отключает глобальные сочетания (пока пользователь вводит новое в настройках).</summary>
    public void SuspendHotkeys() => _hotkeys?.UnregisterAll();

    public void ResumeHotkeys() => RegisterHotkeys();

    /// <summary>Применяет изменённые настройки.</summary>
    public void ApplySettings()
    {
        var failed = RegisterHotkeys();
        UpdateTrayTexts();
        if (failed.Count > 0) ReportHotkeyFailure(failed);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_SETTINGCHANGE && lParam != IntPtr.Zero)
        {
            var area = System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam);
            if (area == "ImmersiveColorSet")
                Application.Current.Dispatcher.BeginInvoke(ThemeService.Refresh);
        }
        return IntPtr.Zero;
    }

    // =====================================================================
    //  Сценарии
    // =====================================================================

    /// <summary>Выделение области мышью и перевод.</summary>
    public async void StartSelection()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await CloseResultBeforeCaptureAsync();

            CapturedScreen? capture = ScreenService.CaptureVirtualScreen();
            var region = await RegionSelector.SelectAsync(capture);
            if (region is not { } rect || rect.IsEmpty)
            {
                capture = null;
                ScheduleTrim();
                return;
            }

            var image = capture.Crop(rect);
            var monitor = ScreenService.MonitorFor(rect, capture.Monitors);
            capture = null; // большой снимок больше не нужен

            Settings.LastRegion = SavedRegion.From(rect, monitor);
            SettingsService.Save();

            ShowResultForCapture(image, rect);
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка захвата экрана", ex);
            Dialogs.Error(null, "Не удалось захватить экран: " + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Повторный захват той же области без выделения.</summary>
    public async void RepeatLastRegion()
    {
        if (_busy) return;

        var saved = Settings.LastRegion;
        if (saved == null)
        {
            if (Dialogs.Confirm(null, "Область для повтора ещё не выбиралась.\n\nВыделить область сейчас?"))
                StartSelection();
            return;
        }

        var monitors = ScreenService.GetMonitors();
        var rect = ScreenService.ResolveSavedRegion(saved, monitors);
        if (rect == null)
        {
            if (Dialogs.Confirm(null,
                    "Последняя область находится за пределами экрана — похоже, изменилась конфигурация мониторов " +
                    "(отключён монитор, сменилось разрешение или расположение).\n\nВыделить область заново?"))
                StartSelection();
            return;
        }

        _busy = true;
        try
        {
            await CloseResultBeforeCaptureAsync();
            var image = ScreenService.CaptureRegion(rect.Value);
            ShowResultForCapture(image, rect.Value);
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка повторного захвата", ex);
            Dialogs.Error(null, "Не удалось захватить область: " + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Выбор мышью зоны экрана, в которой будет открываться окно перевода.</summary>
    public async Task<SavedRegion?> PickResultZoneAsync()
    {
        if (_busy) return null;
        _busy = true;
        try
        {
            await CloseResultBeforeCaptureAsync();
            var capture = ScreenService.CaptureVirtualScreen();
            var rect = await RegionSelector.SelectAsync(capture,
                "Выделите зону, в которой будет открываться окно перевода  ·  Esc — отмена");
            if (rect is not { } zone || zone.IsEmpty) return null;
            return SavedRegion.From(zone, ScreenService.MonitorFor(zone, capture.Monitors));
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка выбора зоны", ex);
            Dialogs.Error(null, "Не удалось выбрать зону: " + ex.Message);
            return null;
        }
        finally
        {
            _busy = false;
            ScheduleTrim();
        }
    }

    /// <summary>Закрывает открытое окно результата, чтобы оно не попало в снимок экрана.</summary>
    private async Task CloseResultBeforeCaptureAsync()
    {
        if (_result == null) return;
        _result.Close();
        _result = null;
        // Даём DWM убрать окно с экрана.
        await Task.Delay(150);
    }

    private void ShowResultForCapture(BitmapSource image, PixelRect region)
    {
        var window = CreateResultWindow();
        window.ShowNear(region);
        _ = RunSafeAsync(() => window.ProcessCaptureAsync(image));
    }

    public void OpenHistoryEntry(HistoryEntry entry)
    {
        var window = CreateResultWindow();
        window.LoadFromHistory(entry);
        window.ShowCentered();
    }

    private ResultWindow CreateResultWindow()
    {
        _result?.Close();
        var window = new ResultWindow();
        window.Closed += (_, _) =>
        {
            if (_result == window) _result = null;
            ScheduleTrim();
        };
        _result = window;
        return window;
    }

    public void ShowSettings() => ShowSettings(firstRun: false);

    private void ShowSettings(bool firstRun)
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(firstRun);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            ScheduleTrim();
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void ShowHistory()
    {
        if (_historyWindow != null)
        {
            if (_historyWindow.WindowState == WindowState.Minimized) _historyWindow.WindowState = WindowState.Normal;
            _historyWindow.Activate();
            return;
        }
        _historyWindow = new HistoryWindow();
        _historyWindow.Closed += (_, _) =>
        {
            _historyWindow = null;
            ScheduleTrim();
        };
        _historyWindow.Show();
        _historyWindow.Activate();
    }

    public void Exit()
    {
        Dispose();
        Application.Current.Shutdown();
    }

    private static async Task RunSafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log.Error("Необработанная ошибка", ex);
            Dialogs.Error(null, "Произошла ошибка: " + ex.Message);
        }
    }

    // =====================================================================
    //  Память
    // =====================================================================

    /// <summary>Когда все окна закрыты, освобождаем память (цель — менее 50 МБ в фоне).</summary>
    private void ScheduleTrim()
    {
        _trimTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.ApplicationIdle, (_, _) =>
        {
            _trimTimer!.Stop();
            if (_result == null && _settingsWindow == null && _historyWindow == null && !_busy)
                MemoryHelper.Trim();
        }, Application.Current.Dispatcher);
        _trimTimer.Stop();
        _trimTimer.Start();
    }

    public void Dispose()
    {
        _trimTimer?.Stop();
        _hotkeys?.Dispose();
        _hotkeys = null;
        _tray?.Dispose();
        _tray = null;
        _messageWindow?.Dispose();
        _messageWindow = null;
    }
}
