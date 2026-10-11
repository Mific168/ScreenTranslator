using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using ScreenTranslator.Models;
using ScreenTranslator.Services;

namespace ScreenTranslator.Views;

/// <summary>Окно истории: поиск, копирование и повторное открытие записей.</summary>
public partial class HistoryWindow : Window
{
    private readonly AppController _app = AppController.Instance;
    private List<HistoryEntry> _all = new();

    public HistoryWindow()
    {
        InitializeComponent();
        Icon = IconFactory.WindowIcon;
        SourceInitialized += (_, _) => ThemeService.ApplyWindowFrame(this);
        Loaded += async (_, _) =>
        {
            await ReloadAsync();
            SearchBox.Focus();
        };
        _app.History.Changed += OnHistoryChanged;
        Closed += (_, _) => _app.History.Changed -= OnHistoryChanged;
        UpdateButtons();
    }

    private async void OnHistoryChanged()
    {
        try
        {
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка обновления истории", ex);
        }
    }

    private async System.Threading.Tasks.Task ReloadAsync()
    {
        _all = await _app.History.GetAllAsync();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var items = query.Length == 0 ? _all : _all.Where(e => e.Matches(query)).ToList();
        EntriesList.ItemsSource = items;

        if (items.Count == 0)
        {
            EmptyText.Text = !SettingsService.Current.HistoryEnabled && _all.Count == 0
                ? "История выключена в настройках."
                : _all.Count == 0
                    ? "История пуста.\nЗдесь появятся последние 50 переводов."
                    : "Ничего не найдено.";
            EmptyText.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyText.Visibility = Visibility.Collapsed;
        }
        UpdateButtons();
    }

    private HistoryEntry? Selected => EntriesList.SelectedItem as HistoryEntry;

    private void UpdateButtons()
    {
        bool has = Selected != null;
        OpenButton.IsEnabled = has;
        CopySourceButton.IsEnabled = has;
        CopyTranslationButton.IsEnabled = has && !string.IsNullOrEmpty(Selected!.TranslatedText);
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilter();

    private void EntriesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateButtons();

    private void EntriesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected != null) OpenSelected();
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (Selected is { } entry) _app.OpenHistoryEntry(entry);
    }

    private void CopySourceButton_Click(object sender, RoutedEventArgs e) => Copy(Selected?.SourceText);

    private void CopyTranslationButton_Click(object sender, RoutedEventArgs e) => Copy(Selected?.TranslatedText);

    private void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        StatusText.Text = ClipboardHelper.TrySetText(text) ? "Скопировано" : "Буфер обмена занят";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (e.Key == Key.Escape)
        {
            Close();
        }
        else if (e.Key == Key.Enter && Selected != null && !SearchBox.IsKeyboardFocused)
        {
            OpenSelected();
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && EntriesList.IsKeyboardFocusWithin)
        {
            Copy(string.IsNullOrEmpty(Selected?.TranslatedText) ? Selected?.SourceText : Selected?.TranslatedText);
        }
    }
}
