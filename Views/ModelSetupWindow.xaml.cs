using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ScreenTranslator.Services;
using ScreenTranslator.Services.Offline;

namespace ScreenTranslator.Views;

/// <summary>
/// Окно загрузки офлайн-моделей при первом запуске после установки
/// (ScreenTranslator.exe --install-models en-ru,ru-en).
/// </summary>
public partial class ModelSetupWindow : Window
{
    private readonly AppController _app = AppController.Instance;
    private readonly List<OfflineModelItem> _items;

    public ModelSetupWindow(IEnumerable<OfflineModelInfo> models)
    {
        InitializeComponent();
        Icon = IconFactory.WindowIcon;
        SourceInitialized += (_, _) => ThemeService.ApplyWindowFrame(this);

        _items = models.Select(m => new OfflineModelItem(m)).ToList();
        ModelsList.ItemsSource = _items;

        _app.OfflineModels.StateChanged += OnStateChanged;
        Closed += (_, _) => _app.OfflineModels.StateChanged -= OnStateChanged;
        Loaded += async (_, _) => await DownloadAllAsync();
    }

    private async Task DownloadAllAsync()
    {
        RetryButton.Visibility = Visibility.Collapsed;
        ResultText.Visibility = Visibility.Collapsed;
        CloseButton.Content = "Скрыть";

        // Модели скачиваются по очереди — так быстрее появляется первая рабочая пара.
        foreach (var item in _items.Where(i => !i.IsInstalled))
            await _app.OfflineModels.DownloadAsync(item.Info);

        foreach (var item in _items) item.Refresh();
        bool ok = _items.All(i => i.IsInstalled);

        if (!IsVisible)
        {
            // Окно скрыли до окончания загрузки — сообщаем итог уведомлением в трее.
            _app.NotifyModelSetupFinished(ok);
            Close();
            return;
        }

        ResultText.Visibility = Visibility.Visible;
        if (ok)
        {
            ResultText.Text = $"Готово! Нажмите {SettingsService.Current.SelectHotkey} и выделите текст на экране — " +
                              "перевод появится без интернета.";
            CloseButton.Content = "Начать";
        }
        else
        {
            ResultText.Text = "Не все модели удалось скачать. Проверьте подключение к интернету и нажмите «Повторить». " +
                              "Модели также можно установить позже: Настройки → Перевод в окне.";
            RetryButton.Visibility = Visibility.Visible;
            CloseButton.Content = "Закрыть";
        }
    }

    private void OnStateChanged(ModelDownloadState state) =>
        _items.FirstOrDefault(i => i.Info.Id == state.ModelId)?.Apply(state);

    private async void RetryButton_Click(object sender, RoutedEventArgs e) => await DownloadAllAsync();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // Пока идёт загрузка — только прячем окно, иначе закрываем.
        if (_items.Any(i => _app.OfflineModels.IsDownloading(i.Info.Id))) Hide();
        else Close();
    }
}
