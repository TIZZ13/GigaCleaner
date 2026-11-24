using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace GigaCleaner.Services;

public sealed record InstalledProgram(
    string Name,
    string? InstallLocation,
    string? UninstallString,
    long SizeBytes,
    DateTime? InstallDate,
    IReadOnlyList<string> ExecutableNames);

public sealed record ProgramRecommendation(
    InstalledProgram Program,
    DateTime LastRun,
    DateTime? LastAccess,
    AutostartEntry? Autostart);

public sealed record ProcessRecommendation(
    UserProcessInfo Process,
    AutostartEntry? Autostart,
    InstalledProgram? Program);

public sealed class AnalysisReport
{
    public required IReadOnlyList<ProgramRecommendation> Programs { get; init; }
    public required IReadOnlyList<ProcessRecommendation> Processes { get; init; }
    public required IReadOnlyList<AutostartEntry> AutostartEntries { get; init; }
    public required DateTime ScannedAt { get; init; }

    public int RecommendationCount => Programs.Count + Processes.Count;
    public int AutostartCount => AutostartEntries.Count;
    public int EnabledAutostartCount => AutostartEntries.Count(entry => entry.Enabled);
}

/// <summary>Умный анализ без изменений системы: Prefetch, Uninstall-реестр и текущие процессы.</summary>
public static class SmartAnalysisService
{
    private const long MinimumProgramSize = 100L * 1024 * 1024;
    private const long MinimumProcessMemory = 150L * 1024 * 1024;

    private static readonly object CacheGate = new();
    private static readonly SemaphoreSlim ScanGate = new(1, 1);
    private static AnalysisReport? _last;

    public static AnalysisReport? LastReport
    {
        get
        {
            lock (CacheGate) return _last;
        }
    }

    public static event Action<AnalysisReport>? Completed;

    public static async Task<AnalysisReport> ScanAsync(CancellationToken ct = default)
    {
        await ScanGate.WaitAsync(ct);
        try
        {
            var report = await Task.Run(() => Analyze(ct), ct);
            lock (CacheGate) _last = report;
            Completed?.Invoke(report);
            return report;
        }
        finally
        {
            ScanGate.Release();
        }
    }

    public static void Invalidate()
    {
        lock (CacheGate) _last = null;
    }

    private static AnalysisReport Analyze(CancellationToken ct)
    {
        var autostart = AutostartService.GetEntries();
        var prefetch = ReadPrefetch(ct);
        var installed = ReadInstalledPrograms(ct);
        var programs = BuildProgramRecommendations(installed, prefetch, autostart, ct);
        var processes = BuildProcessRecommendations(installed, autostart, ct);

        return new AnalysisReport
        {
            Programs = programs,
            Processes = processes,
            AutostartEntries = autostart,
            ScannedAt = DateTime.Now,
        };
    }

    private static IReadOnlyList<ProgramRecommendation> BuildProgramRecommendations(
        IReadOnlyList<InstalledProgram> programs,
        IReadOnlyDictionary<string, DateTime> prefetch,
        IReadOnlyList<AutostartEntry> autostart,
        CancellationToken ct)
    {
        var result = new List<ProgramRecommendation>();
        var cutoff = DateTime.UtcNow.AddDays(-60);

        foreach (var program in programs)
        {
            ct.ThrowIfCancellationRequested();
            if (program.SizeBytes < MinimumProgramSize) continue;

            DateTime? lastRun = null;
            foreach (var name in program.ExecutableNames)
            {
                if (!prefetch.TryGetValue(NormalizeExeName(name), out var date)) continue;
                if (lastRun is null || date > lastRun) lastRun = date;
            }
            if (lastRun is null || lastRun > cutoff) continue;

            var lastAccess = GetLastAccess(program);
            var entry = MatchAutostart(autostart, program.ExecutableNames, program.Name);
            result.Add(new ProgramRecommendation(program, lastRun.Value, lastAccess, entry));
        }

        return result
            .OrderByDescending(item => (DateTime.UtcNow - item.LastRun).TotalDays)
            .ThenByDescending(item => item.Program.SizeBytes)
            .Take(20)
            .ToList();
    }

    private static IReadOnlyList<ProcessRecommendation> BuildProcessRecommendations(
        IReadOnlyList<InstalledProgram> programs,
        IReadOnlyList<AutostartEntry> autostart,
        CancellationToken ct)
    {
        var result = new List<ProcessRecommendation>();

        foreach (var process in ProcessGuard.GetEligibleProcesses(80))
        {
            ct.ThrowIfCancellationRequested();
            if (process.WorkingSetBytes < MinimumProcessMemory) continue;

            var exe = process.ExecutablePath is null ? null : Path.GetFileName(process.ExecutablePath);
            var program = exe is null
                ? null
                : programs.FirstOrDefault(item =>
                    item.ExecutableNames.Any(name => NormalizeExeName(name) == NormalizeExeName(exe)));
            var entry = MatchAutostart(autostart,
                exe is null ? [] : [exe], process.Name);

            result.Add(new ProcessRecommendation(process, entry, program));
        }

        return result
            .OrderByDescending(item => item.Process.WorkingSetBytes)
            .Take(12)
            .ToList();
    }

