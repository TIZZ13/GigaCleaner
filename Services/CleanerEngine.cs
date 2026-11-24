using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace GigaCleaner.Services;

public sealed record CleanReport(long FreedBytes, int Deleted, int Skipped, int Failed)
{
    public IReadOnlyDictionary<CleanTarget, long> FreedByTarget { get; init; } =
        new Dictionary<CleanTarget, long>();
}

/// <summary>
/// Движок очистки. Удаляет только внутри путей белого списка CleanCatalog.
/// Защита путей проверяется повторно перед каждым удалением, а не только при сканировании.
/// </summary>
public sealed class CleanerEngine
{
    private const uint SherbNoConfirmation = 0x0001;
    private const uint SherbNoProgressUi = 0x0002;
    private const uint SherbNoSound = 0x0004;

    private static readonly EnumerationOptions Walk = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

private static readonly string[] ForbiddenRoots = BuildForbiddenRoots();

    // Служебные вложенные папки %TEMP%, которые не являются мусором: там живут рабочие
    // библиотеки запущенных инструментов (агент, winget, .NET). Рекурсивное сканирование
    // Temp считает их «мусором старше 24 ч», но удалить нельзя — файлы заняты, и очистка
    // каждый раз «находит» одни и те же сотни мегабайт.
    private static readonly HashSet<string> TempServiceDirs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "gigatool",
            "gigatool-vision",
            "WinGet",
            ".net",
            "VBCSCompiler",
            "dotnet",
            "NuGetScratch",
            "node-compile-cache",
        };

    private readonly HashSet<string> _allowedDirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _allowedFiles = new(StringComparer.OrdinalIgnoreCase);

    public CleanerEngine()
    {
        foreach (var target in CleanCatalog.All())
        {
            foreach (var path in target.Paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                var full = Normalize(path);
                if (target.Kind is RootKind.FolderContents or RootKind.ServiceFiles)
                    _allowedDirs.Add(full);
                else
                    _allowedFiles.Add(full);
            }
        }
    }

    internal static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
    }

    /// <summary>Разрешён ровно путь цели или что-то внутри разрешённой папки.</summary>
    public bool IsAllowed(string path)
    {
        string full;
        try { full = Normalize(path); }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }

foreach (var forbidden in ForbiddenRoots)
            if (IsUnder(full, forbidden)) return false;

        // Не считаем и не чистим служебные вложенные папки %TEMP% (рабочие данные агента,
        // winget, .NET): они заняты процессами, не являются мусором и вечно «пересчитываются».
        var tempRoot = Normalize(Path.GetTempPath());
        if (IsUnder(full, tempRoot))
        {
            var relative = Path.GetRelativePath(tempRoot, full);
            var top = relative.Split(Path.DirectorySeparatorChar)[0];
            if (TempServiceDirs.Contains(top)) return false;
        }

        if (_allowedFiles.Contains(full)) return true;

        foreach (var dir in _allowedDirs)
            if (IsUnder(full, dir)) return true;

        return false;
    }

    private static bool IsUnder(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public long Measure(CleanTarget target, CancellationToken ct)
    {
        if (target.ProcessName is not null && IsProcessRunning(target.ProcessName)) return 0;

        return target.Kind switch
        {
            RootKind.FileDelete => target.Paths.Where(File.Exists).Where(IsAllowed).Sum(Size.OfFile),
            RootKind.FolderContents or RootKind.ServiceFiles =>
                target.Paths.Sum(path => MeasureFolder(target, path, ct)),
            RootKind.BrowserCookies or RootKind.BrowserAutofill =>
                target.Paths.Where(File.Exists).Where(IsAllowed).Sum(Size.OfFile),
            RootKind.RecycleBin => QueryRecycleBin().Bytes,
            _ => 0,
        };
    }

    public long MeasureAll(IEnumerable<CleanTarget> targets, CancellationToken ct) =>
        targets.Sum(target => Measure(target, ct));

    private long MeasureFolder(CleanTarget target, string path, CancellationToken ct)
    {
        long total = 0;
        foreach (var file in Candidates(target, path, ct)) total += Size.OfFile(file);
        return total;
    }

    private IEnumerable<string> Candidates(CleanTarget target, string root, CancellationToken ct)
    {
        if (!Directory.Exists(root)) yield break;

        string[] files;
        try
        {
            files = Directory.GetFiles(root, "*", Walk);
        }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        var cutoff = target.MinAge > TimeSpan.Zero ? DateTime.UtcNow - target.MinAge : DateTime.MinValue;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (target.FileFilter is not null && !target.FileFilter.IsMatch(Path.GetFileName(file))) continue;
            if (cutoff != DateTime.MinValue && LastWrite(file) > cutoff) continue;
            if (!IsAllowed(file)) continue;
            yield return file;
        }
    }

    private static DateTime LastWrite(string file)
    {
        try { return File.GetLastWriteTimeUtc(file); }
        catch (IOException) { return DateTime.MaxValue; }
        catch (UnauthorizedAccessException) { return DateTime.MaxValue; }
    }

