using System.Windows;
using GigaCleaner.Services;

namespace GigaCleaner;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var silent = e.Args.Any(argument =>
            string.Equals(argument, "--silent", StringComparison.OrdinalIgnoreCase));

        // Фоновый запуск не конкурирует за окно: очистка может идти параллельно с GUI.
        if (!silent)
        {
            if (!SingleInstance.TryAcquireGui())
            {
                SingleInstance.NotifyExistingAndExit();
                return;
            }

NotificationService.EnsureDeployed();
        StartGui();

        // Мягкая фоновая проверка новой версии при старте (не мешает работе).
        _ = SelfUpdateService.CheckQuietAsync();
        return;
        }

        // Извлекаем Notifier.exe по стабильному пути, чтобы уведомления работали и из single-file.
        NotificationService.EnsureDeployed();
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _ = RunSilentAsync();
    }

    private void StartGui()
    {
        // Захватываем управление сном и восстанавливаем процессы после возможного сбоя.
        _ = SleepService.Instance;
        SessionEnding += (_, _) => SleepService.Instance.ResumeAll();

        ThemeService.Initialize();
        GigaCleaner.Services.Localization.ApplySaved();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        SingleInstance.StartPipeListener(() =>
        {
            if (MainWindow is null) return;
            MainWindow.Show();
            if (MainWindow.WindowState == WindowState.Minimized)
                MainWindow.WindowState = WindowState.Normal;
            MainWindow.Activate();
            MainWindow.Topmost = true;
            MainWindow.Topmost = false;
            MainWindow.Focus();
        });

        if (!SettingsStore.AutostartPromptShown)
            GigaCleaner.Views.AutostartPromptWindow.ShowAndApply();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SleepService.Instance.Dispose();
        SingleInstance.Release();
        base.OnExit(e);
    }

    private static async Task RunSilentAsync()
    {
        try
        {
            await SilentRunner.RunAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write("Silent run failed: " + ex);
        }
        finally
        {
            Current.Shutdown();
        }
    }
}
