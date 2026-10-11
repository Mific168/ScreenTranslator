using System.Windows;

namespace ScreenTranslator.Services;

/// <summary>Стандартные диалоги с русскими заголовками.</summary>
public static class Dialogs
{
    private const string Caption = "Экранный переводчик";

    /// <summary>Сколько диалогов открыто сейчас (окно результата не закрывается, пока открыт диалог).</summary>
    public static int OpenCount { get; private set; }

    public static void Info(Window? owner, string text) =>
        Show(owner, text, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Warning(Window? owner, string text) =>
        Show(owner, text, MessageBoxButton.OK, MessageBoxImage.Warning);

    public static void Error(Window? owner, string text) =>
        Show(owner, text, MessageBoxButton.OK, MessageBoxImage.Error);

    public static bool Confirm(Window? owner, string text) =>
        Show(owner, text, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private static MessageBoxResult Show(Window? owner, string text, MessageBoxButton buttons, MessageBoxImage image)
    {
        OpenCount++;
        try
        {
            return owner is { IsVisible: true }
                ? MessageBox.Show(owner, text, Caption, buttons, image)
                : MessageBox.Show(text, Caption, buttons, image);
        }
        finally
        {
            OpenCount--;
        }
    }
}
