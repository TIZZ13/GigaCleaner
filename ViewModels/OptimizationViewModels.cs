using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using GigaCleaner.Services;
using ByteSize = GigaCleaner.Services.Size;

namespace GigaCleaner.ViewModels;

public sealed class RecommendationVm : Observable
{
    private readonly AutostartEntry? _autostart;
    private readonly int? _pid;
    private readonly InstalledProgram? _program;
    private readonly Action<string> _report;
    private bool _canDisableAutostart;
    private bool _canSleep;
    private bool _canUninstall;
    private string _status = string.Empty;

    public RecommendationVm(string title, string badge, string detail, AutostartEntry? autostart,
        int? pid, InstalledProgram? program, Action<string> report)
    {
        Title = title;
        Badge = badge;
        Detail = detail;
        _autostart = autostart;
        _pid = pid;
        _program = program;
        _report = report;

        _canDisableAutostart = autostart is { IsProtected: false };
        _canSleep = pid.HasValue;
        _canUninstall = program is not null && !string.IsNullOrWhiteSpace(program.UninstallString);

        DisableAutostartCommand = new RelayCommand(DisableAutostart);
        SleepCommand = new RelayCommand(Sleep);
        UninstallCommand = new RelayCommand(Uninstall);
    }

    public string Title { get; }
    public string Badge { get; }
    public string Detail { get; }
    public RelayCommand DisableAutostartCommand { get; }
    public RelayCommand SleepCommand { get; }
    public RelayCommand UninstallCommand { get; }

    public bool CanDisableAutostart
    {
        get => _canDisableAutostart;
        private set => Set(ref _canDisableAutostart, value);
    }

    public bool CanSleep
    {
        get => _canSleep;
        private set => Set(ref _canSleep, value);
    }

