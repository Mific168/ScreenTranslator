using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using ScreenTranslator.Models;
using ScreenTranslator.Services;
using ScreenTranslator.Services.Offline;
using ScreenTranslator.Services.Translation;

namespace ScreenTranslator.Views;

public partial class SettingsWindow : Window
{
    private readonly AppController _app = AppController.Instance;
    private readonly AppSettings _settings = SettingsService.Current;
    private readonly bool _firstRun;
    private CancellationTokenSource? _testCts;
    /// <summary>Зона для окна перевода (сохраняется только по кнопке «Сохранить»).</summary>
    private SavedRegion? _pendingZone;

    private sealed record OcrItem(string Code, string Name);

    public SettingsWindow(bool firstRun)
    {
        InitializeComponent();
        _firstRun = firstRun;
        Icon = IconFactory.WindowIcon;
        SourceInitialized += (_, _) => ThemeService.ApplyWindowFrame(this);
        Closed += OnClosed;

        foreach (var box in new[] { SelectHotkeyBox, RepeatHotkeyBox })
        {
            // Пока поле в фокусе, глобальные сочетания отключены — иначе их нельзя «нажать» в поле.
            box.GotKeyboardFocus += (_, _) => _app.SuspendHotkeys();
            box.LostKeyboardFocus += (_, _) => _app.ResumeHotkeys();
        }

        LoadValues();

        _app.OfflineModels.StateChanged += OnModelStateChanged;
        NativeLangCombo.SelectionChanged += (_, _) => LoadOfflineModels();
    }

    private void LoadValues()
    {
        FirstRunBanner.Visibility = _firstRun ? Visibility.Visible : Visibility.Collapsed;
        FirstRunHotkeyRun.Text = _settings.SelectHotkey.ToString();

        SelectHotkeyBox.Hotkey = _settings.SelectHotkey;
        RepeatHotkeyBox.Hotkey = _settings.RepeatHotkey;

        SelectByTag(ProviderCombo, _settings.Provider.ToString());
        LibreUrlBox.Text = _settings.LibreUrl;
        LibreKeyBox.Password = SettingsService.GetLibreKey();
        UpdateProviderPanels();

        LoadBrowsers();

        NativeLangCombo.ItemsSource = LanguageCatalog.Items(includeAuto: false);
        NativeLangCombo.SelectedValue = _settings.NativeLanguage;
        PreferQualityCheck.IsChecked = _settings.OfflinePreferQuality;
        LoadOfflineModels();

        var installed = OcrService.GetInstalledLanguages();
        var ocrItems = new List<OcrItem> { new("auto", "Автоматически (все установленные)") };
        ocrItems.AddRange(installed.Select(l => new OcrItem(l.LanguageTag, $"{l.DisplayName} ({l.LanguageTag})")));
        if (_settings.OcrLanguage != "auto" && ocrItems.All(i => !string.Equals(i.Code, _settings.OcrLanguage, StringComparison.OrdinalIgnoreCase)))
            ocrItems.Add(new OcrItem(_settings.OcrLanguage, $"{_settings.OcrLanguage} — не установлен"));
        OcrLangCombo.ItemsSource = ocrItems;
        OcrLangCombo.SelectedValue = ocrItems.FirstOrDefault(i => string.Equals(i.Code, _settings.OcrLanguage, StringComparison.OrdinalIgnoreCase))?.Code ?? "auto";
        OcrInstalledRun.Text = installed.Count == 0
            ? "Внимание: в Windows не установлен ни один язык распознавания. "
            : $"Установлены: {string.Join(", ", installed.Select(l => l.DisplayName))}. ";

        _pendingZone = _settings.ResultZone;
        SelectByTag(PlacementCombo, _settings.ResultPlacement.ToString());
        UpdatePlacementPanel();

        CloseOnDeactivateCheck.IsChecked = _settings.CloseOnDeactivate;
        AutoCopyReplyCheck.IsChecked = _settings.AutoCopyReply;
        AutoStartCheck.IsChecked = _settings.AutoStart;
        HistoryEnabledCheck.IsChecked = _settings.HistoryEnabled;

        DataFolderRun.Text = SettingsService.AppDataFolder;
    }

