using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ScreenTranslator.Services;

namespace ScreenTranslator;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            RunSelfTest();
            return;
        }

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\ScreenTranslator.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Экранный переводчик уже запущен.\nЗначок приложения находится в области уведомлений (рядом с часами).",
                "Экранный переводчик", MessageBoxButton.OK, MessageBoxImage.Information);
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Критическая ошибка", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Ошибка фоновой задачи", args.Exception);
            args.SetObserved();
        };

        SettingsService.Load();
        ThemeService.Initialize();

        bool silent = e.Args.Any(a => string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase));

        // Параметры, которые передаёт установщик: --install-models en-ru,ru-en --native ru
        var models = ArgValue(e.Args, "--install-models")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var native = ArgValue(e.Args, "--native");

        AppController.Instance.Start(silent, models, native);
    }

    /// <summary>
    /// Диагностика без интерфейса: офлайн-перевод тестовой фразы установленными моделями.
    /// Результат — в %AppData%\ScreenTranslator\selftest.txt.
    /// </summary>
    private async void RunSelfTest()
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine($"Экранный переводчик, самопроверка {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        try
        {
            SettingsService.Load();
            var installed = Services.Offline.OfflineModelCatalog.Installed.ToList();
            report.AppendLine("Модели: " + (installed.Count == 0 ? "нет" : string.Join(", ", installed.Select(m => m.Id))));

            var manager = new Services.Offline.OfflineModelManager();
            var provider = new Services.Translation.OfflineProvider(manager);
            foreach (var model in installed)
            {
                if (model.IsMultilingual)
                {
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    var steps = new[] { new Services.Offline.TranslationStep(model, "eng_Latn", "rus_Cyrl") };
                    var text = await manager.TranslateAsync("Hello, how are you?", steps, CancellationToken.None);
                    report.AppendLine($"{model.Id} (en→ru): {text} ({timer.ElapsedMilliseconds} мс)");
                    continue;
                }

                var sample = model.Source switch
                {
                    "ru" => "Привет, как дела?",
                    "en" => "Hello, how are you?",
                    _ => null
                };
                if (sample == null) continue;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await Services.Translation.TranslationService.TranslateWithAsync(
                    provider, sample, model.Source, model.Target, CancellationToken.None);
                report.AppendLine($"{model.Id}: {(result.Success ? result.Text : "ОШИБКА " + result.ErrorMessage)} ({sw.ElapsedMilliseconds} мс)");
            }
            manager.UnloadAll();
            report.AppendLine("OK");
        }
        catch (Exception ex)
        {
            report.AppendLine("ИСКЛЮЧЕНИЕ: " + ex);
        }

        try
        {
            System.IO.Directory.CreateDirectory(SettingsService.AppDataFolder);
            System.IO.File.WriteAllText(System.IO.Path.Combine(SettingsService.AppDataFolder, "selftest.txt"),
                report.ToString(), System.Text.Encoding.UTF8);
        }
        catch
        {
            // некуда сообщить
        }
        Shutdown();
    }

    private static string? ArgValue(string[] args, string name)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Приложение не должно падать из-за ошибки в одном сценарии.
        Log.Error("Необработанная ошибка интерфейса", e.Exception);
        e.Handled = true;
        try
        {
            Dialogs.Error(null, "Произошла ошибка: " + e.Exception.Message +
                                "\n\nПодробности записаны в %AppData%\\ScreenTranslator\\error.log");
        }
        catch
        {
            // игнорируем: показать сообщение тоже не удалось
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstance != null)
        {
            AppController.Instance.Dispose();
            _singleInstance.ReleaseMutex();
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }
}
