using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace GigaCleaner.Services;

public sealed record UserProcessInfo(
    int Pid,
    string Name,
    string? ExecutablePath,
    string FriendlyName,
    long WorkingSetBytes);

/// <summary>
/// Жёсткий фильтр процессов. Системные процессы, службы, драйверы, антивирусы
/// и сам GigaCleaner никогда не попадают в список для сна.
/// </summary>
public static class ProcessGuard
{
    private const int ProcessQueryLimitedInformation = 0x1000;

    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost",
        "dwm", "explorer", "fontdrvhost", "audiodg", "spoolsv", "taskhostw", "sihost", "ctfmon",
        "runtimebroker", "shellexperiencehost", "startmenuexperiencehost", "searchhost", "searchindexer",
        "searchapp", "securityhealthservice", "securityhealthsystray", "msmpeng", "nissrv",
        "mpdefendercoreservice", "smartscreen", "sgrmbroker", "lsaiso", "dashost", "wudfhost",
        "usoclient", "tiworker", "trustedinstaller", "msiexec", "consent", "lockapp", "logonui",
        "widgetservice", "widgets", "textinputhost", "dllhost", "wmiprvse", "taskmgr", "mmc",
        "nvcontainer", "nvdisplay.container", "nvsphelper64", "nvtelemetry", "amdow", "amdfendrsr",
        "atieclxx", "igfxem", "igfxtray", "igfxhk", "radeonsoftware", "avastui", "avastsvc",
        "afwserv", "avgui", "avgnt", "avguard", "kaspersky", "avp", "ekrn", "egui", "bdagent",
        "bdservicehost", "nortonsecurity", "mcafee", "mcshield", "mbam", "malwarebytes", "sophos",
        "tmus", "drweb", "cfp", "cmdagent", "huorong", "wsctrl", "narrator", "magnify", "osk",
        "gigacleaner", "gigacleaner.notifier",
    };

    private static readonly string[] ProtectedPathMarkers =
    [
        "\\windows defender\\",
        "\\microsoft security client\\",
        "\\kaspersky",
        "\\eset\\",
        "\\avast",
        "\\avg\\",
        "\\bitdefender",
        "\\norton",
        "\\mcafee",
        "\\malwarebytes",
        "\\sophos",
        "\\dr.web",
        "\\comodo",
        "\\huorong",
        "\\360safe",
        "\\windowsapps\\",
    ];

    private static readonly string WindowsDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar);

    private static readonly string ProgramDataDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData).TrimEnd(Path.DirectorySeparatorChar);

    private static readonly int CurrentSessionId = Process.GetCurrentProcess().SessionId;
    private static readonly int CurrentPid = Environment.ProcessId;

    public static bool IsProtectedName(string name)
    {
        var clean = Path.GetFileNameWithoutExtension(name.Trim());
        return ProtectedNames.Contains(clean);
    }

    public static bool IsProtectedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { return true; }

        if (IsUnder(full, WindowsDirectory) || IsUnder(full, ProgramDataDirectory)) return true;
        foreach (var marker in ProtectedPathMarkers)
            if (full.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static bool TryGetEligible(Process process, out UserProcessInfo info, out string reason)
    {
        info = null!;
        reason = string.Empty;

        try
        {
            if (process.Id == CurrentPid)
            {
                reason = "Это сам GigaCleaner";
                return false;
            }
            if (process.Id <= 4)
            {
                reason = "Системный процесс";
                return false;
            }
            if (process.SessionId != CurrentSessionId)
            {
                reason = "Другая сессия или служба";
                return false;
            }

            var name = process.ProcessName;
            if (IsProtectedName(name))
            {
                reason = "Системный или защищённый процесс";
                return false;
            }

            var path = GetPath(process);
            if (string.IsNullOrWhiteSpace(path))
            {
                reason = "Нет доступа к файлу процесса";
                return false;
            }
            if (IsProtectedPath(path))
            {
                reason = "Системный каталог или защищённая программа";
                return false;
            }
            if (IsCritical(process.Id))
            {
                reason = "Критический процесс Windows";
                return false;
            }

            info = new UserProcessInfo(
                process.Id,
                name,
                path,
                GetFriendlyName(path, name),
                SafeWorkingSet(process));
            return true;
        }
        catch (Exception ex)
        {
            reason = "Нет доступа: " + ex.Message;
            return false;
        }
    }

    public static IReadOnlyList<UserProcessInfo> GetEligibleProcesses(int limit = 60)
    {
        var result = new List<UserProcessInfo>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (!TryGetEligible(process, out var info, out _)) continue;
                result.Add(info);
            }
        }

        return result
            .OrderByDescending(item => item.WorkingSetBytes)
            .Take(limit)
            .ToList();
    }

    private static bool IsCritical(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return true;

        try
        {
            return IsProcessCritical(handle, out var critical) && critical;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? GetPath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch (Exception) { return null; }
    }

    private static string GetFriendlyName(string path, string fallback)
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            return string.IsNullOrWhiteSpace(description) ? fallback : description.Trim();
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static long SafeWorkingSet(Process process)
    {
        try { return process.WorkingSet64; }
        catch (Exception) { return 0; }
    }

    private static bool IsUnder(string path, string root) =>
        !string.IsNullOrEmpty(root) &&
        (path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
         path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessCritical(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
