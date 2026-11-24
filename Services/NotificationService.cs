using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.TaskScheduler;

namespace GigaCleaner.Services;

public sealed record NotificationResult(bool Ok, string? Error);

/// <summary>
/// Уведомление отправляет отдельный процесс без прав администратора:
/// Windows не показывает toast из elevated-приложения.
/// Notifier.exe извлекается из встроенного ресурса в %LocalAppData%\GigaCleaner\bin\
/// и запускается по этому стабильному пути (single-file не теряет его).
/// </summary>
public static class NotificationService
{
    public const string NotificationTaskName = "GigaCleaner Notification";

    private static readonly object Gate = new();
    private static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GigaCleaner");
    private static readonly string BinFolder = Path.Combine(Folder, "bin");
    private static readonly string NotifierPath = Path.Combine(BinFolder, "GigaCleaner.Notifier.exe");
    private static readonly string IconPath = Path.Combine(BinFolder, "app.ico");
    private static readonly string MessageFile = Path.Combine(Folder, "last-notification.txt");
    private static bool _taskEnsured;

    /// <summary>
    /// Гарантирует наличие GigaCleaner.Notifier.exe по стабильному пути.
    /// Извлекается из EmbeddedResource; если ресурса нет (dev-сборка) — падает на sidecar рядом с exe.
    /// Иконку для уведомлений раскладываем рядом с exe, чтобы Notifier показал её в toast.
    /// </summary>
    public static string EnsureDeployed()
    {
        lock (Gate)
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("GigaCleaner.Notifier.exe");
                if (stream is not null)
                {
                    Directory.CreateDirectory(BinFolder);
                    if (!File.Exists(NotifierPath) || new FileInfo(NotifierPath).Length != stream.Length)
                    {
                        using var output = File.Create(NotifierPath);
                        stream.CopyTo(output);
                    }
                    EnsureIcon();
                    return NotifierPath;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("Notifier deploy from embedded resource failed: " + ex);
            }

            var sidecar = Path.Combine(AppContext.BaseDirectory, "GigaCleaner.Notifier.exe");
            return File.Exists(sidecar) ? sidecar : NotifierPath;
        }
    }

    private static void EnsureIcon()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("GigaCleaner.Assets.app.ico");
            if (stream is null) return;
            if (File.Exists(IconPath) && new FileInfo(IconPath).Length == stream.Length) return;
            using var output = File.Create(IconPath);
            stream.CopyTo(output);
        }
        catch (Exception ex)
        {
            AppLog.Write("Notifier icon deploy failed: " + ex);
        }
    }

    public static NotificationResult Notify(string title, string body)
    {
        try
        {
            var notifier = EnsureDeployed();
            if (!File.Exists(notifier))
                return new NotificationResult(false, "Не найден GigaCleaner.Notifier.exe");

            Directory.CreateDirectory(Folder);
            File.WriteAllText(MessageFile, title + Environment.NewLine + body, new UTF8Encoding(false));
            EnsureTask(notifier);

            using var service = new TaskService();
            using var task = service.GetTask(NotificationTaskName);
            if (task is null)
                return new NotificationResult(false, "Задача уведомлений не зарегистрирована");

            task.Run();
            return new NotificationResult(true, null);
        }
        catch (Exception ex)
        {
            AppLog.Write("Notification failed: " + ex);
            return new NotificationResult(false, ex.Message);
        }
    }

    private static void EnsureTask(string notifierPath)
    {
        lock (Gate)
        {
            if (_taskEnsured) return;

            using var service = new TaskService();
            using var definition = BuildDefinition(service, notifierPath);
            using var registered = service.RootFolder.RegisterTaskDefinition(NotificationTaskName, definition);
            _taskEnsured = true;
        }
    }

    internal static TaskDefinition BuildDefinition(TaskService service, string notifierPath)
    {
        var definition = service.NewTask();
        definition.RegistrationInfo.Description = "GigaCleaner: показ уведомлений без прав администратора";
        definition.Principal.UserId = WindowsIdentity.GetCurrent().Name;
        definition.Principal.LogonType = TaskLogonType.InteractiveToken;
        definition.Principal.RunLevel = TaskRunLevel.LUA;

        definition.Settings.AllowDemandStart = true;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.ExecutionTimeLimit = TimeSpan.FromMinutes(5);
        definition.Settings.MultipleInstances = TaskInstancesPolicy.Parallel;
        definition.Settings.Enabled = true;

        definition.Actions.Add(new ExecAction(notifierPath,
            $"--message-file \"{MessageFile}\"", BinFolder));
        return definition;
    }
}
