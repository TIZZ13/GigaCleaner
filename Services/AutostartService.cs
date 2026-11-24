using System.IO;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using ScheduledTask = Microsoft.Win32.TaskScheduler.Task;

namespace GigaCleaner.Services;

public enum AutostartKind
{
    Registry,
    StartupFolder,
    ScheduledTask,
}

public sealed class AutostartEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public required string Source { get; init; }
    public AutostartKind Kind { get; init; }
    public bool Enabled { get; set; }
    public bool IsProtected { get; init; }
    public string? ExecutableName { get; init; }

    internal RegistryHive Hive { get; init; }
    internal string ApprovedPath { get; init; } = string.Empty;
    internal string ApprovedValueName { get; init; } = string.Empty;
    internal string? TaskPath { get; init; }
}

public sealed record AutostartToggleResult(bool Ok, string Message);

/// <summary>
/// Автозагрузка только отключается, ничего не удаляется:
/// реестр и папки — через StartupApproved, задачи — через Enabled.
/// </summary>
public static class AutostartService
{
    public static event System.Action? Changed;

    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    /// <summary>Включает или выключает автозапуск самого GigaCleaner через HKCU\...\Run.</summary>
    public static AutostartToggleResult SetSelfAutostart(bool enabled)
    {
        try
        {
            var exe = Environment.ProcessPath
                      ?? throw new InvalidOperationException("Не удалось определить путь к GigaCleaner.exe.");
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var run = baseKey.CreateSubKey(RunPath, true);
            if (run is null)
                return new AutostartToggleResult(false, "Не удалось открыть раздел автозагрузки.");

            const string valueName = "GigaCleaner";
            if (enabled)
            {
                run.SetValue(valueName, $"\"{exe}\"");
            }
            else
            {
                if (run.GetValue(valueName) is not null) run.DeleteValue(valueName, false);
            }

            using var approved = baseKey.CreateSubKey(ApprovedRoot + "Run", true);
            var data = new byte[12];
            if (!enabled)
            {
                data[0] = 0x03;
                BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            }
            else
            {
                data[0] = 0x02;
            }
            approved?.SetValue(valueName, data, RegistryValueKind.Binary);

            Changed?.Invoke();
            return new AutostartToggleResult(true,
                enabled ? "GigaCleaner добавлен в автозагрузку." : "GigaCleaner убран из автозагрузки.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or InvalidOperationException)
        {
            return new AutostartToggleResult(false, "Ошибка: " + ex.Message);
        }
    }

    public static IReadOnlyList<AutostartEntry> GetEntries()
    {
        var entries = new List<AutostartEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddRun(RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU · Run", false, entries, seen);
        AddRun(RegistryHive.CurrentUser, RegistryView.Registry32, "HKCU · Run (32 бита)", true, entries, seen);
        AddRun(RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM · Run", false, entries, seen);
        AddRun(RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM · Run (32 бита)", true, entries, seen);

        AddStartupFolder(Environment.SpecialFolder.Startup, RegistryHive.CurrentUser,
            "Автозагрузка пользователя", entries, seen);
        AddStartupFolder(Environment.SpecialFolder.CommonStartup, RegistryHive.LocalMachine,
            "Общая автозагрузка", entries, seen);

        AddScheduledTasks(entries, seen);

        return entries
            .OrderBy(entry => entry.Source, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static AutostartToggleResult SetEnabled(AutostartEntry entry, bool enabled)
    {
        if (entry.IsProtected)
            return new AutostartToggleResult(false, "Элемент защищён: системные и антивирусные записи не отключаются.");

        try
        {
            if (entry.Kind == AutostartKind.ScheduledTask)
            {
                if (string.IsNullOrWhiteSpace(entry.TaskPath))
                    return new AutostartToggleResult(false, "Не найден путь задачи.");

                using var service = new TaskService();
                using var task = service.GetTask(entry.TaskPath);
                if (task is null)
                    return new AutostartToggleResult(false, "Задача уже удалена.");

                task.Enabled = enabled;
                entry.Enabled = enabled;
                Changed?.Invoke();
                return new AutostartToggleResult(true, enabled ? "Задача включена." : "Задача отключена.");
            }

            if (string.IsNullOrWhiteSpace(entry.ApprovedPath) || string.IsNullOrWhiteSpace(entry.ApprovedValueName))
                return new AutostartToggleResult(false, "Нет данных StartupApproved для элемента.");

            using var baseKey = RegistryKey.OpenBaseKey(entry.Hive, RegistryView.Registry64);
            using var approved = baseKey.CreateSubKey(ApprovedRoot + entry.ApprovedPath, true);
            if (approved is null)
                return new AutostartToggleResult(false, "Не удалось открыть раздел StartupApproved.");

            var data = new byte[12];
            var existing = approved.GetValue(entry.ApprovedValueName) as byte[];
            if (!enabled && existing is { Length: 12 })
                Array.Copy(existing, data, 12);

            data[0] = enabled ? (byte)0x02 : (byte)0x03;
            if (enabled)
                Array.Clear(data, 1, data.Length - 1);
            else if (existing is not { Length: 12 })
                BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);

            approved.SetValue(entry.ApprovedValueName, data, RegistryValueKind.Binary);
            entry.Enabled = enabled;
            Changed?.Invoke();
            return new AutostartToggleResult(true,
                enabled ? "Автозапуск включён." : "Автозапуск отключён без удаления записи.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or InvalidOperationException)
        {
            return new AutostartToggleResult(false, "Ошибка: " + ex.Message);
        }
    }

    private static void AddRun(RegistryHive hive, RegistryView view, string source, bool is32Bit,
        List<AutostartEntry> entries, HashSet<string> seen)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var run = baseKey.OpenSubKey(RunPath);
            if (run is null) return;

            foreach (var valueName in run.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(valueName)) continue;
                if (run.GetValue(valueName) is not string command || string.IsNullOrWhiteSpace(command)) continue;

                var dedup = $"{hive}|{valueName}|{command}";
                if (!seen.Add(dedup)) continue;

                var approvedPath = is32Bit ? "Run32" : "Run";
                var exe = ExtractExecutable(command);
                entries.Add(new AutostartEntry
                {
                    Id = $"run:{hive}:{view}:{valueName}",
                    Name = valueName,
                    Detail = command,
                    Source = source,
                    Kind = AutostartKind.Registry,
                    Enabled = ReadApprovedEnabled(hive, ApprovedRoot + approvedPath, valueName),
                    IsProtected = IsProtectedAutostart(valueName, command, exe),
                    ExecutableName = exe is null ? null : Path.GetFileName(exe),
                    Hive = hive,
                    ApprovedPath = approvedPath,
                    ApprovedValueName = valueName,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Write($"Autostart registry read failed ({source}): {ex.Message}");
        }
    }

    private static void AddStartupFolder(Environment.SpecialFolder folder, RegistryHive hive, string source,
        List<AutostartEntry> entries, HashSet<string> seen)
    {
        try
        {
            var path = Environment.GetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

            foreach (var file in Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add($"folder|{path}|{fileName}")) continue;

                var exe = Path.GetFileNameWithoutExtension(fileName);
                entries.Add(new AutostartEntry
                {
                    Id = "folder:" + file,
                    Name = fileName,
                    Detail = file,
                    Source = source,
                    Kind = AutostartKind.StartupFolder,
                    Enabled = ReadApprovedEnabled(hive, ApprovedRoot + "StartupFolder", fileName),
                    IsProtected = IsProtectedAutostart(fileName, file, exe),
                    ExecutableName = exe,
                    Hive = hive,
                    ApprovedPath = "StartupFolder",
                    ApprovedValueName = fileName,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Write($"Autostart folder read failed ({source}): {ex.Message}");
        }
    }

    private static void AddScheduledTasks(List<AutostartEntry> entries, HashSet<string> seen)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var currentName = identity.Name;
            var currentSid = identity.User?.Value;

            using var service = new TaskService();
            foreach (var task in service.AllTasks)
            {
                using (task)
                {
                    try
                    {
                        var path = task.Path;
                        if (string.IsNullOrWhiteSpace(path)) continue;
                        if (path.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                        if (path.StartsWith(@"\GigaCleaner", StringComparison.OrdinalIgnoreCase)) continue;

                        using var definition = task.Definition;
                        var principal = definition.Principal;
                        if (!IsCurrentUser(principal.UserId, currentName, currentSid)) continue;
                        if (!definition.Triggers.OfType<LogonTrigger>().Any()) continue;

                        var exec = definition.Actions.OfType<ExecAction>().FirstOrDefault();
                        var detail = exec is null
                            ? "Задача без Exec-действия"
                            : (exec.Path + " " + exec.Arguments).Trim();
                        var exe = string.IsNullOrWhiteSpace(exec?.Path) ? null : Path.GetFileName(exec.Path);

                        if (!seen.Add("task|" + path)) continue;
                        entries.Add(new AutostartEntry
                        {
                            Id = "task:" + path,
                            Name = task.Name,
                            Detail = detail,
                            Source = "Планировщик заданий",
                            Kind = AutostartKind.ScheduledTask,
                            Enabled = task.Enabled,
                            IsProtected = IsProtectedAutostart(task.Name, detail, exe),
                            ExecutableName = exe,
                            TaskPath = path,
                        });
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write($"Autostart task read failed: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Autostart task enumeration failed: " + ex.Message);
        }
    }

    private static bool ReadApprovedEnabled(RegistryHive hive, string path, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path);
            if (key?.GetValue(valueName) is byte[] data && data.Length > 0)
                return (data[0] & 1) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
        return true;
    }

    private static bool IsCurrentUser(string? userId, string currentName, string? currentSid)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        if (string.Equals(userId, currentName, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrWhiteSpace(currentSid) &&
            string.Equals(userId, currentSid, StringComparison.OrdinalIgnoreCase)) return true;

        var shortName = currentName.Split('\\').LastOrDefault();
        return !string.IsNullOrWhiteSpace(shortName) &&
               userId.EndsWith("\\" + shortName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProtectedAutostart(string name, string detail, string? executable)
    {
        var file = Path.GetFileNameWithoutExtension(executable ?? name);
        if (ProcessGuard.IsProtectedName(file)) return true;
        if (file is "cmd" or "powershell" or "pwsh" or "wscript" or "cscript" or "rundll32") return true;

        if (string.IsNullOrWhiteSpace(detail)) return false;
        var commandExe = ExtractExecutable(detail);
        if (commandExe is not null &&
            (ProcessGuard.IsProtectedPath(commandExe) ||
             ProcessGuard.IsProtectedName(Path.GetFileNameWithoutExtension(commandExe))))
            return true;

        return detail.Contains(@"\Windows Defender\", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains("%windir%", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains("%systemroot%", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractExecutable(string command)
    {
        var text = command.Trim();
        if (text.Length == 0) return null;

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var space = text.IndexOf(' ');
        var first = space < 0 ? text : text[..space];
        if (first.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || first.Contains('\\'))
            return first;
        return null;
    }
}

