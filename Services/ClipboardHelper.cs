using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using ScreenTranslator.Native;

namespace ScreenTranslator.Services;

public static class ClipboardHelper
{
    /// <summary>
    /// Копирует текст в буфер обмена. Буфер может быть временно занят другой программой,
    /// поэтому делаем несколько попыток.
    /// </summary>
    public static bool TrySetText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(60);
            }
            catch (ExternalException)
            {
                Thread.Sleep(60);
            }
        }
        return false;
    }

    /// <summary>Номер содержимого буфера после последней картинки — чтобы потом убрать именно её.</summary>
    private static uint _imageSequence;

    /// <summary>
    /// Копирует картинку в буфер обмена. Кроме обычного растра кладём PNG — его браузеры
    /// вставляют на сайты как файл изображения. Снимок экрана не должен нигде сохраняться,
    /// поэтому он помечается как исключённый из журнала буфера обмена Windows (Win+V) и синхронизации.
    /// </summary>
    public static bool TrySetImage(BitmapSource image)
    {
        var png = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(png);

        var data = new DataObject();
        data.SetImage(image);
        data.SetData("PNG", png);
        // Форматы, по которым Windows и менеджеры буфера обмена не сохраняют содержимое.
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                _imageSequence = NativeMethods.GetClipboardSequenceNumber();
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(60);
            }
            catch (ExternalException)
            {
                Thread.Sleep(60);
            }
        }
        return false;
    }

    /// <summary>
    /// Через указанное время убирает картинку из буфера обмена, если пользователь ничего
    /// не копировал после неё. Вызывать из потока интерфейса.
    /// </summary>
    public static async void ClearImageLater(TimeSpan delay)
    {
        uint sequence = _imageSequence;
        await Task.Delay(delay);
        if (sequence == 0 || NativeMethods.GetClipboardSequenceNumber() != sequence) return;
        try
        {
            Clipboard.Clear();
        }
        catch (ExternalException)
        {
            // буфер занят другой программой — не страшно
        }
    }
}