    private void LoadBrowsers()
    {
        var browsers = new List<BrowserInfo> { BrowserInfo.Default };
        browsers.AddRange(BrowserService.DetectBrowsers());

        // Сохранённый браузер мог быть удалён — показываем его, чтобы пользователь это увидел.
        if (_settings.BrowserId != BrowserInfo.DefaultId && browsers.All(b => b.Id != _settings.BrowserId))
            browsers.Add(new BrowserInfo(_settings.BrowserId, _settings.BrowserName + " (не найден)", _settings.BrowserPath));

        var previous = (BrowserCombo.SelectedItem as BrowserInfo)?.Id ?? _settings.BrowserId;
        BrowserCombo.ItemsSource = browsers;
        BrowserCombo.SelectedItem = browsers.FirstOrDefault(b => b.Id == previous) ?? browsers[0];
    }

    private static void SelectByTag(ComboBox combo, string tag)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag)
                             ?? combo.Items.OfType<ComboBoxItem>().First();
    }

    private static string SelectedTag(ComboBox combo) => (string)((ComboBoxItem)combo.SelectedItem).Tag;

    private TranslationProviderKind SelectedProvider =>
        Enum.TryParse<TranslationProviderKind>(SelectedTag(ProviderCombo), out var p) ? p : TranslationProviderKind.Offline;

    private void UpdateProviderPanels()
    {
        var provider = SelectedProvider;
        OfflinePanel.Visibility = provider == TranslationProviderKind.Offline ? Visibility.Visible : Visibility.Collapsed;
        LibrePanel.Visibility = provider == TranslationProviderKind.LibreTranslate ? Visibility.Visible : Visibility.Collapsed;
        OnlinePanel.Visibility = provider == TranslationProviderKind.Online ? Visibility.Visible : Visibility.Collapsed;
        TestResultText.Text = "";
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized && OfflinePanel != null) UpdateProviderPanels();
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        _testCts?.Cancel();
        var cts = _testCts = new CancellationTokenSource();

        ITranslationProvider provider = SelectedProvider switch
        {
            TranslationProviderKind.LibreTranslate => TranslationService.CreateLibre(LibreUrlBox.Text, LibreKeyBox.Password),
            TranslationProviderKind.Online => TranslationService.CreateOnline(),
            _ => new OfflineProvider(_app.OfflineModels)
        };

        TestButton.IsEnabled = false;
        TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush");
        TestResultText.Text = "Проверяю…";
        try
        {
            var native = (NativeLangCombo.SelectedValue as string) ?? "ru";
            var result = await TranslationService.TranslateWithAsync(provider, "Hello, world!", "en", native == "en" ? "de" : native, cts.Token);
            if (cts.IsCancellationRequested) return;

            if (result.Success)
            {
                TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
                TestResultText.Text = $"Работает: «{result.Text}»";
            }
            else
            {
                TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
                TestResultText.Text = result.ErrorMessage;
            }
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void RefreshBrowsers_Click(object sender, RoutedEventArgs e) => LoadBrowsers();

    // ---------- Офлайн-модели ----------

    private string SelectedNative => NativeLangCombo.SelectedValue as string ?? _settings.NativeLanguage;

    /// <summary>Модели «родной язык ↔ английский» — с них стоит начать.</summary>
    private List<OfflineModelInfo> RecommendedModels()
    {
        var native = SelectedNative == "en" ? "ru" : SelectedNative;
        return new[] { OfflineModelCatalog.Find(native, "en"), OfflineModelCatalog.Find("en", native) }
            .Where(m => m != null).Select(m => m!).ToList();
    }

    private void LoadOfflineModels()
    {
        var native = SelectedNative;
        // Сначала модели с родным языком, затем остальные.
        var items = OfflineModelCatalog.All
            .OrderByDescending(m => m.IsMultilingual)
            .ThenByDescending(m => m.Source == native || m.Target == native)
            .ThenByDescending(m => m.IsInstalled)
            .Select(m =>
            {
                var item = new OfflineModelItem(m);
                item.Apply(_app.OfflineModels.GetState(m.Id) is { IsDownloading: true } s ? s : null);
                return item;
            })
            .ToList();
        OfflineModelsList.ItemsSource = items;
        UpdateRecommendedButton();
        UpdateQualityCheck();
    }

    private void UpdateQualityCheck()
    {
        bool installed = OfflineModelCatalog.Nllb.IsInstalled;
        PreferQualityCheck.IsEnabled = installed;
        PreferQualityCheck.ToolTip = installed ? null : "Сначала скачайте модель NLLB-200 в списке выше";
        if (!installed) PreferQualityCheck.IsChecked = false;
    }

    private void UpdateRecommendedButton()
    {
        var recommended = RecommendedModels();
        if (recommended.Count == 0)
        {
            InstallRecommendedButton.Visibility = Visibility.Collapsed;
            return;
        }

        var native = SelectedNative == "en" ? "ru" : SelectedNative;
        var pair = $"{LanguageCatalog.NameOf(native)} ↔ Английский";
        bool allInstalled = recommended.All(m => m.IsInstalled);
        bool downloading = recommended.Any(m => _app.OfflineModels.IsDownloading(m.Id));

        InstallRecommendedButton.Visibility = Visibility.Visible;
        InstallRecommendedButton.IsEnabled = !allInstalled && !downloading;
        InstallRecommendedButton.Content = allInstalled
            ? $"✓ {pair}: установлено"
            : downloading ? $"Загрузка: {pair}…" : $"Установить {pair} (~{recommended.Where(m => !m.IsInstalled).Sum(m => m.SizeMb)} МБ)";
    }

    private string RecommendedLabel()
    {
        var native = SelectedNative == "en" ? "ru" : SelectedNative;
        return $"{LanguageCatalog.NameOf(native)} ↔ Английский, ~{RecommendedModels().Sum(m => m.SizeMb)} МБ";
    }

    private void InstallRecommendedButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var model in RecommendedModels().Where(m => !m.IsInstalled))
            _ = _app.OfflineModels.DownloadAsync(model);
        UpdateRecommendedButton();
    }

    private void ModelActionButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not OfflineModelItem item) return;
        var model = item.Info;

        if (_app.OfflineModels.IsDownloading(model.Id))
        {
            _app.OfflineModels.CancelDownload(model.Id);
        }
        else if (model.IsInstalled)
        {
            if (Dialogs.Confirm(this, $"Удалить модель «{model.DisplayName}»? Её можно будет скачать снова."))
                _app.OfflineModels.Delete(model);
        }
        else
        {
            _ = _app.OfflineModels.DownloadAsync(model);
        }
    }

    private void OnModelStateChanged(ModelDownloadState state)
    {
        if (OfflineModelsList.ItemsSource is IEnumerable<OfflineModelItem> items)
            items.FirstOrDefault(i => i.Info.Id == state.ModelId)?.Apply(state);
        UpdateRecommendedButton();
        UpdateQualityCheck();

        // Только что скачали NLLB — пользователь хочет качество, включаем её сразу.
        if (state.ModelId == OfflineModelCatalog.Nllb.Id && !state.IsDownloading && state.Error == null && OfflineModelCatalog.Nllb.IsInstalled)
            PreferQualityCheck.IsChecked = true;
    }

    // ---------- Размещение окна перевода ----------

    private ResultPlacement SelectedPlacement =>
        Enum.TryParse<ResultPlacement>(SelectedTag(PlacementCombo), out var p) ? p : ResultPlacement.NearSelection;

    private void PlacementCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized && ZonePanel != null) UpdatePlacementPanel();
    }

    private void UpdatePlacementPanel()
    {
        var placement = SelectedPlacement;
        ZonePanel.Visibility = placement == ResultPlacement.CustomZone ? Visibility.Visible : Visibility.Collapsed;
        ZoneText.Text = _pendingZone == null
            ? "Зона не выбрана"
            : $"Зона {_pendingZone.Width}×{_pendingZone.Height}, {MonitorName(_pendingZone.MonitorDevice)}";

        PlacementHint.Text = placement switch
        {
            ResultPlacement.CustomZone =>
                "Окно всегда открывается в этой зоне и подстраивается под её ширину. В нижней половине экрана " +
                "окно прижимается к нижнему краю зоны. Если монитор с зоной отключён — окно откроется рядом с выделением.",
            ResultPlacement.LastPosition =>
                "Перетащите окно перевода за заголовок в удобное место — в следующий раз оно откроется там же.",
            ResultPlacement.ScreenCenter => "Окно открывается по центру монитора, на котором выделен текст.",
            _ => "Окно появляется под выделенной областью, а если места нет — над ней или сбоку."
        };
    }

    private static string MonitorName(string device)
    {
        var name = device.Replace(@"\\.\", "");
        return name.StartsWith("DISPLAY", StringComparison.OrdinalIgnoreCase) ? "монитор " + name[7..] : name;
    }

    private async void PickZoneButton_Click(object sender, RoutedEventArgs e)
    {
        // Прячем окно настроек, чтобы оно не закрывало экран при выборе зоны.
        Hide();
        try
        {
            await System.Threading.Tasks.Task.Delay(200);
            var zone = await _app.PickResultZoneAsync();
            if (zone != null) _pendingZone = zone;
        }
        finally
        {
            Show();
            Activate();
            UpdatePlacementPanel();
        }
    }

    private void OcrHelp_Click(object sender, RoutedEventArgs e) =>
        Dialogs.Info(this, OcrService.InstallInstructions);

    private void OpenHistory_Click(object sender, RoutedEventArgs e) => _app.ShowHistory();

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Confirm(this, "Удалить всю историю переводов? Это действие нельзя отменить.")) return;
        if (_app.History.Clear()) Dialogs.Info(this, "История очищена.");
        else Dialogs.Error(this, "Не удалось очистить историю. Подробности — в файле error.log.");
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(SettingsService.AppDataFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SettingsService.AppDataFolder}\"") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, "Не удалось открыть папку: " + ex.Message);
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        BrowserService.OpenUrl(this, e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var select = SelectHotkeyBox.Hotkey;
        var repeat = RepeatHotkeyBox.Hotkey;
        if (select is not { IsValid: true } || repeat is not { IsValid: true })
        {
            Dialogs.Warning(this, "Укажите обе горячие клавиши (сочетание с Ctrl, Alt, Shift или Win).");
            return;
        }
        if (select.SameAs(repeat))
        {
            Dialogs.Warning(this, "Горячие клавиши выделения и повтора должны различаться.");
            return;
        }

        var provider = SelectedProvider;
        var libreUrl = LibreTranslateProvider.NormalizeUrl(LibreUrlBox.Text);
        if (provider == TranslationProviderKind.LibreTranslate && !Uri.TryCreate(libreUrl, UriKind.Absolute, out _))
        {
            Dialogs.Warning(this, "Укажите корректный адрес сервера LibreTranslate, например https://libretranslate.com");
            return;
        }
        if (provider == TranslationProviderKind.Offline && !OfflineModelCatalog.Installed.Any() &&
            !OfflineModelCatalog.All.Any(m => _app.OfflineModels.IsDownloading(m.Id)))
        {
            if (Dialogs.Confirm(this, "Офлайн-модели ещё не установлены, поэтому перевод в окне работать не будет.\n\n" +
                                      $"Скачать рекомендуемые модели ({RecommendedLabel()}) сейчас?"))
                InstallRecommendedButton_Click(this, new RoutedEventArgs());
        }
        var placement = SelectedPlacement;
        if (placement == ResultPlacement.CustomZone && _pendingZone == null)
        {
            Dialogs.Warning(this, "Выберите зону экрана для окна перевода (кнопка «Выбрать зону на экране…») " +
                                  "или другой вариант размещения.");
            return;
        }

        _settings.SelectHotkey = select;
        _settings.ResultPlacement = placement;
        _settings.ResultZone = _pendingZone;
        _settings.OfflinePreferQuality = PreferQualityCheck.IsChecked == true;
        _settings.RepeatHotkey = repeat;
        _settings.Provider = provider;
        _settings.LibreUrl = libreUrl;
        _settings.LibreKeyProtected = SettingsService.Protect(LibreKeyBox.Password);

        if (BrowserCombo.SelectedItem is BrowserInfo browser)
        {
            _settings.BrowserId = browser.Id;
            _settings.BrowserName = browser.Name.Replace(" (не найден)", "");
            _settings.BrowserPath = browser.ExePath;
        }

        _settings.NativeLanguage = NativeLangCombo.SelectedValue as string ?? "ru";
        _settings.OcrLanguage = OcrLangCombo.SelectedValue as string ?? "auto";

        _settings.CloseOnDeactivate = CloseOnDeactivateCheck.IsChecked == true;
        _settings.AutoCopyReply = AutoCopyReplyCheck.IsChecked == true;
        _settings.HistoryEnabled = HistoryEnabledCheck.IsChecked == true;

        bool autoStart = AutoStartCheck.IsChecked == true;
        if (!AutoStartService.Apply(autoStart))
        {
            Dialogs.Warning(this, "Не удалось изменить автозапуск (нет доступа к реестру).");
            autoStart = !autoStart;
        }
        _settings.AutoStart = autoStart;

        _settings.FirstRunCompleted = true;
        SettingsService.Save();
        _app.ApplySettings();
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        _testCts?.Cancel();
        _app.OfflineModels.StateChanged -= OnModelStateChanged;
        if (_firstRun && !_settings.FirstRunCompleted)
        {
            // Пользователь закрыл окно без сохранения — работаем с настройками по умолчанию.
            _settings.FirstRunCompleted = true;
            SettingsService.Save();
        }
        _app.ResumeHotkeys();
    }
}
