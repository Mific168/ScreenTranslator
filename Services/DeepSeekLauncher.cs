using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using ScreenTranslator.Native;

namespace ScreenTranslator.Services;

/// <summary>
/// Перевод через сайт DeepSeek (без API): открывает chat.deepseek.com в браузере и вставляет туда
/// запрос и снимок выделенной области, как если бы пользователь нажал Ctrl+V.
/// Пользователь должен быть заранее авторизован на сайте — иначе откроется страница входа.
/// </summary>
public static class DeepSeekLauncher
{
    public const string Url = "https://chat.deepseek.com/";

    public const string LoginNotice =
        "Для перевода через DeepSeek нужно быть авторизованным в сервисе: войдите в свой аккаунт " +
        "на chat.deepseek.com в браузере, который выбран в настройках переводчика.";

    /// <summary>Сколько ждать, пока вкладка DeepSeek станет активной.</summary>
    private static readonly TimeSpan WaitForWindow = TimeSpan.FromSeconds(20);

    /// <summary>Сколько картинка остаётся в буфере обмена — на случай ручной вставки (Ctrl+V).</summary>
    private static readonly TimeSpan ImageLifetime = TimeSpan.FromMinutes(2);

    private static bool _busy;

    public enum Outcome { NotStarted, Pasted, PasteManually }

    /// <param name="image">Снимок области; null — отправляется только текст (запись из истории).</param>
    public static async Task<Outcome> SendAsync(Window owner, BitmapSource? image, string text, string targetCode)
    {
        if (_busy) return Outcome.NotStarted;
        if (image == null && string.IsNullOrWhiteSpace(text))
        {
            Dialogs.Info(owner, "Нет текста для перевода.");
            return Outcome.NotStarted;
        }

        var settings = SettingsService.Current;
        if (!settings.DeepSeekNoticeShown)
        {
            var accepted = Dialogs.Confirm(owner,
                LoginNotice + "\n\n" +
                "Переводчик откроет DeepSeek в новой вкладке и сам вставит туда " +
                (image != null ? "картинку с выделенным текстом" : "текст") +
                " и просьбу перевести. Останется проверить и нажать Enter.\n\n" +
                "Если вы не вошли в аккаунт, вставка не сработает — войдите и нажмите Ctrl+V в поле сообщения.\n\n" +
                "Продолжить?");
            if (!accepted) return Outcome.NotStarted;
            settings.DeepSeekNoticeShown = true;
            SettingsService.Save();
        }

        string language = LanguageCatalog.NameOf(targetCode);
        string prompt = image != null
            ? $"Переведи текст с картинки. Язык перевода: {language}. В ответе дай только перевод."
            : $"Переведи текст. Язык перевода: {language}. В ответе дай только перевод.\n\n{text.Trim()}";

        // Сначала в буфере — то, что вставляется последним: если автовставка не сработает,
        // пользователь вставит картинку сам.
        if (!(image != null ? ClipboardHelper.TrySetImage(image) : ClipboardHelper.TrySetText(prompt)))
        {
            Dialogs.Error(owner, "Не удалось скопировать данные в буфер обмена.");
            return Outcome.NotStarted;
        }
        if (!BrowserService.OpenUrl(owner, Url))
        {
            if (image != null) ClipboardHelper.ClearImageLater(TimeSpan.Zero);
            return Outcome.NotStarted;
        }

        _busy = true;
        try
        {
            var window = await WaitForDeepSeekWindowAsync();
            if (window == IntPtr.Zero) return Outcome.PasteManually;

            // Страница загружается и ставит курсор в поле сообщения.
            await Task.Delay(2000);

            if (image != null)
            {
                // Запрос → картинка. В конце в буфере снова картинка — на случай ручной вставки.
                if (!ClipboardHelper.TrySetText(prompt) || !PasteInto(window)) return Restore(image);
                await Task.Delay(700);
                if (!ClipboardHelper.TrySetImage(image)) return Outcome.PasteManually;
                await Task.Delay(150);
                return PasteInto(window) ? Outcome.Pasted : Outcome.PasteManually;
            }

            return PasteInto(window) ? Outcome.Pasted : Outcome.PasteManually;
        }
        finally
        {
            _busy = false;
            // Снимок экрана не храним: если пользователь не скопировал ничего нового, убираем его из буфера.
            if (image != null) ClipboardHelper.ClearImageLater(ImageLifetime);
        }
    }

    private static Outcome Restore(BitmapSource image)
    {
        ClipboardHelper.TrySetImage(image);
        return Outcome.PasteManually;
    }

    private static async Task<IntPtr> WaitForDeepSeekWindowAsync()
    {
        var deadline = DateTime.Now + WaitForWindow;
        while (DateTime.Now < deadline)
        {
            var window = NativeMethods.GetForegroundWindow();
            if (IsDeepSeek(window)) return window;
            await Task.Delay(300);
        }
        return IntPtr.Zero;
    }

    /// <summary>Заголовок окна браузера содержит заголовок активной вкладки («DeepSeek - Into the Unknown»).</summary>
    private static bool IsDeepSeek(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        var title = new StringBuilder(512);
        NativeMethods.GetWindowText(window, title, title.Capacity);
        return title.ToString().Contains("DeepSeek", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ctrl+V — только если активна всё та же вкладка DeepSeek (пользователь мог переключиться).</summary>
    private static bool PasteInto(IntPtr window)
    {
        if (NativeMethods.GetForegroundWindow() != window || !IsDeepSeek(window)) return false;

        var inputs = new[]
        {
            Key(NativeMethods.VK_CONTROL, up: false),
            Key(NativeMethods.VK_V, up: false),
            Key(NativeMethods.VK_V, up: true),
            Key(NativeMethods.VK_CONTROL, up: true)
        };
        return NativeMethods.SendInput((uint)inputs.Length, inputs,
            System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>()) == inputs.Length;
    }

    private static NativeMethods.INPUT Key(ushort vk, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT { wVk = vk, dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0 }
        }
    };
}