    public bool CanUninstall
    {
        get => _canUninstall;
        private set => Set(ref _canUninstall, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    private void DisableAutostart()
    {
        if (_autostart is null) return;

        var result = AutostartService.SetEnabled(_autostart, false);
        Status = result.Message;
        _report(result.Message);
        if (!result.Ok) return;

        CanDisableAutostart = false;
        SmartAnalysisService.Invalidate();
    }

    private void Sleep()
    {
        if (_pid is not { } pid) return;

        var result = SleepService.Instance.LowerPriority(pid);
        Status = result.Message;
        _report(result.Message);
        if (result.Ok) CanSleep = false;
    }

    private void Uninstall()
    {
        if (_program is null || string.IsNullOrWhiteSpace(_program.UninstallString)) return;

        var answer = MessageBox.Show(
            $"Запустить штатный деинсталлятор для «{_program.Name}»?\n\n" +
            $"{_program.UninstallString}\n\n" +
            "GigaCleaner не удаляет папки и ключи реестра самостоятельно.",
            "Удаление программы",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        var result = UninstallLauncher.Launch(_program.UninstallString);
        Status = result.Message;
        _report(result.Message);
        if (result.Ok) CanUninstall = false;
    }
}

public sealed class SmartAnalysisViewModel : Observable
{
    private readonly RelayCommand _scan;
    private AnalysisReport? _applied;
    private bool _loaded;
    private bool _busy;
    private string _status = "Нажмите «Анализ», чтобы проверить установленные программы.";
    private string _summary = "Программы без запуска дольше 60 дней.";

    public SmartAnalysisViewModel()
    {
        _scan = new RelayCommand(() => _ = ScanAsync(), () => !_busy);
        SmartAnalysisService.Completed += OnAnalysisCompleted;
        if (SmartAnalysisService.LastReport is { } report) Apply(report);
    }

    public ObservableCollection<RecommendationVm> Recommendations { get; } = [];
    public RelayCommand ScanCommand => _scan;
    public bool HasRecommendations => Recommendations.Count > 0;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        if (SmartAnalysisService.LastReport is { } report)
        {
            Apply(report);
            return;
        }

        _ = ScanAsync();
    }

    private void OnAnalysisCompleted(AnalysisReport report)
    {
        var app = Application.Current;
        if (app is null || app.Dispatcher.CheckAccess())
        {
            Apply(report);
            return;
        }

        app.Dispatcher.BeginInvoke(new Action(() => Apply(report)));
    }

    private async Task ScanAsync()
    {
        if (_busy) return;

        _busy = true;
        _scan.RaiseCanExecuteChanged();
        Status = "Сканирую установленные программы и процессы…";
        try
        {
            var report = await SmartAnalysisService.ScanAsync();
            Apply(report);
        }
        catch (Exception ex)
        {
            Status = "Ошибка анализа: " + ex.Message;
        }
        finally
        {
            _busy = false;
            _scan.RaiseCanExecuteChanged();
        }
    }

    private void Apply(AnalysisReport report)
    {
        if (ReferenceEquals(_applied, report)) return;
        _applied = report;

        Recommendations.Clear();
        foreach (var item in report.Programs)
        {
            var days = Math.Max(0, (int)(DateTime.UtcNow - item.LastRun).TotalDays);
            var detail = $"Не запускалось {days} дн. · занимает {ByteSize.Format(item.Program.SizeBytes)}";
            if (item.LastAccess is { } access && access < DateTime.UtcNow.AddDays(-1))
                detail += $" · доступ {access.ToLocalTime():dd.MM.yyyy}";

            Recommendations.Add(new RecommendationVm(
                item.Program.Name, "ПРОГРАММА", detail, item.Autostart, null, item.Program, SetStatus));
        }

        Summary = $"Найдено {report.Programs.Count} программ · " +
                  $"автозагрузка: {report.EnabledAutostartCount} из {report.AutostartCount} включено";
        Status = report.Programs.Count == 0
            ? "Неиспользуемых программ не найдено."
            : "Выберите безопасное действие. Изменения выполняются только после нажатия.";
        OnPropertyChanged(nameof(HasRecommendations));
    }

    private void SetStatus(string message) => Status = message;
}

public sealed class SleepViewModel : Observable
{
    private static readonly HashSet<string> GenericFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "application", "bin", "cef", "cef.win32", "cef.win64", "current", "debug",
        "framework", "helper", "helpers", "lib", "libs", "node_modules", "plugins",
        "release", "resources", "runtime", "version", "versions", "win32", "win64", "x64", "x86",
    };

    private readonly RelayCommand _refresh;
    private bool _loaded;
    private bool _busy;
    private string _status = "Список фоновых приложений ещё не загружен.";

    public SleepViewModel()
    {
        _refresh = new RelayCommand(() => _ = RefreshAsync(), () => !_busy);
    }

    public ObservableCollection<SleepAppVm> Groups { get; } = [];
    public RelayCommand RefreshCommand => _refresh;
    public bool IsAvailable => SleepService.Instance.IsAvailable;
    public bool IsEmpty => Groups.Count == 0 && !_busy;

    public string Notice => IsAvailable
        ? string.Empty
        : "Управление сном уже открыто в другом экземпляре GigaCleaner. Действия недоступны.";

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;

        _busy = true;
        _refresh.RaiseCanExecuteChanged();
        Status = "Собираю список приложений…";
        try
        {
            var items = await Task.Run(() =>
            {
                var list = ProcessGuard.GetEligibleProcesses(500).ToList();
                var known = list.Select(item => item.Pid).ToHashSet();
                foreach (var pid in SleepService.Instance.ManagedPids)
                {
                    if (known.Contains(pid)) continue;
                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        string? path = null;
                        try { path = process.MainModule?.FileName; }
                        catch (Exception) { }
                        long memory = 0;
                        try { memory = process.WorkingSet64; }
                        catch (Exception) { }
                        var name = string.IsNullOrWhiteSpace(path)
                            ? process.ProcessName
                            : Path.GetFileNameWithoutExtension(path);
                        list.Add(new UserProcessInfo(pid, process.ProcessName, path, name, memory));
                    }
                    catch (Exception) { }
                }
                return list;
            });

            var groups = items
                .GroupBy(GroupKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => new SleepAppVm(group.Key, group.ToList(), this))
                .OrderByDescending(group => group.TotalMemoryBytes)
                .ThenBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            Groups.Clear();
            foreach (var group in groups) Groups.Add(group);

            var suspended = groups.Count(group => group.IsFullySuspended);
            Status = $"Приложений: {groups.Count} · процессов: {items.Count} · заморожено: {suspended}";
            OnPropertyChanged(nameof(IsAvailable));
            OnPropertyChanged(nameof(Notice));
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex)
        {
            Status = "Ошибка списка процессов: " + ex.Message;
        }
        finally
        {
            _busy = false;
            _refresh.RaiseCanExecuteChanged();
        }
    }

    public async Task LowerAsync(SleepAppVm group)
    {
        var result = await Task.Run(() => ApplyGroup(group, SleepService.Instance.LowerPriority));
        await RefreshAsync();
        Status = result.Message;
    }

    public async Task SuspendAsync(SleepAppVm group)
    {
        var answer = MessageBox.Show(
            $"Усыпить «{group.Name}»?\n\n" +
            $"Будут заморожены все процессы приложения: {group.ProcessCount}.\n" +
            "Приложение перестанет отвечать, пока вы не нажмёте «Разбудить» " +
            "или не закроете GigaCleaner.",
            "Спящий режим",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        var result = await Task.Run(() => ApplyGroup(group, SleepService.Instance.Suspend));
        await RefreshAsync();
        Status = result.Message;
    }

    public async Task ResumeAsync(SleepAppVm group)
    {
        var result = await Task.Run(() => ApplyGroup(group, SleepService.Instance.Resume));
        await RefreshAsync();
        Status = result.Message;
    }

    private static SleepActionResult ApplyGroup(SleepAppVm group, Func<int, SleepActionResult> action)
    {
        var pids = group.Pids.Distinct().ToArray();
        if (pids.Length == 0)
            return new SleepActionResult(false, "В группе не осталось процессов.");

        var succeeded = 0;
        var errors = new List<string>();
        foreach (var pid in pids)
        {
            try
            {
                var result = action(pid);
                if (result.Ok) succeeded++;
                else errors.Add(result.Message);
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }

        return errors.Count == 0
            ? new SleepActionResult(true, $"Обработано процессов: {succeeded}.")
            : new SleepActionResult(false,
                $"Обработано: {succeeded}, ошибок: {errors.Count}. {string.Join("; ", errors.Take(2))}");
    }

    private static string GroupKey(UserProcessInfo item)
    {
        if (string.IsNullOrWhiteSpace(item.ExecutablePath)) return item.Name;

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(item.ExecutablePath));
            if (string.IsNullOrWhiteSpace(directory)) return item.Name;

            for (var depth = 0; depth < 8; depth++)
            {
                var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var name = Path.GetFileName(trimmed);
                if (string.IsNullOrWhiteSpace(name) || !IsGenericFolder(name)) break;

                var parent = Path.GetDirectoryName(trimmed);
                if (string.IsNullOrWhiteSpace(parent) ||
                    string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
                    break;
                directory = parent;
            }

            return directory;
        }
        catch (Exception)
        {
            return item.Name;
        }
    }

    private static bool IsGenericFolder(string name) =>
        GenericFolders.Contains(name) ||
        name.StartsWith("app-", StringComparison.OrdinalIgnoreCase) ||
        name.All(character => char.IsDigit(character) || character == '.');

    internal static string ProcessWord(int count)
    {
        var lastTwo = count % 100;
        if (lastTwo is >= 11 and <= 14) return "процессов";
        return (count % 10) switch
        {
            1 => "процесс",
            2 or 3 or 4 => "процесса",
            _ => "процессов",
        };
    }
}