    private static IReadOnlyList<InstalledProgram> ReadInstalledPrograms(CancellationToken ct)
    {
        var result = new List<InstalledProgram>();
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

                            if (Convert.ToInt32(key.GetValue("SystemComponent") ?? 0) == 1) continue;
                            var parent = key.GetValue("ParentKeyName") as string;
                            if (!string.IsNullOrWhiteSpace(parent)) continue;
                            var release = key.GetValue("ReleaseType") as string;
                            if (release is not null &&
                                (release.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
                                 release.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)))
                                continue;

                            var uninstallString = (key.GetValue("UninstallString") as string)?.Trim();
                            if (string.IsNullOrWhiteSpace(uninstallString)) continue;

                            var location = (key.GetValue("InstallLocation") as string)?.Trim();
                            var icon = key.GetValue("DisplayIcon") as string;
                            var sizeKb = GetLong(key.GetValue("EstimatedSize"));
                            var installDate = ParseInstallDate(key.GetValue("InstallDate") as string);
                            var executables = GetExecutableNames(icon, location);

                            if (executables.Count == 0) continue;
                            var dedup = name + "|" + (location ?? string.Empty);
                            if (!seen.Add(dedup)) continue;

                            result.Add(new InstalledProgram(
                                name,
                                location,
                                uninstallString,
                                Math.Max(0, sizeKb) * 1024,
                                installDate,
                                executables));
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

        return result;
    }

    private static IReadOnlyList<string> GetExecutableNames(string? displayIcon, string? installLocation)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var icon = CleanIconPath(displayIcon);
        if (icon is not null) paths.Add(icon);

        if (!string.IsNullOrWhiteSpace(installLocation))
        {
            try
            {
                if (Directory.Exists(installLocation))
                {
                    foreach (var file in Directory
                                 .EnumerateFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly)
                                 .Where(file => !IsHelperExecutable(Path.GetFileName(file)))
                                 .Take(8))
                        paths.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return paths
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsHelperExecutable(string name) =>
        name.Contains("unins", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("setup", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("update", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("install", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("crash", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("vc_redist", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("dxsetup", StringComparison.OrdinalIgnoreCase);

    private static DateTime? GetLastAccess(InstalledProgram program)
    {
        DateTime? latest = null;
        foreach (var name in program.ExecutableNames)
        {
            if (string.IsNullOrWhiteSpace(program.InstallLocation)) continue;
            try
            {
                var path = Path.Combine(program.InstallLocation, name);
                if (!File.Exists(path)) continue;
                var access = File.GetLastAccessTimeUtc(path);
                if (latest is null || access > latest) latest = access;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return latest;
    }

    private static AutostartEntry? MatchAutostart(
        IReadOnlyList<AutostartEntry> entries,
        IEnumerable<string> executableNames,
        string displayName)
    {
        var names = executableNames
            .Select(NormalizeExeName)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var byExe = entries.FirstOrDefault(entry =>
            !entry.IsProtected &&
            entry.ExecutableName is { } exe &&
            names.Contains(NormalizeExeName(exe)));
        if (byExe is not null) return byExe;

        return entries.FirstOrDefault(entry =>
            !entry.IsProtected &&
            string.Equals(entry.Name, displayName, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, DateTime> ReadPrefetch(CancellationToken ct)
    {
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var folder = Path.Combine(windows, "Prefetch");
            if (!Directory.Exists(folder)) return result;

            foreach (var file in Directory.EnumerateFiles(folder, "*.pf", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var exe = ParsePrefetchName(Path.GetFileName(file));
                    if (string.IsNullOrWhiteSpace(exe)) continue;
                    var date = File.GetLastWriteTimeUtc(file);
                    if (!result.TryGetValue(exe, out var current) || date > current)
                        result[exe] = date;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Prefetch read failed: " + ex.Message);
        }
        return result;
    }

    internal static string? ParsePrefetchName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var dash = name.LastIndexOf('-');
        if (dash > 0) name = name[..dash];
        return string.IsNullOrWhiteSpace(name) ? null : NormalizeExeName(name);
    }

    private static string NormalizeExeName(string name)
    {
        var file = Path.GetFileName(name.Trim());
        return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file : file + ".exe";
    }

    private static string? CleanIconPath(string? displayIcon)
    {
        var text = displayIcon?.Trim();
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

        text = Environment.ExpandEnvironmentVariables(text.Trim());
        if (!text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        try { return Path.GetFullPath(text); }
        catch (Exception) { return null; }
    }

    private static long GetLong(object? value)
    {
        try { return value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture); }
        catch (Exception) { return 0; }
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
