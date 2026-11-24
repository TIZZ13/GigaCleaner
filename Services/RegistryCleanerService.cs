using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace GigaCleaner.Services;

public sealed class RegistryIssue
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required RegistryHive Hive { get; init; }
    public required RegistryView View { get; init; }
    public required string ParentPath { get; init; }
    public required string TargetName { get; init; }
    public required bool IsKey { get; init; }
    public string TargetValueName { get; init; } = string.Empty;

    public string FullKeyPath =>
        (Hive == RegistryHive.CurrentUser ? "HKEY_CURRENT_USER\\" : "HKEY_LOCAL_MACHINE\\") +
        ParentPath + "\\" + TargetName;
}

public sealed record RegistryRepairResult(
    bool Ok,
    string Message,
    string? BackupDirectory,
    int Repaired,
    int Failed);

public static class RegistryCleanerService
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string AppPathsPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths";

    private static readonly HashSet<string> SystemHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "msiexec.exe", "rundll32.exe", "regsvr32.exe", "cmd.exe", "powershell.exe",
        "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "explorer.exe",
        "control.exe", "dllhost.exe",
    };

    private static readonly object Gate = new();

    public static IReadOnlyList<RegistryIssue> Scan()
    {
        lock (Gate)
        {
            var result = new List<RegistryIssue>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            ScanUninstall(RegistryHive.CurrentUser, RegistryView.Registry64, result, seen);
            ScanUninstall(RegistryHive.CurrentUser, RegistryView.Registry32, result, seen);
            ScanUninstall(RegistryHive.LocalMachine, RegistryView.Registry64, result, seen);
            ScanUninstall(RegistryHive.LocalMachine, RegistryView.Registry32, result, seen);

            ScanAppPaths(RegistryHive.CurrentUser, RegistryView.Registry64, result, seen);
            ScanAppPaths(RegistryHive.CurrentUser, RegistryView.Registry32, result, seen);
            ScanAppPaths(RegistryHive.LocalMachine, RegistryView.Registry64, result, seen);
            ScanAppPaths(RegistryHive.LocalMachine, RegistryView.Registry32, result, seen);

            return result
                .OrderBy(item => item.Category, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    public static RegistryRepairResult Repair(IReadOnlyList<RegistryIssue> issues)
    {
        lock (Gate)
        {
            var selected = issues
                .Where(IsAllowedIssue)
                .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (selected.Count == 0)
                return new RegistryRepairResult(false, "Не выбрано ни одной безопасной записи.", null, 0, 0);

            var backupRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GigaCleaner", "Backups", "Registry");
            var backupDirectory = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            var exports = selected
                .GroupBy(item => (item.Hive, item.View, item.ParentPath))
                .ToList();

            try
            {
                Directory.CreateDirectory(backupDirectory);
                var index = 1;
                foreach (var group in exports)
                {
                    var file = Path.Combine(backupDirectory, $"backup-{index:00}.reg");
                    var export = ExportKey(group.Key.Hive, group.Key.View, group.Key.ParentPath, file);
                    if (!export.Ok)
                        return new RegistryRepairResult(false,
                            "Не удалось создать .reg-бэкап. Исправление отменено: " + export.Error,
                            backupDirectory, 0, 0);
                    if (!File.Exists(file) || new FileInfo(file).Length == 0)
                        return new RegistryRepairResult(false,
                            "Бэкап пустой. Исправление отменено.", backupDirectory, 0, 0);
                    index++;
                }

                var repaired = 0;
                var failed = 0;
                foreach (var issue in selected)
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(issue.Hive, issue.View);
                        using var parent = baseKey.OpenSubKey(issue.ParentPath, writable: true);
                        if (parent is null)
                        {
                            failed++;
                            continue;
                        }

                        if (issue.IsKey)
                        {
                            parent.DeleteSubKeyTree(issue.TargetName, throwOnMissingSubKey: false);
                        }
                        else
                        {
                            parent.DeleteValue(issue.TargetValueName, throwOnMissingValue: false);
                        }
                        repaired++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        AppLog.Write($"Registry repair failed ({issue.Id}): {ex.Message}");
                    }
                }

                var message = failed == 0
                    ? $"Исправлено записей: {repaired}. Бэкап: {backupDirectory}"
                    : $"Исправлено: {repaired}, не удалось: {failed}. Бэкап: {backupDirectory}";
                AppLog.Write($"Registry cleanup: repaired={repaired}, failed={failed}, backup={backupDirectory}");
                return new RegistryRepairResult(failed == 0, message, backupDirectory, repaired, failed);
            }
            catch (Exception ex)
            {
                return new RegistryRepairResult(false,
                    "Не удалось выполнить исправление: " + ex.Message, backupDirectory, 0, 0);
            }
        }
    }

    private static void ScanUninstall(RegistryHive hive, RegistryView view,
        List<RegistryIssue> result, HashSet<string> seen)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(UninstallPath);
            if (root is null) return;

            foreach (var subName in root.GetSubKeyNames())
            {
                try
                {
                    using var key = root.OpenSubKey(subName);
                    if (key is null) continue;

                    var displayName = (key.GetValue("DisplayName") as string)?.Trim();
                    if (string.IsNullOrWhiteSpace(displayName)) continue;
                    if (Convert.ToInt32(key.GetValue("SystemComponent") ?? 0) == 1) continue;
                    if (!string.IsNullOrWhiteSpace(key.GetValue("ParentKeyName") as string)) continue;

                    var releaseType = key.GetValue("ReleaseType") as string;
                    if (releaseType is not null &&
                        (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
                         releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var uninstallString = (key.GetValue("UninstallString") as string)?.Trim();
                    if (string.IsNullOrWhiteSpace(uninstallString)) continue;

                    var target = ExtractExecutable(uninstallString);
                    if (target is null || IsSystemTarget(target)) continue;
                    if (File.Exists(target)) continue;

                    var installLocation = Expand((key.GetValue("InstallLocation") as string)?.Trim());
                    if (!string.IsNullOrWhiteSpace(installLocation) &&
                        (Directory.Exists(installLocation) || IsSystemTarget(installLocation)))
                        continue;

                    var displayIcon = CleanIconPath(key.GetValue("DisplayIcon") as string);
                    if (displayIcon is not null && File.Exists(displayIcon)) continue;

                    var seenKey = hive == RegistryHive.CurrentUser
                        ? $"uninstall|{subName}"
                        : $"uninstall|{hive}|{view}|{subName}";
                    if (!seen.Add(seenKey)) continue;

                    result.Add(new RegistryIssue
                    {
                        Id = $"uninstall:{hive}:{view}:{subName}",
                        Category = "Деинсталляция",
                        Title = displayName,
                        Detail = $"Не найден файл деинсталлятора: {target}",
                        Hive = hive,
                        View = view,
                        ParentPath = UninstallPath,
                        TargetName = subName,
                        IsKey = true,
                    });
                }
                catch (Exception ex)
                {
                    AppLog.Write($"Registry uninstall scan failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Registry uninstall root failed ({hive}/{view}): {ex.Message}");
        }
    }

    private static void ScanAppPaths(RegistryHive hive, RegistryView view,
        List<RegistryIssue> result, HashSet<string> seen)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(AppPathsPath);
            if (root is null) return;

            foreach (var subName in root.GetSubKeyNames())
            {
                try
                {
                    using var key = root.OpenSubKey(subName);
                    if (key is null) continue;

                    var value = Expand((key.GetValue(string.Empty) as string)?.Trim());
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    if (!Path.IsPathRooted(value)) continue;
                    if (IsSystemTarget(value) || File.Exists(value)) continue;

                    var seenKey = hive == RegistryHive.CurrentUser
                        ? $"apppath|{subName}"
                        : $"apppath|{hive}|{view}|{subName}";
                    if (!seen.Add(seenKey)) continue;

                    result.Add(new RegistryIssue
                    {
                        Id = $"apppath:{hive}:{view}:{subName}",
                        Category = "App Paths",
                        Title = subName,
                        Detail = $"Не найден файл: {value}",
                        Hive = hive,
                        View = view,
                        ParentPath = AppPathsPath,
                        TargetName = subName,
                        IsKey = true,
                    });
                }
                catch (Exception ex)
                {
                    AppLog.Write($"Registry App Paths scan failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Registry App Paths root failed ({hive}/{view}): {ex.Message}");
        }
    }

    private static string? ExtractExecutable(string command)
    {
        var text = Expand(command);
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? Expand(text[1..end]) : null;
        }

        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return Expand(text[..(exe + 4)].Trim());

        var first = text.Split(' ', 2)[0];
        return first.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Expand(first) : null;
    }

    private static string Expand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try { return Environment.ExpandEnvironmentVariables(value.Trim()); }
        catch (Exception) { return value.Trim(); }
    }

    private static string? CleanIconPath(string? value)
    {
        var text = Expand(value);
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            if (end <= 1) return null;
            text = text[1..end];
        }
        else
        {
            var comma = text.IndexOf(',');
            if (comma > 0) text = text[..comma];
        }

        text = text.Trim();
        if (!Path.IsPathRooted(text)) return null;
        try { return Path.GetFullPath(text); }
        catch (Exception) { return null; }
    }

    private static bool IsSystemTarget(string target)
    {
        try
        {
            var name = Path.GetFileName(target);
            if (SystemHosts.Contains(name)) return true;
            if (!Path.IsPathRooted(target)) return true;
            if (target.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return true;

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var full = Path.GetFullPath(target);
            return full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   full.Equals(windows, StringComparison.OrdinalIgnoreCase) ||
                   full.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static bool IsAllowedIssue(RegistryIssue issue)
    {
        if (string.IsNullOrWhiteSpace(issue.TargetName)) return false;
        if (issue.Hive is not (RegistryHive.CurrentUser or RegistryHive.LocalMachine)) return false;
        if (issue.View is not (RegistryView.Registry64 or RegistryView.Registry32)) return false;
        if (issue.ParentPath is not (UninstallPath or AppPathsPath)) return false;
        if (issue.IsKey)
            return issue.TargetName.IndexOfAny(['\\', '/', ':', '"']) < 0;
        return issue.TargetValueName.Length == 0;
    }

    private static (bool Ok, string Error) ExportKey(
        RegistryHive hive, RegistryView view, string parentPath, string file)
    {
        var root = hive == RegistryHive.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";
        var full = root + "\\" + parentPath;
        var args = new List<string> { "export", full, file, "/y" };
        if (view == RegistryView.Registry32) args.Add("/reg:32");
        else args.Add("/reg:64");

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "reg.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in args) info.ArgumentList.Add(arg);

            using var process = Process.Start(info);
            if (process is null) return (false, "reg.exe не запустился.");
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(true); } catch (Exception) { }
                return (false, "reg.exe не завершился за 15 секунд.");
            }
            return (process.ExitCode == 0, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