public sealed class SleepAppVm : Observable
{
    private readonly SleepViewModel _owner;
    private readonly IReadOnlyList<UserProcessInfo> _processes;
    private readonly SleepLevel[] _levels;

    public SleepAppVm(string groupKey, IReadOnlyList<UserProcessInfo> processes, SleepViewModel owner)
    {
        _owner = owner;
        _processes = processes;
        Name = ResolveName(groupKey, processes);
        ProcessCount = processes.Count;
        TotalMemoryBytes = processes.Sum(item => item.WorkingSetBytes);
        _levels = processes
            .Select(item => SleepService.Instance.GetLevel(item.Pid))
            .ToArray();

        LowerCommand = new RelayCommand(() => _ = owner.LowerAsync(this), () => CanLower);
        SuspendCommand = new RelayCommand(() => _ = owner.SuspendAsync(this), () => CanSuspend);
        WakeCommand = new RelayCommand(() => _ = owner.ResumeAsync(this), () => CanWake);
    }

    private static string ResolveName(string groupKey, IReadOnlyList<UserProcessInfo> processes)
    {
        if (Path.IsPathRooted(groupKey))
        {
            var folder = Path.GetFileName(
                groupKey.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(folder)) return folder;
        }

        return processes
            .Select(item => item.FriendlyName)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? Path.GetFileNameWithoutExtension(processes[0].ExecutablePath)
            ?? processes[0].Name;
    }