public CleanReport Clean(IReadOnlyCollection<CleanTarget> targets, CancellationToken ct)
    {
        long freed = 0;
        int deleted = 0, skipped = 0, failed = 0;
        var freedByTarget = new Dictionary<CleanTarget, long>();

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();

            if (target.ProcessName is not null && IsProcessRunning(target.ProcessName))
            {
                skipped++;
                continue;
            }

            var before = freed;
            try
            {
                switch (target.Kind)
                {
                    case RootKind.Clipboard:
                        if (!ClearClipboard()) failed++;
                        break;

                    case RootKind.RecycleBin:
                        CleanRecycleBin(ref freed, ref deleted, ref failed);
                        break;

                    case RootKind.DnsCache:
                        if (!FlushDns()) failed++;
                        break;

                    case RootKind.FileDelete:
                        CleanFiles(target, ref freed, ref deleted, ref skipped, ref failed);
                        break;

                    case RootKind.FolderContents:
                        CleanFolders(target, ref freed, ref deleted, ref skipped, ref failed, ct);
                        break;

                    case RootKind.ServiceFiles:
                        CleanServiceFiles(target, ref freed, ref deleted, ref skipped, ref failed, ct);
                        break;

                    case RootKind.BrowserCookies:
                        CleanCookies(target, ref freed, ref deleted, ref skipped, ref failed, ct);
                        break;

                    case RootKind.BrowserAutofill:
                        CleanAutofill(target, ref freed, ref deleted, ref skipped, ref failed, ct);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                failed++;
            }

            var freedHere = freed - before;
            if (freedHere > 0) freedByTarget[target] = freedHere;
        }

        return new CleanReport(freed, deleted, skipped, failed) { FreedByTarget = freedByTarget };
    }

    private void CleanFiles(CleanTarget target, ref long freed, ref int deleted, ref int skipped, ref int failed)
    {
        foreach (var path in target.Paths)
        {
            if (!File.Exists(path)) continue;
            if (!IsAllowed(path)) { skipped++; continue; }
            if (TryDelete(path, out var size)) { deleted++; freed += size; }
            else failed++;
        }
    }

    private void CleanFolders(CleanTarget target, ref long freed, ref int deleted, ref int skipped, ref int failed,
        CancellationToken ct)
    {
        foreach (var path in target.Paths)
        {
            if (!IsAllowed(path)) { skipped++; continue; }
            foreach (var file in Candidates(target, path, ct))
            {
                if (TryDelete(file, out var size)) { deleted++; freed += size; }
                else failed++;
            }
        }
    }

    private void CleanServiceFiles(CleanTarget target, ref long freed, ref int deleted, ref int skipped,
        ref int failed, CancellationToken ct)
    {
        var (canClean, wasRunning) = StopService(target.ServiceName);
        try
        {
            if (!canClean) { failed++; return; }
            CleanFolders(target, ref freed, ref deleted, ref skipped, ref failed, ct);
        }
        finally
        {
            if (wasRunning) StartService(target.ServiceName);
        }
    }

    private void CleanCookies(CleanTarget target, ref long freed, ref int deleted, ref int skipped, ref int failed,
        CancellationToken ct)
    {
        var whitelist = SettingsStore.CookieWhitelist;

        foreach (var path in target.Paths)
        {
            if (!File.Exists(path)) continue;
            if (!IsAllowed(path)) { skipped++; continue; }

            var result = BrowserDataCleaner.CleanCookies(path, whitelist, ct);
            if (!result.Ok) { failed++; continue; }
            freed += result.Freed;
            deleted += result.Removed;
        }
    }

    private void CleanAutofill(CleanTarget target, ref long freed, ref int deleted, ref int skipped, ref int failed,
        CancellationToken ct)
    {
        foreach (var path in target.Paths)
        {
            if (!File.Exists(path)) continue;
            if (!IsAllowed(path)) { skipped++; continue; }

            var result = BrowserDataCleaner.CleanAutofill(path, ct);
            if (!result.Ok) { failed++; continue; }
            freed += result.Freed;
            deleted += result.Removed;
        }
    }

    private static void CleanRecycleBin(ref long freed, ref int deleted, ref int failed)
    {
        var before = QueryRecycleBin();
        if (before.Items == 0) return;

        var hr = SHEmptyRecycleBin(IntPtr.Zero, null,
            SherbNoConfirmation | SherbNoProgressUi | SherbNoSound);
        if (hr == 0)
        {
            freed += before.Bytes;
            deleted += before.Items;
        }
        else
        {
            failed++;
        }
    }

    private static bool TryDelete(string file, out long size)
    {
        size = Size.OfFile(file);
        try
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
            return !File.Exists(file);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool IsProcessRunning(string name)
    {
        try
        {
            var processes = Process.GetProcessesByName(name);
            try { return processes.Length > 0; }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static (bool CanClean, bool WasRunning) StopService(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return (true, false);

        var wasRunning = false;
        try
        {
            using var service = new ServiceController(name);

            if (service.Status == ServiceControllerStatus.Stopped) return (true, false);

            if (service.Status == ServiceControllerStatus.StopPending)
            {
                wasRunning = true;
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(25));
                return (true, true);
            }

            if (service.Status != ServiceControllerStatus.Running) return (false, false);

            service.Stop();
            wasRunning = true;
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(25));
            return (true, true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or System.TimeoutException
                                       or UnauthorizedAccessException or NotSupportedException)
        {
            return (false, wasRunning);
        }
    }

    private static void StartService(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            using var service = new ServiceController(name);
            if (service.Status == ServiceControllerStatus.Stopped)
            {
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(25));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or System.TimeoutException
                                       or UnauthorizedAccessException or NotSupportedException)
        {
            // Служба вернётся в строй при следующем запуске Windows.
        }
    }

    private static bool FlushDns()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            });

            if (process is null) return false;
            process.WaitForExit(15000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool ClearClipboard()
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try { return EmptyClipboard(); }
        finally { CloseClipboard(); }
    }

    private static (long Bytes, int Items) QueryRecycleBin()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        var hr = SHQueryRecycleBin(null, ref info);
        if (hr != 0) return (0, 0);
        return (info.i64Size, (int)Math.Min(info.i64NumItems, int.MaxValue));
    }

    private static string[] BuildForbiddenRoots()
    {
        var roots = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windows))
        {
            roots.Add(Path.Combine(windows, "System32"));
            roots.Add(Path.Combine(windows, "SysWOW64"));
            roots.Add(Path.Combine(windows, "WinSxS"));
        }

        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Normalize(root!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();
}

