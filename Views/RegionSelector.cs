using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ScreenTranslator.Models;
using ScreenTranslator.Native;
using ScreenTranslator.Services;

namespace ScreenTranslator.Views;

/// <summary>Показывает оверлеи на всех мониторах и ждёт, пока пользователь выделит область.</summary>
internal static class RegionSelector
{
    /// <returns>Область в физических пикселях или null, если выделение отменено.</returns>
    /// <param name="hint">Подсказка вверху экрана (по умолчанию — про выделение текста).</param>
    public static Task<PixelRect?> SelectAsync(CapturedScreen capture, string? hint = null)
    {
        var tcs = new TaskCompletionSource<PixelRect?>();
        var overlays = new List<SelectionOverlay>();
        bool finished = false;

        void Finish(PixelRect? result)
        {
            if (finished) return;
            finished = true;
            foreach (var overlay in overlays) overlay.Close();
            tcs.TrySetResult(result);
        }

        foreach (var monitor in capture.Monitors)
        {
            var overlay = new SelectionOverlay(monitor, capture.View(monitor.Bounds), hint);
            overlay.Selected += rect => Finish(rect);
            overlay.Cancelled += () => Finish(null);
            overlay.Closed += (_, _) =>
            {
                // Окно закрыто извне (например, Alt+F4) — считаем это отменой.
                Finish(null);
            };
            overlays.Add(overlay);
        }

        foreach (var overlay in overlays) overlay.Show();

        // Клавиатурный фокус (для Esc) получает оверлей под курсором.
        NativeMethods.GetCursorPos(out var cursor);
        var active = overlays.FirstOrDefault(o => o.Monitor.Bounds.Contains(cursor.X, cursor.Y)) ?? overlays.FirstOrDefault();
        if (active != null)
        {
            active.Activate();
            active.Focus();
        }

        return tcs.Task;
    }
}
