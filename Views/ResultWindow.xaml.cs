using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenTranslator.Models;
using ScreenTranslator.Native;
using ScreenTranslator.Services;
using ScreenTranslator.Services.Translation;

namespace ScreenTranslator.Views;

/// <summary>
/// Окно результата: распознанный текст (редактируемый), перевод прямо в окне,
/// запасной вариант — браузер, и поле быстрого ответа.
/// </summary>
public partial class ResultWindow : Window
{
    /// <summary>Ожидаемая высота окна (DIP) для выбора места: окно растёт вместе с текстом.</summary>
    private const double EstimatedHeightDip = 620;

    /// <summary>Пределы ширины окна (DIP), когда оно подстраивается под ширину зоны.</summary>
    private const double MinZoneWidthDip = 480;
    private const double MaxZoneWidthDip = 900;

    /// <summary>Наибольшая высота полей, пока высота окна подстраивается под содержимое.</summary>
    private const double SourceBoxAutoMaxHeight = 160;
    private const double TranslationBoxAutoMaxHeight = 180;

    private readonly double _defaultWidth;
    /// <summary>Пользователь тянет край окна (а не перетаскивает его).</summary>
    private bool _sizing;
    /// <summary>В текущем растягивании меняется высота (верхний/нижний край или угол).</summary>
    private bool _heightResized;

    private readonly AppSettings _settings = SettingsService.Current;
    private readonly AppController _app = AppController.Instance;

    private CancellationTokenSource? _sourceCts;
    private CancellationTokenSource? _replyCts;
    private DispatcherTimer? _statusTimer;
    private PixelRect? _anchor;
    private string? _detectedSource;
    private bool _suppressEvents;
    private bool _targetChosenByUser;
    private bool _replyLangChosenByUser;
    private bool _closed;
    private bool _positioned;
    private bool _userMoved;
    /// <summary>Нижняя граница (физ. пиксели), к которой «прилипает» окно, растущее вверх.</summary>
    private int? _bottomAnchor;
    private PixelRect _workArea;
    private DateTime _ignoreDeactivateUntil;
    private DateTime _browserOpenedAt = DateTime.MinValue;
    /// <summary>Снимок выделенной области (для DeepSeek); null у записи из истории.</summary>
    private BitmapSource? _captureImage;

