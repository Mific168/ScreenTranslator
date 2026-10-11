using System;
using System.Diagnostics;
using ScreenTranslator.Native;

namespace ScreenTranslator.Services;

/// <summary>Освобождение памяти, когда приложение уходит в фон.</summary>
public static class MemoryHelper
{
    public static void Trim()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var process = Process.GetCurrentProcess();
            // Отдаём неиспользуемые страницы рабочего набора системе.
            NativeMethods.SetProcessWorkingSetSize(process.Handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch
        {
            // не критично
        }
    }
}
