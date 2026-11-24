using System.Diagnostics;
using System.IO;
using System.Management;
using System.ServiceProcess;
using Microsoft.Win32;

namespace GigaCleaner.Services;

public enum WindowsRecommendationKind
{
    Service,
    Hibernation,
    UwpApplication,
}

public sealed class WindowsRecommendation
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Category { get; init; }
    public required string Reason { get; init; }
    public long MemoryBytes { get; init; }
    public long DiskBytes { get; init; }
    public bool CanToggle { get; init; }
    public bool IsApplied { get; init; }
    public string ActionText { get; init; } = string.Empty;
    internal WindowsRecommendationKind Kind { get; init; }
    internal string? ServiceName { get; init; }
    internal int TargetStartMode { get; init; }
}

public sealed record WindowsActionResult(bool Ok, string Message);

public static class WindowsOptimizationService
{
    private const int AutoStart = 2;
    private const int ManualStart = 3;
    private const int DisabledStart = 4;

    private static readonly object Gate = new();

    private static readonly HashSet<string> AllowedServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "XblAuthManager", "XblGameSave", "XboxGipSvc", "XboxNetApiSvc",
        "Spooler", "Fax", "WSearch", "DiagTrack", "dmwappushservice",
        "SysMain", "bthserv", "RemoteRegistry", "MapsBroker",
    };

    private static readonly HashSet<string> ForbiddenServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "WinDefend", "SecurityHealthService", "wuauserv", "UsoSvc", "WaaSMedicSvc", "BITS",
        "mpssvc", "BFE", "Dhcp", "Dnscache", "NlaSvc", "netprofm",
        "LanmanWorkstation", "WlanSvc", "Audiosrv", "AudioEndpointBuilder",
        "CryptSvc", "RpcSs", "RpcEptMapper", "DcomLaunch", "Power", "PlugPlay",
        "ProfSvc", "EventLog", "Schedule", "Themes", "Winmgmt", "SamSs", "KeyIso",
        "VaultSvc", "gpsvc", "UserManager", "SystemEventsBroker", "BrokerInfrastructure",
        "StateRepository", "AppXSvc", "ClipSVC", "TokenBroker", "WpnService",
        "WpnUserService", "ShellHWDetection", "DeviceInstall", "DsmSvc",
        "NcbService", "Netman", "SENS", "W32Time",
    };

    private static readonly string[] XboxProcesses =
    [
        "GameBar", "GameBarFTServer", "XboxApp", "XboxPcApp",
        "XboxGameBar", "XboxGameBarWidgets",
    ];

    private static readonly (string Family, string Title)[] UwpApps =
    [
        ("Microsoft.Windows.Cortana_cw5n1h2txyewy", "Cortana"),
        ("Microsoft.XboxApp_8wekyb3d8bbwe", "Xbox"),
        ("Microsoft.XboxGamingOverlay_8wekyb3d8bbwe", "Xbox Game Bar"),
        ("Microsoft.WindowsMaps_8wekyb3d8bbwe", "Карты"),
        ("Microsoft.BingWeather_8wekyb3d8bbwe", "Погода"),
        ("Microsoft.Getstarted_8wekyb3d8bbwe", "Советы"),
    ];

    public static IReadOnlyList<WindowsRecommendation> Scan()
    {
        lock (Gate)
        {
            var result = new List<WindowsRecommendation>();
            var backups = new Dictionary<string, WindowsServiceBackup>(StringComparer.OrdinalIgnoreCase);
            foreach (var backup in SettingsStore.ServiceBackups)
                backups[backup.Name] = backup;

            var wmiServices = QueryWmiServices();
            var context = new ScanContext
            {
                PrintersFound = QueryPhysicalPrinters(),
                AllPhysicalDisksAreSsd = QueryAllPhysicalDisksAreSsd(),
                XboxAppsRunning = QueryXboxAppsRunning(),
                BluetoothPaired = QueryBluetoothPaired(),
                SearchIndexBytes = QuerySearchIndexSize(),
            };

            foreach (var spec in BuildServiceSpecs())
            {
                var state = GetServiceState(spec.Name, wmiServices);
                if (!state.Exists) continue;

                if (backups.TryGetValue(spec.Name, out var backup))
                {
                    if (IsOriginalState(state, backup))
                    {
                        SettingsStore.RemoveServiceBackup(spec.Name);
                    }
                    else
                    {
                        result.Add(BuildAppliedService(spec, backup));
                        continue;
                    }
                }

                if (!IsActive(state) || !spec.Unused(context, state)) continue;
                if (state.StartMode == spec.TargetStartMode &&
                    state.Status != ServiceControllerStatus.Running) continue;

                result.Add(new WindowsRecommendation
                {
                    Id = "service:" + spec.Name,
                    Title = spec.Title,
                    Category = "СЛУЖБА",
                    Reason = spec.Reason(context, state),
                    MemoryBytes = state.MemoryBytes,
                    CanToggle = true,
                    IsApplied = false,
                    ActionText = "Отключить",
                    Kind = WindowsRecommendationKind.Service,
                    ServiceName = spec.Name,
                    TargetStartMode = spec.TargetStartMode,
                });
            }

            AddHibernationRecommendation(result);
            AddUwpRecommendations(result);

            return result
                .OrderByDescending(item => item.MemoryBytes + item.DiskBytes)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    public static WindowsActionResult Toggle(WindowsRecommendation item)
    {
        lock (Gate)
        {
            return item.Kind switch
            {
                WindowsRecommendationKind.Service when item.ServiceName is { } name =>
                    item.IsApplied ? RestoreService(name) : ApplyService(name, item.TargetStartMode),
                WindowsRecommendationKind.Hibernation =>
                    item.IsApplied ? RestoreHibernation() : ApplyHibernation(),
                WindowsRecommendationKind.UwpApplication =>
                    new WindowsActionResult(false, "Встроенные приложения не удаляются автоматически. Откройте Параметры → Приложения."),
                _ => new WindowsActionResult(false, "Неизвестный тип рекомендации."),
            };
        }
    }

    private static IReadOnlyList<ServiceSpec> BuildServiceSpecs()
    {
        var specs = new List<ServiceSpec>();

        Add("XblAuthManager", "Xbox Auth Manager", ManualStart);
        Add("XblGameSave", "Xbox Game Save", ManualStart);
        Add("XboxGipSvc", "Xbox GIP Service", ManualStart);
        Add("XboxNetApiSvc", "Xbox Network API", ManualStart);

        specs.Add(new ServiceSpec("Spooler", "Диспетчер печати", ManualStart,
            (context, _) => context.PrintersFound == false,
            (context, state) => $"Физические принтеры не найдены; служба печати занимает {Size.Format(state.MemoryBytes)} ОЗУ."));

        specs.Add(new ServiceSpec("Fax", "Факс", DisabledStart,
            (context, _) => context.PrintersFound == false,
            (_, _) => "Физические принтеры и факс-устройства не найдены."));

        specs.Add(new ServiceSpec("WSearch", "Windows Search", ManualStart,
            (context, state) => state.Status == ServiceControllerStatus.Running &&
                                (state.MemoryBytes >= 150L * 1024 * 1024 ||
                                 context.SearchIndexBytes >= 1024L * 1024 * 1024),
            (context, state) => $"Индекс поиска: {Size.Format(context.SearchIndexBytes)}; служба использует " +
                                $"{Size.Format(state.MemoryBytes)} ОЗУ."));

        specs.Add(new ServiceSpec("DiagTrack", "Телеметрия и диагностика", DisabledStart,
            (_, _) => true,
            (_, state) => $"Служба диагностики работает в фоне и занимает {Size.Format(state.MemoryBytes)} ОЗУ."));

        specs.Add(new ServiceSpec("dmwappushservice", "WAP-сообщения диагностики", DisabledStart,
            (_, _) => true,
            (_, state) => $"Служба диагностики работает в фоне и занимает {Size.Format(state.MemoryBytes)} ОЗУ."));

        specs.Add(new ServiceSpec("SysMain", "SysMain / Superfetch", ManualStart,
            (context, _) => context.AllPhysicalDisksAreSsd == true,
            (_, _) => "Все физические диски — SSD; SysMain не даёт заметного выигрыша на накопителе."));

        specs.Add(new ServiceSpec("bthserv", "Служба Bluetooth", ManualStart,
            (context, _) => context.BluetoothPaired == false,
            (_, _) => "Сопряжённые Bluetooth-устройства не найдены."));

        specs.Add(new ServiceSpec("RemoteRegistry", "Удалённый реестр", DisabledStart,
            (_, _) => true,
            (_, _) => "Удалённое редактирование реестра не используется."));

        specs.Add(new ServiceSpec("MapsBroker", "Диспетчер карт", DisabledStart,
            (_, _) => true,
            (_, _) => "Офлайн-карты и приложение «Карты» не используются."));

        return specs;

        void Add(string name, string title, int target)
        {
            specs.Add(new ServiceSpec(name, title, target,
                (context, _) => context.XboxAppsRunning == false,
                (_, state) => state.Status == ServiceControllerStatus.Running
                    ? $"Приложения Xbox и Game Bar не запущены; служба занимает {Size.Format(state.MemoryBytes)} ОЗУ."
                    : "Автозапуск включён, но приложения Xbox и Game Bar не запущены."));
        }
    }

    private static WindowsRecommendation BuildAppliedService(ServiceSpec spec, WindowsServiceBackup backup)
    {
        var mode = backup.StartMode switch
        {
            AutoStart => "автоматически",
            ManualStart => "вручную",
            DisabledStart => "отключена",
            _ => "неизвестно",
        };
        var running = backup.WasRunning ? "была запущена" : "была остановлена";

        return new WindowsRecommendation
        {
            Id = "service:" + spec.Name,
            Title = spec.Title,
            Category = "СЛУЖБА",
            Reason = $"Служба изменена GigaCleaner. Исходный режим: {mode}, {running}. Верните, чтобы восстановить.",
            MemoryBytes = 0,
            DiskBytes = 0,
            CanToggle = true,
            IsApplied = true,
            ActionText = "Вернуть",
            Kind = WindowsRecommendationKind.Service,
            ServiceName = spec.Name,
            TargetStartMode = spec.TargetStartMode,
        };
    }

    private static void AddHibernationRecommendation(List<WindowsRecommendation> result)
    {
        var file = GetHibernationFile();
        var backup = SettingsStore.HibernationWasEnabled;

        if (backup is not null)
        {
            if (!File.Exists(file.Path))
            {
                result.Add(new WindowsRecommendation
                {
                    Id = "hibernation",
                    Title = "Гибернация Windows",
                    Category = "СИСТЕМНАЯ ФУНКЦИЯ",
                    Reason = "Гибернация уже отключена GigaCleaner. Вернуть — снова включит hiberfil.sys и быстрый запуск.",
                    CanToggle = true,
                    IsApplied = true,
                    ActionText = "Вернуть",
                    Kind = WindowsRecommendationKind.Hibernation,
                });
            }
            else
            {
                SettingsStore.ClearHibernationBackup();
            }
            return;
        }

        if (file.Bytes <= 0) return;

        result.Add(new WindowsRecommendation
        {
            Id = "hibernation",
            Title = "Файл гибернации",
            Category = "СИСТЕМНАЯ ФУНКЦИЯ",
            Reason = $"Файл hiberfil.sys занимает {Size.Format(file.Bytes)}. Отключение также отключит быстрый запуск Windows.",
            DiskBytes = file.Bytes,
            CanToggle = true,
            ActionText = "Отключить",
            Kind = WindowsRecommendationKind.Hibernation,
        });
    }

    private static void AddUwpRecommendations(List<WindowsRecommendation> result)
    {
        var packages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        var cutoff = DateTime.UtcNow.AddDays(-60);

        foreach (var (family, title) in UwpApps)
        {
            var folder = Path.Combine(packages, family);
            if (!Directory.Exists(folder)) continue;

            var lastUsed = GetLastUsage(folder);
            if (lastUsed is null || lastUsed > cutoff) continue;

            result.Add(new WindowsRecommendation
            {
                Id = "uwp:" + family,
                Title = title,
                Category = "UWP-ПРИЛОЖЕНИЕ",
                Reason = $"Пакет установлен, но следов запуска нет с {lastUsed:dd.MM.yyyy}. Удаление — в Параметрах Windows → Приложения.",
                DiskBytes = GetDirectorySize(folder),
                CanToggle = false,
                Kind = WindowsRecommendationKind.UwpApplication,
            });
        }
    }

    private static WindowsActionResult ApplyService(string name, int targetStartMode)
    {
        if (!IsAllowedService(name))
            return new WindowsActionResult(false, "Эта служба не входит в список безопасных рекомендаций.");
        if (SettingsStore.GetServiceBackup(name) is not null)
            return new WindowsActionResult(false, "Служба уже изменена GigaCleaner. Сначала верните исходное состояние.");

        var state = GetServiceState(name, QueryWmiServices());
        if (!state.Exists) return new WindowsActionResult(false, "Служба не найдена.");

        SettingsStore.SaveServiceBackup(name, state.StartMode, state.Status == ServiceControllerStatus.Running);
        var wasRunning = state.Status == ServiceControllerStatus.Running;

        if (wasRunning)
        {
            try
            {
                StopService(name);
            }
            catch (Exception ex)
            {
                if (wasRunning) TryStartService(name);
                SettingsStore.RemoveServiceBackup(name);
                return new WindowsActionResult(false, "Не удалось остановить службу: " + ex.Message);
            }
        }

        if (!SetStartMode(name, targetStartMode, out var error))
        {
            if (wasRunning) TryStartService(name);
            SettingsStore.RemoveServiceBackup(name);
            return new WindowsActionResult(false, "Не удалось изменить режим запуска: " + error);
        }

        AppLog.Write($"Windows optimization: {name} -> start {targetStartMode}");
        return new WindowsActionResult(true, "Служба отключена. Вернуть можно одной кнопкой.");
    }

    private static WindowsActionResult RestoreService(string name)
    {
        if (!IsAllowedService(name))
            return new WindowsActionResult(false, "Эта служба не входит в список безопасных рекомендаций.");

        var backup = SettingsStore.GetServiceBackup(name);
        if (backup is null) return new WindowsActionResult(false, "Сохранённое состояние службы не найдено.");

        var state = GetServiceState(name, null);
        if (!state.Exists)
        {
            SettingsStore.RemoveServiceBackup(name);
            return new WindowsActionResult(false, "Служба больше не существует; запись состояния удалена.");
        }

        if (!SetStartMode(name, backup.StartMode, out var error))
            return new WindowsActionResult(false, "Не удалось вернуть режим запуска: " + error);

        if (backup.WasRunning && backup.StartMode != DisabledStart && !TryStartService(name))
            return new WindowsActionResult(false, "Режим возвращён, но службу не удалось запустить. Попробуйте ещё раз.");

        SettingsStore.RemoveServiceBackup(name);
        AppLog.Write($"Windows optimization: {name} restored");
        return new WindowsActionResult(true, "Служба возвращена в исходное состояние.");
    }

    private static WindowsActionResult ApplyHibernation()
    {
        var file = GetHibernationFile();
        if (!File.Exists(file.Path) || file.Bytes <= 0)
            return new WindowsActionResult(false, "Файл гибернации не найден.");

        SettingsStore.SaveHibernationBackup(true);
        var (ok, error) = RunPowerCfg("/h", "off");
        if (!ok)
        {
            SettingsStore.ClearHibernationBackup();
            return new WindowsActionResult(false, "Не удалось отключить гибернацию: " + error);
        }

        AppLog.Write("Windows optimization: hibernation disabled");
        return new WindowsActionResult(true, "Гибернация и быстрый запуск отключены.");
    }

    private static WindowsActionResult RestoreHibernation()
    {
        var backup = SettingsStore.HibernationWasEnabled;
        if (backup is null) return new WindowsActionResult(false, "Сохранённое состояние гибернации не найдено.");

        if (backup == true)
        {
            var (ok, error) = RunPowerCfg("/h", "on");
            if (!ok) return new WindowsActionResult(false, "Не удалось вернуть гибернацию: " + error);
        }

        SettingsStore.ClearHibernationBackup();
        AppLog.Write("Windows optimization: hibernation restored");
        return new WindowsActionResult(true, "Гибернация возвращена в исходное состояние.");
    }

    private static bool IsAllowedService(string name) =>
        AllowedServices.Contains(name) && !ForbiddenServices.Contains(name);

    private static bool IsActive(ServiceState state) =>
        state.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending
            or ServiceControllerStatus.ContinuePending ||
        state.StartMode == AutoStart;

    private static bool IsOriginalState(ServiceState state, WindowsServiceBackup backup) =>
        state.StartMode == backup.StartMode &&
        backup.WasRunning == (state.Status == ServiceControllerStatus.Running);

    private static ServiceState GetServiceState(string name, IReadOnlyDictionary<string, int>? wmiServices)
    {
        try
        {
            ServiceControllerStatus status;
            using (var controller = new ServiceController(name))
            {
                controller.Refresh();
                status = controller.Status;
            }

            var startMode = GetStartMode(name);
            long memory = 0;
            if (wmiServices is not null &&
                wmiServices.TryGetValue(name, out var pid) && pid > 0)
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    memory = process.WorkingSet64;
                }
                catch (Exception)
                {
                    memory = 0;
                }
            }

            return new ServiceState(true, status, startMode, memory);
        }
        catch (Exception)
        {
            return new ServiceState(false, ServiceControllerStatus.Stopped, -1, 0);
        }
    }

    private static int GetStartMode(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + name);
            var value = key?.GetValue("Start");
            return value is null ? -1 : Convert.ToInt32(value);
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static void StopService(string name)
    {
        using var controller = new ServiceController(name);
        if (controller.Status != ServiceControllerStatus.Running) return;
        controller.Stop();
        controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(12));
    }

    private static bool TryStartService(string name)
    {
        try
        {
            using var controller = new ServiceController(name);
            if (controller.Status == ServiceControllerStatus.Running) return true;
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(12));
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Windows optimization start failed ({name}): {ex.Message}");
            return false;
        }
    }

    private static bool SetStartMode(string name, int startMode, out string error)
    {
        var value = startMode switch
        {
            AutoStart => "auto",
            ManualStart => "demand",
            DisabledStart => "disabled",
            _ => "demand",
        };
        var (ok, output) = RunProcess("sc.exe", "config", name, "start=", value);
        error = ok ? string.Empty : output;
        return ok;
    }

    private static (bool Ok, string Output) RunPowerCfg(params string[] arguments) =>
        RunProcess(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), arguments);

    private static (bool Ok, string Output) RunProcess(string file, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = file,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process is null) return (false, "Не удалось запустить процесс.");
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(true); } catch (Exception) { }
                return (false, "Истекло время ожидания.");
            }
            return (process.ExitCode == 0, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static Dictionary<string, int> QueryWmiServices()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, ProcessId FROM Win32_Service");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    var name = item["Name"] as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    result[name] = item["ProcessId"] is null ? 0 : Convert.ToInt32(item["ProcessId"]);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("WMI services read failed: " + ex.Message);
        }
        return result;
    }

    private static bool? QueryPhysicalPrinters()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PortName FROM Win32_Printer");
            var found = false;
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    var name = item["Name"] as string ?? string.Empty;
                    var port = item["PortName"] as string ?? string.Empty;
                    if (IsVirtualPrinter(name, port)) continue;
                    found = true;
                    break;
                }
            }
            return found;
        }
        catch (Exception ex)
        {
            AppLog.Write("WMI printers read failed: " + ex.Message);
            return null;
        }
    }

    private static bool IsVirtualPrinter(string name, string port)
    {
        var lower = name.ToLowerInvariant();
        var portLower = port.ToLowerInvariant();
        return lower.Contains("microsoft print to pdf") ||
               lower.Contains("microsoft xps") ||
               lower.Contains("onenote") ||
               lower.Contains("fax") ||
               portLower.StartsWith("portprompt:") ||
               portLower.StartsWith("nul") ||
               portLower.StartsWith("shrfa");
    }

    private static bool? QueryAllPhysicalDisksAreSsd()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT MediaType FROM MSFT_PhysicalDisk");
            var found = false;
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    found = true;
                    var mediaType = item["MediaType"] is null ? 0 : Convert.ToInt32(item["MediaType"]);
                    if (mediaType is not (4 or 5)) return false;
                }
            }
            return found;
        }
        catch (Exception ex)
        {
            AppLog.Write("WMI disk read failed: " + ex.Message);
            return null;
        }
    }

    private static bool? QueryXboxAppsRunning()
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (XboxProcesses.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool? QueryBluetoothPaired()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices");
            return key is not null && key.GetSubKeyNames().Length > 0;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long QuerySearchIndexSize()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft", "Search", "Data", "Applications", "Windows", "Windows.edb");
            return Size.OfFile(path);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static (string Path, long Bytes) GetHibernationFile()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var path = Path.Combine(root, "hiberfil.sys");
        return (path, Size.OfFile(path));
    }

    private static DateTime? GetLastUsage(string folder)
    {
        DateTime? latest = null;
        void Consider(DateTime value)
        {
            if (value.Year < 2000) return;
            if (latest is null || value > latest) latest = value;
        }

        try { Consider(Directory.GetLastAccessTimeUtc(folder)); } catch (Exception) { }
        try { Consider(Directory.GetLastWriteTimeUtc(folder)); } catch (Exception) { }

        var localState = Path.Combine(folder, "LocalState");
        try
        {
            if (Directory.Exists(localState))
            {
                Consider(Directory.GetLastAccessTimeUtc(localState));
                Consider(Directory.GetLastWriteTimeUtc(localState));
            }
        }
        catch (Exception) { }
        return latest;
    }

    private static long GetDirectorySize(string folder)
    {
        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch (Exception) { }
            }
            return total;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private sealed record ServiceSpec(
        string Name,
        string Title,
        int TargetStartMode,
        Func<ScanContext, ServiceState, bool> Unused,
        Func<ScanContext, ServiceState, string> Reason);

    private sealed record ServiceState(
        bool Exists,
        ServiceControllerStatus Status,
        int StartMode,
        long MemoryBytes);

    private sealed class ScanContext
    {
        public bool? PrintersFound { get; init; }
        public bool? AllPhysicalDisksAreSsd { get; init; }
        public bool? XboxAppsRunning { get; init; }
        public bool? BluetoothPaired { get; init; }
        public long SearchIndexBytes { get; init; }
    }
}