    public string Name { get; }
    public int ProcessCount { get; }
    public long TotalMemoryBytes { get; }
    public string Summary => $"{ProcessCount} {SleepViewModel.ProcessWord(ProcessCount)} · " +
                             $"{ByteSize.Format(TotalMemoryBytes)} ОЗУ";
    public IReadOnlyList<int> Pids => _processes.Select(item => item.Pid).ToArray();
    public bool IsFullySuspended => _levels.Length > 0 && _levels.All(level => level == SleepLevel.Suspended);
    public RelayCommand LowerCommand { get; }
    public RelayCommand SuspendCommand { get; }
    public RelayCommand WakeCommand { get; }

    public bool CanLower => _owner.IsAvailable && _levels.Any(level => level == SleepLevel.None);
    public bool CanSuspend => _owner.IsAvailable && _levels.Any(level => level != SleepLevel.Suspended);
    public bool CanWake => _owner.IsAvailable && _levels.Any(level => level != SleepLevel.None);

    public string StateText
    {
        get
        {
            if (_levels.Length == 0) return "Активен";
            if (_levels.All(level => level == SleepLevel.Suspended)) return "Заморожен";
            if (_levels.All(level => level == SleepLevel.LowPriority)) return "Низкий приоритет";
            if (_levels.All(level => level == SleepLevel.None)) return "Активен";
            return "Смешанное состояние";
        }
    }
}

public sealed class AutostartViewModel : Observable
{
    private readonly RelayCommand _refresh;
    private bool _loaded;
    private bool _busy;
    private string _status = "Список автозагрузки ещё не загружен.";

    public AutostartViewModel()
    {
        _refresh = new RelayCommand(() => _ = RefreshAsync(), () => !_busy);
        AutostartService.Changed += OnAutostartChanged;
    }

    public ObservableCollection<AutostartEntryVm> Entries { get; } = [];
    public RelayCommand RefreshCommand => _refresh;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _ = RefreshAsync();
    }

    private void OnAutostartChanged()
    {
        var app = Application.Current;
        if (app is null) return;
        if (app.Dispatcher.CheckAccess()) _ = RefreshAsync();
        else app.Dispatcher.BeginInvoke(new Action(() => _ = RefreshAsync()));
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;

        _busy = true;
        _refresh.RaiseCanExecuteChanged();
        Status = "Читаю реестр, папки и Планировщик…";
        try
        {
            var entries = await Task.Run(AutostartService.GetEntries);
            Entries.Clear();
            foreach (var entry in entries)
                Entries.Add(new AutostartEntryVm(entry, this));

            var enabled = entries.Count(entry => entry.Enabled);
            var protectedCount = entries.Count(entry => entry.IsProtected);
            Status = $"{entries.Count} элементов · включено {enabled} · защищено {protectedCount}";
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex)
        {
            Status = "Ошибка чтения автозагрузки: " + ex.Message;
        }
        finally
        {
            _busy = false;
            _refresh.RaiseCanExecuteChanged();
        }
    }

    public bool IsEmpty => Entries.Count == 0 && !_busy;

    public async Task ToggleAsync(AutostartEntryVm item)
    {
        var result = await Task.Run(() => AutostartService.SetEnabled(item.Entry, !item.Entry.Enabled));
        Status = result.Message;
        if (result.Ok)
        {
            item.UpdateEnabled();
            SmartAnalysisService.Invalidate();
        }
    }
}

public sealed class AutostartEntryVm : Observable
{
    public AutostartEntryVm(AutostartEntry entry, AutostartViewModel owner)
    {
        Entry = entry;
        ToggleCommand = new RelayCommand(() => _ = owner.ToggleAsync(this), () => !entry.IsProtected);
    }

    public AutostartEntry Entry { get; }
    public string Name => Entry.Name;
    public string Detail => Entry.Detail;
    public string Source => Entry.Source;
    public bool IsProtected => Entry.IsProtected;
    public bool Enabled => Entry.Enabled;
    public RelayCommand ToggleCommand { get; }

    public string ToggleText => IsProtected ? "Защищено" : Enabled ? "Отключить" : "Включить";
    public string StateText => IsProtected ? "Защищено" : Enabled ? "Включено" : "Отключено";

    public void UpdateEnabled()
    {
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(StateText));
    }
}