    public ResultWindow()
    {
        InitializeComponent();
        _defaultWidth = Width;
        Icon = IconFactory.WindowIcon;
        AppIconImage.Source = IconFactory.WindowIcon;

        _suppressEvents = true;
        SourceLangCombo.ItemsSource = LanguageCatalog.Items(includeAuto: true);
        TargetLangCombo.ItemsSource = LanguageCatalog.Items(includeAuto: false);
        ReplyLangCombo.ItemsSource = LanguageCatalog.Items(includeAuto: false);
        SourceLangCombo.SelectedValue = LanguageCatalog.Auto;
        TargetLangCombo.SelectedValue = _settings.NativeLanguage;
        ReplyLangCombo.SelectedValue = _settings.NativeLanguage == "en" ? "de" : "en";
        ProviderCombo.ItemsSource = ProviderOption.Build();
        ProviderCombo.SelectedValue = ProviderOption.CurrentId();
        _suppressEvents = false;

        SourceInitialized += (_, _) =>
        {
            ThemeService.ApplyWindowFrame(this, rounded: true);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
            PlaceWindow();
        };
        SizeChanged += (_, _) =>
        {
            if (SizeToContent == SizeToContent.Manual)
                SourceBox.MaxHeight = Math.Max(SourceBoxAutoMaxHeight, ActualHeight * 0.3);
            KeepOnScreen();
        };
        LayoutUpdated += (_, _) => GrowIfClipped();
        // Окно не разворачивается (Win+↑, двойной щелчок): размер меняется только растягиванием.
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
        };
        Loaded += (_, _) =>
        {
            PlaceWindow(); // повторно: после переноса на монитор с другим DPI размер окна меняется
            _positioned = true;
            _ignoreDeactivateUntil = DateTime.Now.AddMilliseconds(500);
        };
        Deactivated += OnDeactivated;
        Closed += (_, _) =>
        {
            _closed = true;
            _sourceCts?.Cancel();
            _replyCts?.Cancel();
            _statusTimer?.Stop();
        };
    }

    /// <summary>
    /// Смена сервиса прямо в окне: сохраняем выбор в настройках и сразу переводим текст заново
    /// (и ответ, если он уже был переведён).
    /// </summary>
    private void ProviderCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressEvents || ProviderCombo.SelectedItem is not ProviderOption option) return;

        option.Apply(_settings);
        SettingsService.Save();
        if (!IsLoaded) return;

        TranslationBox.Text = "";
        if (SourceBox.Text.Trim().Length > 0)
            Run(() => TranslateSourceAsync(autoDetect: SelectedSourceCode == null));
        if (ReplyResultPanel.Visibility == Visibility.Visible && ReplyBox.Text.Trim().Length > 0)
            Run(TranslateReplyAsync);
    }

    // =====================================================================
    //  Показ окна
    // =====================================================================

    /// <summary>Показывает окно рядом с выделенной областью (координаты в физических пикселях).</summary>
    public void ShowNear(PixelRect region)
    {
        _anchor = region;
        ApplySavedSize();
        Show();
        Activate();
    }

    /// <summary>Показывает окно по центру монитора под курсором.</summary>
    public void ShowCentered()
    {
        _anchor = null;
        ApplySavedSize();
        Show();
        Activate();
    }

    /// <summary>
    /// Размер, который пользователь задал растягиванием окна; без него — ширина по зоне (или стандартная)
    /// и высота по содержимому. Задаётся до показа окна: после создания окна WPF перечитал бы старый
    /// размер при первом же перемещении.
    /// </summary>
    private void ApplySavedSize()
    {
        if (_settings.ResultWidth is { } width) Width = Math.Max(MinWidth, width);
        else ApplyZoneWidth();

        if (_settings.ResultHeight is { } height) UseManualHeight(height);
    }

    /// <summary>Высота задана пользователем: всё место, которое не нужно остальным элементам, — полю перевода.</summary>
    private void UseManualHeight(double height)
    {
        SizeToContent = SizeToContent.Manual;
        Height = height;
        TranslationBox.MaxHeight = double.PositiveInfinity;
        SourceBox.MaxHeight = Math.Max(SourceBoxAutoMaxHeight, height * 0.3);
    }

    /// <summary>Двойной щелчок по заголовку: стандартная ширина и высота по содержимому.</summary>
    private void ResetSize()
    {
        _settings.ResultWidth = null;
        _settings.ResultHeight = null;
        SettingsService.Save();

        MinHeight = 0;
        TranslationBox.MaxHeight = TranslationBoxAutoMaxHeight;
        SourceBox.MaxHeight = SourceBoxAutoMaxHeight;
        // Пока высота подстраивается под содержимое, WPF не применяет новую ширину к уже растянутому
        // окну — меняем её в ручном режиме и затем возвращаем подстройку высоты.
        SizeToContent = SizeToContent.Manual;
        Width = ActualWidth;
        Width = _defaultWidth;
        ApplyZoneWidth();
        SizeToContent = SizeToContent.Height;
    }

    /// <summary>
    /// Высота задана пользователем, а содержимого стало больше (ошибка, перевод ответа) — поле перевода
    /// сжалось до минимума и нижние кнопки не помещаются. Тогда окно подрастает.
    /// </summary>
    private void GrowIfClipped()
    {
        if (SizeToContent != SizeToContent.Manual || _sizing || !IsLoaded) return;
        double deficit = TranslationBox.MinHeight - TranslationBoxRow.ActualHeight;
        if (deficit > 1) Height = ActualHeight + Math.Ceiling(deficit);
    }

    /// <summary>
    /// В режиме зоны ширина окна подстраивается под ширину зоны.
    /// </summary>
    private void ApplyZoneWidth()
    {
        if (_settings.ResultPlacement != ResultPlacement.CustomZone || _settings.ResultZone == null) return;
        var monitors = ScreenService.GetMonitors();
        if (ScreenService.ResolveSavedRegion(_settings.ResultZone, monitors) is not { } zone) return;
        var monitor = ScreenService.MonitorFor(zone, monitors);
        Width = Math.Clamp(zone.Width / monitor.Scale, MinZoneWidthDip, MaxZoneWidthDip);
    }

    /// <summary>Выбирает место окна по настройке «Где открывать окно перевода».</summary>
    private void PlaceWindow()
    {
        if (_positioned) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var monitors = ScreenService.GetMonitors();
        _bottomAnchor = null;

        switch (_settings.ResultPlacement)
        {
            case ResultPlacement.CustomZone when _settings.ResultZone != null:
                if (ScreenService.ResolveSavedRegion(_settings.ResultZone, monitors) is { } zone)
                {
                    PlaceInZone(hwnd, zone, monitors);
                    return;
                }
                break; // зона вне экрана (монитор отключён) — открываем рядом с выделением

            case ResultPlacement.LastPosition when _settings.LastResultPosition != null:
                if (ScreenService.ResolveSavedRegion(_settings.LastResultPosition, monitors) is { } last)
                {
                    var monitor = ScreenService.MonitorFor(last, monitors);
                    MoveTo(hwnd, last.X, last.Y, monitor);
                    return;
                }
                break;

            case ResultPlacement.ScreenCenter:
                PlaceCentered(hwnd, monitors);
                return;
        }

        if (_anchor is { } region) PlaceNearSelection(hwnd, region, monitors);
        else PlaceCentered(hwnd, monitors);
    }

    /// <summary>Под областью → над областью → справа → слева.</summary>
    private void PlaceNearSelection(IntPtr hwnd, PixelRect r, IReadOnlyList<MonitorInfo> monitors)
    {
        var monitor = ScreenService.MonitorFor(r, monitors);
        double scale = monitor.Scale;
        int w = (int)Math.Ceiling(Width * scale);
        int h = (int)Math.Ceiling(Math.Max(ActualHeight, EstimatedHeightDip) * scale);
        var work = monitor.WorkArea;
        int gap = (int)Math.Round(8 * scale);

        int x = r.X;
        int y = r.Bottom + gap;
        if (y + h > work.Bottom)
        {
            if (r.Y - gap - h >= work.Y)
            {
                // Над областью: окно растёт вверх, нижний край остаётся у выделения.
                _bottomAnchor = r.Y - gap;
                y = _bottomAnchor.Value - CurrentHeightPx(hwnd, h);
            }
            else
            {
                y = r.Y;
                if (r.Right + gap + w <= work.Right) x = r.Right + gap;
                else if (r.X - gap - w >= work.X) x = r.X - gap - w;
                else x = work.Right - w;
            }
        }
        SetPosition(hwnd, x, y, w, work);
    }

    /// <summary>
    /// Окно в выбранной зоне: ширина подстраивается под зону, в нижней половине экрана
    /// окно прижимается к нижнему краю зоны и растёт вверх.
    /// </summary>
    private void PlaceInZone(IntPtr hwnd, PixelRect zone, IReadOnlyList<MonitorInfo> monitors)
    {
        var monitor = ScreenService.MonitorFor(zone, monitors);
        double scale = monitor.Scale;

        int w = (int)Math.Ceiling(Width * scale);
        var work = monitor.WorkArea;
        int x = zone.Width > w ? zone.X + (zone.Width - w) / 2 : zone.X;
        int y = zone.Y;

        bool lowerHalf = zone.Y + zone.Height / 2 > work.Y + work.Height / 2;
        if (lowerHalf)
        {
            _bottomAnchor = Math.Min(zone.Bottom, work.Bottom);
            y = _bottomAnchor.Value - CurrentHeightPx(hwnd, (int)Math.Ceiling(EstimatedHeightDip * scale));
        }
        SetPosition(hwnd, x, y, w, work);
    }

    private void PlaceCentered(IntPtr hwnd, IReadOnlyList<MonitorInfo> monitors)
    {
        var monitor = _anchor is { } a ? ScreenService.MonitorFor(a, monitors) : ScreenService.MonitorUnderCursor();
        double scale = monitor.Scale;
        int w = (int)Math.Ceiling(Width * scale);
        int h = (int)Math.Ceiling(Math.Max(ActualHeight, EstimatedHeightDip) * scale);
        var work = monitor.WorkArea;
        SetPosition(hwnd, work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, work);
    }

    private void MoveTo(IntPtr hwnd, int x, int y, MonitorInfo monitor)
    {
        int w = (int)Math.Ceiling(Width * monitor.Scale);
        SetPosition(hwnd, x, y, w, monitor.WorkArea);
    }

    private void SetPosition(IntPtr hwnd, int x, int y, int widthPx, PixelRect work)
    {
        _workArea = work;
        x = Math.Clamp(x, work.X, Math.Max(work.X, work.Right - widthPx));
        y = Math.Max(work.Y, Math.Min(y, work.Bottom - 50));
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        KeepOnScreen();
    }

    /// <summary>Текущая высота окна в физических пикселях (до первого показа — оценка).</summary>
    private int CurrentHeightPx(IntPtr hwnd, int fallback)
    {
        if (ActualHeight > 0)
            return (int)Math.Ceiling(ActualHeight * VisualTreeHelper.GetDpi(this).DpiScaleY);
        if (NativeMethods.GetWindowRect(hwnd, out var rect) && rect.Bottom - rect.Top > 50)
            return rect.Bottom - rect.Top;
        return fallback;
    }

    /// <summary>
    /// При изменении высоты (появился перевод) окно не должно уходить за край экрана:
    /// привязанное снизу растёт вверх, остальные сдвигаются вверх, если упёрлись в низ.
    /// После ручного перетаскивания окно больше не двигаем.
    /// </summary>
    private void KeepOnScreen()
    {
        if (_userMoved || _workArea.IsEmpty || ActualHeight <= 0) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect)) return;

        // Во время SizeChanged окно Windows ещё старого размера — берём новую высоту из WPF.
        int height = (int)Math.Ceiling(ActualHeight * VisualTreeHelper.GetDpi(this).DpiScaleY);
        int y = rect.Top;
        if (_bottomAnchor is { } bottom) y = bottom - height;
        if (y + height > _workArea.Bottom) y = _workArea.Bottom - height;
        y = Math.Max(_workArea.Y, y);

        if (y != rect.Top)
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, rect.Left, y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_ENTERSIZEMOVE:
                _userMoved = true;
                break;

            case NativeMethods.WM_SIZING:
                if (!_sizing)
                {
                    _sizing = true;
                    // Не даём сжать окно так, чтобы поле перевода стало меньше минимума и кнопки обрезались.
                    MinHeight = Math.Max(0, ActualHeight - (TranslationBox.ActualHeight - TranslationBox.MinHeight));
                }
                if (!_heightResized && wParam.ToInt32() >= NativeMethods.WMSZ_TOP)
                {
                    _heightResized = true;
                    UseManualHeight(ActualHeight);
                }
                break;

            case NativeMethods.WM_EXITSIZEMOVE:
                if (_sizing) FinishResize();
                SaveLastPosition(hwnd);
                break;

            case NativeMethods.WM_NCLBUTTONDBLCLK when wParam.ToInt32() == NativeMethods.HTCAPTION:
                ResetSize();
                handled = true;
                break;

            case NativeMethods.WM_SYSCOMMAND when (wParam.ToInt32() & 0xFFF0) == NativeMethods.SC_MAXIMIZE:
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Запоминает размер после растягивания. Если меняли только ширину, высота по-прежнему
    /// подстраивается под содержимое.
    /// </summary>
    private void FinishResize()
    {
        _settings.ResultWidth = Math.Round(ActualWidth);
        if (_heightResized || _settings.ResultHeight != null)
        {
            _settings.ResultHeight = Math.Round(ActualHeight);
            UseManualHeight(ActualHeight);
        }
        else
        {
            // WPF отключает подстройку высоты при любом растягивании — возвращаем её.
            MinHeight = 0;
            SizeToContent = SizeToContent.Height;
        }
        _sizing = false;
        _heightResized = false;
    }

    /// <summary>Запоминает место, куда пользователь перетащил окно (для режима «как в прошлый раз»).</summary>
    private void SaveLastPosition(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;
        var topLeft = new PixelRect(rect.Left, rect.Top, Math.Max(1, rect.Right - rect.Left), 1);
        var monitor = ScreenService.MonitorFor(topLeft);
        _settings.LastResultPosition = SavedRegion.From(topLeft, monitor);
        SettingsService.Save();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_settings.CloseOnDeactivate || PinButton.IsChecked == true) return;
        if (Dialogs.OpenCount > 0 || DateTime.Now < _ignoreDeactivateUntil) return;

        // Фокус ушёл в только что открытый браузер — окно оставляем, чтобы можно было ответить.
        if ((DateTime.Now - _browserOpenedAt).TotalSeconds < 10)
        {
            _browserOpenedAt = DateTime.MinValue;
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!_closed && !IsActive) Close();
        });
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && !e.Handled)
        {
            e.Handled = true;
            Close();
        }
    }

    // =====================================================================
    //  Распознавание
    // =====================================================================

    public async Task ProcessCaptureAsync(BitmapSource image)
    {
        _captureImage = image;
        HideErrors();
        OcrBusyPanel.Visibility = Visibility.Visible;
        SourceBox.IsReadOnly = true;
        DetectInfoText.Text = "";

        OcrOutcome outcome;
        try
        {
            outcome = await _app.Ocr.RecognizeAsync(image, _settings.OcrLanguage);
        }
        finally
        {
            OcrBusyPanel.Visibility = Visibility.Collapsed;
            SourceBox.IsReadOnly = false;
        }
        if (_closed) return;

        switch (outcome.Status)
        {
            case OcrStatus.NoLanguagesInstalled:
            case OcrStatus.LanguageNotInstalled:
                ShowOcrError(outcome.Message + "\nНажмите «Как установить язык OCR» — там пошаговая инструкция.", showHelp: true);
                return;
            case OcrStatus.NoText:
                ShowOcrError("В выделенной области не найден текст. Попробуйте выделить область точнее или крупнее.\n" +
                             $"Языки распознавания в системе: {OcrService.InstalledLanguagesText()}. " +
                             "Если текст на другом языке, установите для него пакет OCR.", showHelp: true);
                return;
            case OcrStatus.Failed:
                ShowOcrError(outcome.Message, showHelp: false);
                return;
        }

        SourceBox.Text = outcome.Text;

        var detection = LanguageDetector.Detect(outcome.Text);
        var language = detection.Code != null && detection.Confidence >= 0.5
            ? detection.Code
            : outcome.LanguageCode ?? detection.Code;
        ApplyDetectedSource(language, "по тексту");
        ReplyBox.Focus();

        await TranslateSourceAsync(autoDetect: true);
    }

    private void ShowOcrError(string message, bool showHelp)
    {
        OcrErrorText.Text = message;
        OcrHelpButton.Visibility = showHelp ? Visibility.Visible : Visibility.Collapsed;
        OcrErrorPanel.Visibility = Visibility.Visible;
        SourceBox.Focus();
    }

    /// <summary>Устанавливает язык исходного текста и подбирает языки перевода по умолчанию.</summary>
    private void ApplyDetectedSource(string? code, string how)
    {
        _suppressEvents = true;
        try
        {
            _detectedSource = code;
            SourceLangCombo.SelectedValue = code ?? LanguageCatalog.Auto;

            if (code == null)
            {
                DetectInfoText.Text = "Язык не определён — при необходимости выберите его вручную.";
                return;
            }

            DetectInfoText.Text = $"Язык определён {how}: {LanguageCatalog.NameOf(code)}";
            string native = _settings.NativeLanguage;

            if (!_targetChosenByUser)
                TargetLangCombo.SelectedValue = code == native ? (native == "en" ? "ru" : "en") : native;

            // Ответ по умолчанию переводится на язык исходного текста.
            if (!_replyLangChosenByUser && code != native)
                ReplyLangCombo.SelectedValue = code;
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    // =====================================================================
    //  Перевод распознанного текста
    // =====================================================================

    private string? SelectedSourceCode =>
        SourceLangCombo.SelectedValue is string code && code != LanguageCatalog.Auto ? code : null;

    private string TargetCode => TargetLangCombo.SelectedValue as string ?? _settings.NativeLanguage;

    private string ReplyCode => ReplyLangCombo.SelectedValue as string ?? "en";

    private async Task TranslateSourceAsync(bool autoDetect)
    {
        var text = SourceBox.Text.Trim();
        TranslationErrorPanel.Visibility = Visibility.Collapsed;
        if (text.Length == 0)
        {
            ShowTranslationError("Нет текста для перевода.", showSettings: false);
            return;
        }

        _sourceCts?.Cancel();
        var cts = _sourceCts = new CancellationTokenSource();
        var target = TargetCode;
        var source = autoDetect ? null : SelectedSourceCode;

        TranslationProgress.Visibility = Visibility.Visible;
        TranslateButton.IsEnabled = false;
        TranslationResult result;
        try
        {
            var hint = autoDetect ? _detectedSource : null;
            result = await _app.Translation.TranslateAsync(text, source, target, cts.Token, hint);
        }
        finally
        {
            if (_sourceCts == cts)
            {
                TranslationProgress.Visibility = Visibility.Collapsed;
                TranslateButton.IsEnabled = true;
            }
        }

        if (_closed || cts.IsCancellationRequested) return;

        if (!result.Success)
        {
            if (result.Error == TranslationError.Cancelled) return;
            bool settingsRelated = result.Error is TranslationError.NotConfigured or TranslationError.InvalidKey
                or TranslationError.QuotaExceeded or TranslationError.UnsupportedLanguage;
            ShowTranslationError(result.ErrorMessage + "\nМожно открыть перевод в браузере.", settingsRelated);
            return;
        }

        if (source == null && result.DetectedSourceLanguage is { } detected && detected != _detectedSource)
        {
            ApplyDetectedSource(detected, $"сервисом «{result.Provider}»");
            // Текст оказался на языке перевода — переводим на другой язык.
            if (TargetCode != target)
            {
                await TranslateSourceAsync(autoDetect: false);
                return;
            }
        }

        TranslationBox.Text = result.Text;
        _app.History.Add(new HistoryEntry
        {
            Kind = HistoryKind.Region,
            SourceText = text,
            TranslatedText = result.Text,
            SourceLang = source ?? result.DetectedSourceLanguage ?? _detectedSource,
            TargetLang = target
        });
    }

    private void ShowTranslationError(string message, bool showSettings)
    {
        TranslationErrorText.Text = message;
        ErrorSettingsButton.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
        TranslationErrorPanel.Visibility = Visibility.Visible;
    }

    private void OpenSourceInBrowser(WebTranslatorKind kind) =>
        OpenInBrowser(kind, SourceBox.Text.Trim(), SelectedSourceCode, TargetCode, "Перевод открыт");

    /// <summary>Открывает текст в Google или Яндекс Переводчике в выбранном в настройках браузере.</summary>
    private void OpenInBrowser(WebTranslatorKind kind, string text, string? source, string target, string status)
    {
        if (text.Length == 0)
        {
            ShowStatus("Нет текста для перевода");
            return;
        }

        _browserOpenedAt = DateTime.Now;
        if (!BrowserService.OpenTranslation(this, kind, text, source, target))
        {
            _browserOpenedAt = DateTime.MinValue;
            return;
        }
        ShowStatus($"{status} в {(kind == WebTranslatorKind.Yandex ? "Яндекс Переводчике" : "Google Переводчике")}");
    }

    // =====================================================================
    //  Быстрый ответ
    // =====================================================================

    /// <summary>
    /// Язык ответа: по условию пользователь пишет на родном языке, но если детектор уверенно
    /// видит другой язык — доверяем автоопределению провайдера.
    /// </summary>
    private string? ReplySourceCode(string text)
    {
        var native = _settings.NativeLanguage;
        if (ReplyCode == native) return null;
        var detection = LanguageDetector.Detect(text);
        return detection.Code != null && detection.Code != native && detection.Confidence >= 0.7 ? null : native;
    }

    private async Task TranslateReplyAsync()
    {
        var text = ReplyBox.Text.Trim();
        ReplyErrorPanel.Visibility = Visibility.Collapsed;
        if (text.Length == 0)
        {
            ReplyBox.Focus();
            return;
        }

        _replyCts?.Cancel();
        var cts = _replyCts = new CancellationTokenSource();
        var target = ReplyCode;
        var source = ReplySourceCode(text);

        ReplyResultPanel.Visibility = Visibility.Visible;
        ReplyResultBox.Text = "";
        ReplyProgress.Visibility = Visibility.Visible;
        ReplyTranslateButton.IsEnabled = false;
        TranslationResult result;
        try
        {
            var hint = LanguageDetector.Detect(text) is { Code: { } code, Confidence: >= 0.5 } ? code : _settings.NativeLanguage;
            result = await _app.Translation.TranslateAsync(text, source, target, cts.Token, hint);
        }
        finally
        {
            if (_replyCts == cts)
            {
                ReplyProgress.Visibility = Visibility.Collapsed;
                ReplyTranslateButton.IsEnabled = true;
            }
        }

        if (_closed || cts.IsCancellationRequested) return;

        if (!result.Success)
        {
            if (result.Error == TranslationError.Cancelled) return;
            ReplyResultPanel.Visibility = Visibility.Collapsed;
            ReplyErrorText.Text = result.ErrorMessage + "\nМожно открыть перевод ответа в браузере.";
            ReplyErrorPanel.Visibility = Visibility.Visible;
            return;
        }

        ReplyResultBox.Text = result.Text;
        if (_settings.AutoCopyReply && ClipboardHelper.TrySetText(result.Text))
            ShowStatus("Перевод ответа скопирован в буфер обмена");

        _app.History.Add(new HistoryEntry
        {
            Kind = HistoryKind.Reply,
            SourceText = text,
            TranslatedText = result.Text,
            SourceLang = source ?? result.DetectedSourceLanguage,
            TargetLang = target
        });
    }

    private void OpenReplyInBrowser(WebTranslatorKind kind)
    {
        var text = ReplyBox.Text.Trim();
        if (text.Length == 0)
        {
            ReplyBox.Focus();
            return;
        }
        OpenInBrowser(kind, text, ReplySourceCode(text), ReplyCode, "Перевод ответа открыт");
    }

    // =====================================================================
    //  История
    // =====================================================================

    /// <summary>Загружает запись истории обратно в окно.</summary>
    public void LoadFromHistory(HistoryEntry entry)
    {
        _suppressEvents = true;
        try
        {
            if (entry.Kind == HistoryKind.Region)
            {
                SourceBox.Text = entry.SourceText;
                _detectedSource = entry.SourceLang;
                SourceLangCombo.SelectedValue = entry.SourceLang ?? LanguageCatalog.Auto;
                TargetLangCombo.SelectedValue = entry.TargetLang;
                _targetChosenByUser = true;
                TranslationBox.Text = entry.TranslatedText;
                if (entry.SourceLang != null && entry.SourceLang != _settings.NativeLanguage)
                    ReplyLangCombo.SelectedValue = entry.SourceLang;
            }
            else
            {
                ReplyBox.Text = entry.SourceText;
                ReplyLangCombo.SelectedValue = entry.TargetLang;
                _replyLangChosenByUser = true;
                ReplyResultBox.Text = entry.TranslatedText;
                ReplyResultPanel.Visibility = string.IsNullOrEmpty(entry.TranslatedText) ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        finally
        {
            _suppressEvents = false;
        }

        DetectInfoText.Text = $"Запись из истории от {entry.DateText}";
    }

    // =====================================================================
    //  Обработчики событий
    // =====================================================================

    private async void Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка в окне результата", ex);
            // Сообщение показываем только из потока интерфейса, иначе WPF завершит приложение.
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (!_closed) Dialogs.Error(this, "Произошла ошибка: " + ex.Message);
            });
        }
    }

    private void SourceLangCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressEvents || !IsLoaded) return;
        var code = SelectedSourceCode;
        _detectedSource = code;
        DetectInfoText.Text = code == null ? "Язык будет определён автоматически" : "Язык выбран вручную";

        if (code != null && !_replyLangChosenByUser && code != _settings.NativeLanguage)
        {
            _suppressEvents = true;
            ReplyLangCombo.SelectedValue = code;
            _suppressEvents = false;
        }

        if (SourceBox.Text.Trim().Length > 0)
            Run(() => TranslateSourceAsync(autoDetect: code == null));
    }

    private void TargetLangCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressEvents || !IsLoaded) return;
        _targetChosenByUser = true;
        if (SourceBox.Text.Trim().Length > 0)
            Run(() => TranslateSourceAsync(autoDetect: SelectedSourceCode == null));
    }

    /// <summary>
    /// Меняет местами язык текста и язык перевода. Если перевод уже есть, тексты тоже меняются местами
    /// (как в Google Переводчике): перевод становится исходным текстом, а исходный текст — его переводом.
    /// </summary>
    private void SwapLanguagesButton_Click(object sender, RoutedEventArgs e)
    {
        var source = SelectedSourceCode ?? _detectedSource;
        var target = TargetCode;
        if (source == null)
        {
            ShowStatus("Язык текста не определён — выберите его вручную");
            return;
        }
        if (source == target) return;

        var translation = TranslationBox.Text.Trim();
        bool swapTexts = translation.Length > 0 && TranslationErrorPanel.Visibility != Visibility.Visible;

        _sourceCts?.Cancel();
        _suppressEvents = true;
        try
        {
            SourceLangCombo.SelectedValue = target;
            _detectedSource = target;
            TargetLangCombo.SelectedValue = source;
            _targetChosenByUser = true;
            DetectInfoText.Text = $"Языки поменяны местами: {LanguageCatalog.NameOf(target)} → {LanguageCatalog.NameOf(source)}";

            if (swapTexts)
            {
                TranslationBox.Text = SourceBox.Text.Trim();
                SourceBox.Text = translation;
            }
        }
        finally
        {
            _suppressEvents = false;
        }

        // Тексты поменялись — перевод уже готов (это исходный текст). Иначе переводим в новом направлении.
        if (!swapTexts && SourceBox.Text.Trim().Length > 0)
            Run(() => TranslateSourceAsync(autoDetect: false));
    }

    private void ReplyLangCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressEvents || !IsLoaded) return;
        _replyLangChosenByUser = true;
        if (ReplyResultPanel.Visibility == Visibility.Visible && ReplyBox.Text.Trim().Length > 0)
            Run(TranslateReplyAsync);
    }

    private void SourceBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Run(() => TranslateSourceAsync(autoDetect: SelectedSourceCode == null));
        }
    }

    private void ReplyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter — перевести, Shift+Enter — новая строка.
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            Run(TranslateReplyAsync);
        }
    }

    private void TranslateButton_Click(object sender, RoutedEventArgs e) =>
        Run(() => TranslateSourceAsync(autoDetect: SelectedSourceCode == null));

    /// <summary>Кнопки «Google» и «Яндекс»: какой переводчик открыть, указано в Tag кнопки.</summary>
    private static WebTranslatorKind WebKind(object sender) =>
        (sender as FrameworkElement)?.Tag as string == nameof(WebTranslatorKind.Yandex)
            ? WebTranslatorKind.Yandex
            : WebTranslatorKind.Google;

    private void SourceWebButton_Click(object sender, RoutedEventArgs e) => OpenSourceInBrowser(WebKind(sender));

    private void DeepSeekButton_Click(object sender, RoutedEventArgs e) => Run(SendToDeepSeekAsync);

    /// <summary>Открывает DeepSeek в браузере и вставляет туда снимок области (или текст для записи из истории).</summary>
    private async Task SendToDeepSeekAsync()
    {
        _browserOpenedAt = DateTime.Now;
        var outcome = await DeepSeekLauncher.SendAsync(this, _captureImage, SourceBox.Text, TargetCode);
        if (outcome == DeepSeekLauncher.Outcome.NotStarted) _browserOpenedAt = DateTime.MinValue;
        if (_closed) return;

        string what = _captureImage != null ? "Картинка" : "Текст";
        switch (outcome)
        {
            case DeepSeekLauncher.Outcome.Pasted:
                ShowStatus("Отправлено в DeepSeek — нажмите Enter. Если поле пустое, нажмите в нём Ctrl+V");
                break;
            case DeepSeekLauncher.Outcome.PasteManually:
                ShowStatus($"{what} в буфере обмена — войдите в DeepSeek и нажмите Ctrl+V в поле сообщения");
                break;
        }
    }

    private void ReplyTranslateButton_Click(object sender, RoutedEventArgs e) => Run(TranslateReplyAsync);

    private void ReplyWebButton_Click(object sender, RoutedEventArgs e) => OpenReplyInBrowser(WebKind(sender));

    private void CopySourceButton_Click(object sender, RoutedEventArgs e) => CopyWithStatus(SourceBox.Text, "Текст скопирован");

    private void CopyTranslationButton_Click(object sender, RoutedEventArgs e) => CopyWithStatus(TranslationBox.Text, "Перевод скопирован");

    private void CopyReplyButton_Click(object sender, RoutedEventArgs e) => CopyWithStatus(ReplyResultBox.Text, "Перевод ответа скопирован");

    private void CopyWithStatus(string text, string status)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowStatus("Нечего копировать");
            return;
        }
        ShowStatus(ClipboardHelper.TrySetText(text.Trim()) ? status : "Не удалось скопировать: буфер обмена занят");
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _app.ShowHistory();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _app.ShowSettings();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OcrHelpButton_Click(object sender, RoutedEventArgs e) =>
        Dialogs.Info(this, OcrService.InstallInstructions + $"\n\nСейчас установлены: {OcrService.InstalledLanguagesText()}.");

    private void ReselectButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _app.StartSelection();
    }

    private void HideErrors()
    {
        OcrErrorPanel.Visibility = Visibility.Collapsed;
        TranslationErrorPanel.Visibility = Visibility.Collapsed;
        ReplyErrorPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
        _statusTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Normal, (_, _) =>
        {
            StatusText.Visibility = Visibility.Collapsed;
            _statusTimer?.Stop();
        }, Dispatcher);
        _statusTimer.Stop();
        _statusTimer.Start();
    }
}
