using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace GigaCleaner.Services;

public sealed record InstalledApp(
    string Name,
    string Publisher,
    string Version,
    string UninstallString,
    string? InstallLocation,
    long SizeBytes,
    DateTime? InstallDate);

/// <summary>Читает записи деинсталляции для списка установленных программ. Ничего не удаляет.</summary>
public static class InstalledProgramsService
{
    public static IReadOnlyList<InstalledApp> Scan(CancellationToken ct = default)
    {
        var result = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hives = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };

        foreach (var hive in hives)
        {
            foreach (var view in views)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = baseKey.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall is null) continue;

                    foreach (var subName in uninstall.GetSubKeyNames())
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            using var key = uninstall.OpenSubKey(subName);
                            if (key is null) continue;

                            var name = (key.GetValue("DisplayName") as string)?.Trim();
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            if (IsSystemComponent(key.GetValue("SystemComponent"))) continue;
                            if (!string.IsNullOrWhiteSpace(key.GetValue("ParentKeyName") as string)) continue;

                            var release = key.GetValue("ReleaseType") as string;
                            if (release is not null &&
                                (release.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
                                 release.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)))
                                continue;

                            var uninstallString =
                                (key.GetValue("UninstallString") as string)?.Trim();
                            if (string.IsNullOrWhiteSpace(uninstallString))
                                uninstallString = (key.GetValue("QuietUninstallString") as string)?.Trim();
                            if (string.IsNullOrWhiteSpace(uninstallString)) continue;

                            var publisher = (key.GetValue("Publisher") as string)?.Trim() ?? "";
                            var version = (key.GetValue("DisplayVersion") as string)?.Trim() ?? "";
                            var location = (key.GetValue("InstallLocation") as string)?.Trim();
                            var size = Math.Max(0, GetLong(key.GetValue("EstimatedSize"))) * 1024;
                            var date = ParseInstallDate(key.GetValue("InstallDate") as string);

                            var dedup = name + "|" + uninstallString;
                            if (!seen.Add(dedup)) continue;

                            result.Add(new InstalledApp(
                                name, publisher, version, uninstallString, location, size, date));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                                       or System.Security.SecurityException or InvalidCastException
                                                       or FormatException)
                        {
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                               or System.Security.SecurityException)
                {
                    AppLog.Write($"Uninstall registry read failed ({hive}/{view}): {ex.Message}");
                }
            }
        }

        return result
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool IsSystemComponent(object? value)
    {
        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static long GetLong(object? value)
    {
        try
        {
            return value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static DateTime? ParseInstallDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date
            : null;
    }
}

public sealed record DiskFolder(string Path, string Name, long Bytes, int Files);

public sealed record DiskScanResult(
    IReadOnlyList<DiskFolder> Folders,
    long TotalBytes,
    int TotalFiles);

/// <summary>Считает размеры папок первого уровня. Только чтение, без удаления файлов.</summary>
public static class DiskAnalyzerService
{
    private static readonly System.IO.EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        RecurseSubdirectories = false,
    };

    public static DiskScanResult Analyze(
        string root,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var folders = new List<DiskFolder>();
        var directories = new List<string>();

        try
        {
            directories.AddRange(Directory.EnumerateDirectories(root, "*", Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        for (var index = 0; index < directories.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var directory = directories[index];
            var name = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(name)) name = directory;
            progress?.Report($"{name} · {index + 1}/{directories.Count}");

            var (bytes, files) = Measure(directory, ct);
            folders.Add(new DiskFolder(directory, name, bytes, files));
        }

        var (rootBytes, rootFiles) = MeasureFiles(root, ct);
        if (rootFiles > 0)
            folders.Add(new DiskFolder(root, "Файлы в корне", rootBytes, rootFiles));

        folders.Sort((left, right) => right.Bytes.CompareTo(left.Bytes));
        return new DiskScanResult(
            folders,
            folders.Sum(item => item.Bytes),
            folders.Sum(item => item.Files));
    }

    private static (long Bytes, int Files) Measure(string root, CancellationToken ct)
    {
        long bytes = 0;
        var files = 0;
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = stack.Pop();
            var (folderBytes, folderFiles) = MeasureFiles(current, ct);
            bytes += folderBytes;
            files += folderFiles;

            try
            {
                foreach (var child in Directory.EnumerateDirectories(current, "*", Options))
                    stack.Push(child);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return (bytes, files);
    }

    private static (long Bytes, int Files) MeasureFiles(string folder, CancellationToken ct)
    {
        long bytes = 0;
        var files = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", Options))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    bytes += new FileInfo(file).Length;
                    files++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return (bytes, files);
    }
}

public sealed record DuplicateGroup(
    string Hash,
    long Size,
    IReadOnlyList<string> Files)
{
    public long WastedBytes => Size * Math.Max(0, Files.Count - 1);
}

public sealed record DuplicateScanResult(
    IReadOnlyList<DuplicateGroup> Groups,
    int ScannedFiles,
    bool Truncated);

/// <summary>Ищет совпадения по размеру и SHA-256. Ничего не удаляет.</summary>
public static class DuplicateFinderService
{
    private const int MaxFiles = 20000;

    private static readonly System.IO.EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        RecurseSubdirectories = true,
    };

    public static DuplicateScanResult Find(
        string root,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var byLength = new Dictionary<long, List<string>>();
        var scanned = 0;
        var truncated = false;

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", Options))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var length = new FileInfo(file).Length;
                    if (length == 0) continue;
                    if (!byLength.TryGetValue(length, out var list))
                        byLength[length] = list = [];
                    list.Add(file);
                    scanned++;
                    if (scanned >= MaxFiles)
                    {
                        truncated = true;
                        break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }

                if (scanned % 250 == 0)
                    progress?.Report($"Проверено файлов: {scanned}…");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var groups = new List<DuplicateGroup>();
        var hashed = 0;
        foreach (var (length, files) in byLength.Where(pair => pair.Value.Count > 1))
        {
            var byHash = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var stream = File.OpenRead(file);
                    var hash = Convert.ToHexString(SHA256.HashData(stream));
                    if (!byHash.TryGetValue(hash, out var list))
                        byHash[hash] = list = [];
                    list.Add(file);
                    hashed++;
                    if (hashed % 25 == 0)
                        progress?.Report($"Сверяю содержимое: {hashed}…");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            foreach (var (hash, sameFiles) in byHash.Where(pair => pair.Value.Count > 1))
                groups.Add(new DuplicateGroup(hash, length, sameFiles));
        }

        groups.Sort((left, right) => right.WastedBytes.CompareTo(left.WastedBytes));
        return new DuplicateScanResult(groups, scanned, truncated);
    }
}

public sealed record RestorePointInfo(
    string Description,
    DateTime CreatedAt,
    uint SequenceNumber,
    string Type);

public sealed record RestorePointScanResult(
    IReadOnlyList<RestorePointInfo> Points,
    string Message);

/// <summary>Читает и создаёт штатные точки восстановления Windows. Требуются права администратора.</summary>
public static class RestorePointService
{
    private static ManagementScope Scope => new(@"root\default");

    public static RestorePointScanResult List()
    {
        try
        {
            using var managementClass = new ManagementClass(
                Scope, new ManagementPath("SystemRestore"), null);
            using var instances = managementClass.GetInstances();
            var points = new List<RestorePointInfo>();

            foreach (ManagementObject item in instances)
            {
                using (item)
                {
                    var description = item["Description"] as string ?? "Без описания";
                    var type = Convert.ToUInt32(item["RestorePointType"] ?? 0);
                    var sequence = Convert.ToUInt32(item["SequenceNumber"] ?? 0);
                    points.Add(new RestorePointInfo(
                        description,
                        ToDateTime(item["CreationTime"]),
                        sequence,
                        DescribeType(type)));
                }
            }

            points.Sort((left, right) => right.CreatedAt.CompareTo(left.CreatedAt));
            var message = points.Count == 0
                ? "Точки не найдены. Защита системы может быть отключена."
                : $"Точек восстановления: {points.Count}.";
            return new RestorePointScanResult(points, message);
        }
        catch (Exception ex)
        {
            return new RestorePointScanResult([], "Не удалось прочитать точки: " + ex.Message);
        }
    }

    public static (bool Ok, string Message) Create(string description)
    {
        try
        {
            using var managementClass = new ManagementClass(
                Scope, new ManagementPath("SystemRestore"), null);
            using var parameters = managementClass.GetMethodParameters("CreateRestorePoint");
            parameters["Description"] = string.IsNullOrWhiteSpace(description)
                ? "GigaCleaner: контрольная точка"
                : description;
            parameters["RestorePointType"] = 0;
            parameters["EventType"] = 100;

            using var result = managementClass.InvokeMethod("CreateRestorePoint", parameters, null);
            var code = Convert.ToUInt32(result?["ReturnValue"] ?? 1);
            return code == 0
                ? (true, "Точка восстановления создана.")
                : (false, "Windows вернул код ошибки " + code + ".");
        }
        catch (Exception ex)
        {
            return (false, "Не удалось создать точку: " + ex.Message);
        }
    }

    public static (bool Ok, string Message) OpenProtection()
        => ToolsLauncher.Open("SystemPropertiesProtection.exe");

    private static DateTime ToDateTime(object? value)
    {
        try
        {
            return value switch
            {
                DateTime date => date,
                string text => ManagementDateTimeConverter.ToDateTime(text),
                _ => DateTime.MinValue,
            };
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }

    private static string DescribeType(uint type) => type switch
    {
        0 => "Установка приложения",
        1 => "Удаление приложения",
        2 => "Изменение системы",
        10 => "Установка драйвера",
        12 => "Восстановление",
        13 => "Отменено",
        14 => "Контрольная точка",
        _ => "Тип " + type,
    };
}

public static class ToolsLauncher
{
    public static (bool Ok, string Message) Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, "Не удалось открыть: " + ex.Message);
        }
    }

    public static (bool Ok, string Message) OpenFolder(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"")
            {
                UseShellExecute = true,
            });
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, "Не удалось открыть папку: " + ex.Message);
        }
    }

    public static (bool Ok, string Message) SelectFile(string file)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"")
            {
                UseShellExecute = true,
            });
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, "Не удалось открыть файл: " + ex.Message);
        }
    }
}
