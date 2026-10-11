using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ScreenTranslator.Services.Offline;

namespace ScreenTranslator.Views;

/// <summary>Строка списка офлайн-моделей в настройках.</summary>
public sealed class OfflineModelItem : INotifyPropertyChanged
{
    private double _progress;
    private bool _isDownloading;
    private string? _error;

    public OfflineModelItem(OfflineModelInfo info)
    {
        Info = info;
    }

    public OfflineModelInfo Info { get; }

    public string Name => Info.DisplayName;

    public bool IsInstalled => Info.IsInstalled;

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        private set => Set(ref _isDownloading, value);
    }

    public string Status => _error != null
        ? "Ошибка: " + _error
        : IsDownloading
            ? $"Загрузка… {Progress:P0}"
            : IsInstalled ? "Установлена" : $"~{Info.SizeMb} МБ";

    public string ActionText => IsDownloading ? "Отмена" : IsInstalled ? "Удалить" : "Скачать";

    public Visibility ProgressVisibility => IsDownloading ? Visibility.Visible : Visibility.Collapsed;

    public void Apply(ModelDownloadState? state)
    {
        _error = state?.Error;
        IsDownloading = state?.IsDownloading == true;
        Progress = state?.Progress ?? 0;
        Refresh();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(ProgressVisibility));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
