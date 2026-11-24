using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using GigaCleaner.Services;

namespace GigaCleaner.ViewModels;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class MainViewModel : Observable
{
    private string _selected = "Overview";

    public string Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public string VersionText => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    public string RightsText { get; } = Elevation.IsAdministrator ? "Права администратора" : "Ограниченный режим";
}

public sealed class CleanItem : Observable
{
    private bool _enabled;
    private long _bytes;
    private long _freed;

    public required CleanTarget Target { get; init; }
    public required string Title { get; init; }
    public required string Note { get; init; }

    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (Set(ref _enabled, value)) Changed?.Invoke();
        }
    }

    public long Bytes => _bytes;

    public string SizeText => _bytes > 0 ? Size.Format(_bytes) : "—";

    /// <summary>Сколько освободила последняя очистка именно этой цели (для подписи при наведении).</summary>
    public long Freed => _freed;

    public bool HasFreed => _freed > 0;

    public string FreedText => HasFreed ? $"Освобождено: {Size.Format(_freed)}" : "";

    public event Action? Changed;

    public void SetBytes(long bytes)
    {
        _bytes = bytes;
        OnPropertyChanged(nameof(Bytes));
        OnPropertyChanged(nameof(SizeText));
    }

    public void SetFreed(long freed)
    {
        _freed = freed;
        OnPropertyChanged(nameof(Freed));
        OnPropertyChanged(nameof(HasFreed));
        OnPropertyChanged(nameof(FreedText));
    }
}

public sealed record CleanGroupVm(string Title, string? Hint, IReadOnlyList<CleanItem> Items);

public sealed class CleanViewModel : Observable
{
    private readonly CleanerEngine _engine = new();
    private readonly CancellationTokenSource _cancel = new();
    private string _status = "Нажмите «Сканировать», чтобы оценить объём";
    private bool _busy;

    private bool IsBusyInternal
    {
        get => _busy;
        set
        {
            if (Set(ref _busy, value)) OnPropertyChanged(nameof(IsBusy));
        }
    }

    public CleanViewModel()
    {
        foreach (var group in CleanCatalog.Windows().Concat(CleanCatalog.Browsers()).Concat(CleanCatalog.Applications()))
        {
            if (group.Targets.Count == 0) continue;

            var items = new List<CleanItem>();
            foreach (var target in group.Targets)
            {
                var item = new CleanItem
                {
                    Target = target,
                    Title = target.Title,
                    Note = target.Note,
                    IsEnabled = target.SafeByDefault,
                };
                item.Changed += UpdateTotals;
                items.Add(item);
            }
            Groups.Add(new CleanGroupVm(group.Title, group.Hint, items));
        }

        ScanCommand = new RelayCommand(() => _ = ScanAsync(), () => !_busy);
        CleanCommand = new RelayCommand(() => _ = CleanAsync(), () => !_busy && Items.Any(i => i.IsEnabled));
        UpdateTotals();
    }

    public List<CleanGroupVm> Groups { get; } = [];

    public IEnumerable<CleanItem> Items => Groups.SelectMany(g => g.Items);

    public RelayCommand ScanCommand { get; }
    public RelayCommand CleanCommand { get; }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool IsBusy => _busy;

    private bool _justCleaned;
    public bool JustCleaned
    {
        get => _justCleaned;
        private set => Set(ref _justCleaned, value);
    }

    // Прогресс очистки: доля обработанных целей, null — без прогресса (сканирование/покой).
    private double? _activityProgress;
    public double? ActivityProgress
    {
        get => _activityProgress;
        private set => Set(ref _activityProgress, value);
    }

    public string TotalText => Size.Format(Items.Sum(i => i.Bytes));

    public string SelectionText =>
        $"{Size.Format(Items.Sum(i => i.Bytes))} всего · {Size.Format(Items.Where(i => i.IsEnabled).Sum(i => i.Bytes))} к очистке";

    private void UpdateTotals()
    {
        ScanCommand.RaiseCanExecuteChanged();
        CleanCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(SelectionText));
    }

    private async Task ResetCleanedFlagAsync()
    {
        await Task.Delay(4000);
        JustCleaned = false;
    }

    private async Task ScanAsync()
    {
        IsBusyInternal = true;
        UpdateTotals();
        Status = "Сканирование…";
        ActivityProgress = null;
        try
        {
            var snapshot = Items.ToList();
            var bytes = await Task.Run(() => snapshot
                .Select(i => (Item: i, Bytes: _engine.Measure(i.Target, _cancel.Token)))
                .ToList(), _cancel.Token);

            foreach (var (item, size) in bytes) item.SetBytes(size);
            Status = $"Готово: {TotalText} к очистке";
        }
        catch (OperationCanceledException)
        {
            Status = "Сканирование отменено";
        }
        catch (Exception ex)
        {
            Status = $"Ошибка сканирования: {ex.Message}";
        }
        finally
        {
            IsBusyInternal = false;
            ActivityProgress = null;
            UpdateTotals();
        }
    }

    private async Task CleanAsync()
    {
        IsBusyInternal = true;
        UpdateTotals();
        Status = "Очистка…";
        ActivityProgress = 0;
        try
        {
            var selected = Items.Where(i => i.IsEnabled).Select(i => i.Target).ToList();
            var report = await Task.Run(() => _engine.Clean(selected, _cancel.Token));
            Status = $"Освобождено {Size.Format(report.FreedBytes)} · удалено {report.Deleted} элементов, пропущено {report.Skipped}, не удалось {report.Failed}";
            foreach (var item in Items.Where(i => i.IsEnabled))
            {
                item.SetFreed(report.FreedByTarget.TryGetValue(item.Target, out var freed) ? freed : 0);
                item.SetBytes(0);
            }
            JustCleaned = true;
            ResetCleanedFlagAsync();
        }
        catch (OperationCanceledException)
        {
            Status = "Очистка отменена";
        }
        catch (Exception ex)
        {
            Status = $"Ошибка очистки: {ex.Message}";
        }
        finally
        {
            IsBusyInternal = false;
            ActivityProgress = null;
            UpdateTotals();
        }
    }
}

public sealed class AutomationViewModel : Observable
{
    private readonly RelayCommand _save;
    private readonly RelayCommand _disable;
    private readonly RelayCommand _test;
    private int _frequencyIndex;
    private TimeSpan? _timeOfDay = new(12, 0, 0);
    private string _scheduleStatus = "Проверяю расписание…";
    private string _notificationStatus = "";
    private bool _busy;

    public AutomationViewModel()
    {
        _save = new RelayCommand(() => _ = SaveAsync(), () => !_busy);
        _disable = new RelayCommand(() => _ = DisableAsync(), () => !_busy);
        _test = new RelayCommand(() => _ = TestNotificationAsync(), () => !_busy);
        LoadStatus();
    }

    public RelayCommand SaveCommand => _save;
    public RelayCommand DisableCommand => _disable;
    public RelayCommand TestNotificationCommand => _test;

    public int FrequencyIndex
    {
        get => _frequencyIndex;
        set
        {
            if (!Set(ref _frequencyIndex, Math.Clamp(value, 0, 4))) return;
            OnPropertyChanged(nameof(IsScheduleEnabled));
        }
    }

    public bool IsScheduleEnabled => _frequencyIndex > 0;

    public TimeSpan? TimeOfDay
    {
        get => _timeOfDay;
        set
        {
            if (!Set(ref _timeOfDay, value)) return;
            OnPropertyChanged(nameof(Hour));
            OnPropertyChanged(nameof(Minute));
        }
    }

    public int Hour
    {
        get => _timeOfDay?.Hours ?? 12;
        set => TimeOfDay = new TimeSpan(Math.Clamp(value, 0, 23), _timeOfDay?.Minutes ?? 0, 0);
    }

    public int Minute
    {
        get => _timeOfDay?.Minutes ?? 0;
        set => TimeOfDay = new TimeSpan(_timeOfDay?.Hours ?? 12, Math.Clamp(value, 0, 59), 0);
    }

    public string ScheduleStatus
    {
        get => _scheduleStatus;
        private set => Set(ref _scheduleStatus, value);
    }

    public string NotificationStatus
    {
        get => _notificationStatus;
        private set => Set(ref _notificationStatus, value);
    }

    private void LoadStatus()
    {
        try
        {
            var status = ScheduleService.GetStatus();
            if (status.Exists)
            {
                FrequencyIndex = (int)status.Frequency;
                TimeOfDay = new TimeSpan(status.Hour, status.Minute, 0);
            }
            else
            {
                var saved = SettingsStore.Schedule;
                FrequencyIndex = 0;
                TimeOfDay = new TimeSpan(saved.Hour, saved.Minute, 0);
            }
            ScheduleStatus = Describe(status);
        }
        catch (Exception ex)
        {
            ScheduleStatus = "Не удалось прочитать расписание: " + ex.Message;
        }
    }

    private async Task SaveAsync()
    {
        _busy = true;
        UpdateBusy();
        ScheduleStatus = "Сохраняю расписание…";
        try
        {
            var frequency = (ScheduleFrequency)Math.Clamp(FrequencyIndex, 0, 4);
            var time = TimeOfDay ?? new TimeSpan(12, 0, 0);
            var status = await Task.Run(() => ScheduleService.Apply(frequency, time.Hours, time.Minutes));
            SettingsStore.SaveSchedule(frequency, time.Hours, time.Minutes);

            if (frequency == ScheduleFrequency.Off)
            {
                FrequencyIndex = 0;
                ScheduleStatus = "Расписание отключено.";
                return;
            }

            if (status.Exists)
            {
                FrequencyIndex = (int)status.Frequency;
                TimeOfDay = new TimeSpan(status.Hour, status.Minute, 0);
            }
            ScheduleStatus = "Сохранено. " + Describe(status);
        }
        catch (Exception ex)
        {
            ScheduleStatus = "Не удалось сохранить расписание: " + ex.Message;
        }
        finally
        {
            _busy = false;
            UpdateBusy();
        }
    }

    private async Task DisableAsync()
    {
        _busy = true;
        UpdateBusy();
        ScheduleStatus = "Отключаю…";
        try
        {
            var time = TimeOfDay ?? new TimeSpan(12, 0, 0);
            await Task.Run(() => ScheduleService.Apply(ScheduleFrequency.Off, time.Hours, time.Minutes));
            SettingsStore.SaveSchedule(ScheduleFrequency.Off, time.Hours, time.Minutes);
            FrequencyIndex = 0;
            ScheduleStatus = "Расписание отключено.";
        }
        catch (Exception ex)
        {
            ScheduleStatus = "Не удалось отключить расписание: " + ex.Message;
        }
        finally
        {
            _busy = false;
            UpdateBusy();
        }
    }

    private async Task TestNotificationAsync()
    {
        _busy = true;
        UpdateBusy();
        NotificationStatus = "Отправляю тестовое уведомление…";
        try
        {
            var result = await Task.Run(() => NotificationService.Notify(
                "GigaCleaner", "Проверка уведомлений. Если вы это видите, всё работает."));
            NotificationStatus = result.Ok
                ? "Уведомление отправлено. Если его не видно, проверьте режим «Не беспокоить»."
                : "Не удалось показать уведомление: " + result.Error;
        }
        catch (Exception ex)
        {
            NotificationStatus = "Не удалось показать уведомление: " + ex.Message;
        }
        finally
        {
            _busy = false;
            UpdateBusy();
        }
    }

    private void UpdateBusy()
    {
        _save.RaiseCanExecuteChanged();
        _disable.RaiseCanExecuteChanged();
        _test.RaiseCanExecuteChanged();
    }

    private static string Describe(ScheduleStatus status)
    {
        if (status.Error is not null) return "Ошибка планировщика: " + status.Error;
        if (!status.Exists) return "Расписание не настроено. Очистка запускается только вручную.";
        if (status.Frequency == ScheduleFrequency.Off)
            return "Задача найдена, но расписание не распознано. Сохраните настройки заново.";
        if (!status.Enabled) return "Задача отключена в Планировщике заданий.";
        return status.NextRun is { } next
            ? $"Активно. Следующий запуск: {next:dd.MM.yyyy HH:mm}."
            : "Активно. Ближайший запуск не рассчитан.";
    }
}

public sealed record Tile(string Label, string Value, string Symbol = "");

public sealed class OverviewViewModel : Observable
{
    private readonly CleanerEngine _engine = new();
    private string _summary = "Считаю объём мусора…";
    private string _optimizationText = "Комплексная проверка оптимизации ещё не запускалась.";
    private bool _hasRecommendations;
    private bool _busy;

    public string Greeting => $"Сегодня {DateTime.Now.ToString("d MMMM yyyy")}";

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public string OptimizationText
    {
        get => _optimizationText;
        private set => Set(ref _optimizationText, value);
    }

    public bool HasRecommendations
    {
        get => _hasRecommendations;
        private set => Set(ref _hasRecommendations, value);
    }

    public string Hint =>
        "Очистка работает по белому списку путей: Program Files, AppData\\Roaming, Документы и System32 не затрагиваются. Временные файлы %TEMP% удаляются только старше 24 часов.";

    public ObservableCollection<Tile> Tiles { get; } = [];
    public RelayCommand CleanOneClickCommand { get; }
    public RelayCommand ScanHealthCommand { get; }
    public RelayCommand OpenOptimizeCommand { get; }

    public OverviewViewModel()
    {
        CleanOneClickCommand = new RelayCommand(() => _ = OneClickAsync(), () => !_busy);
        ScanHealthCommand = new RelayCommand(() => _ = ScanHealthAsync(), () => !_busy);
        OpenOptimizeCommand = new RelayCommand(() => NavigationService.Navigate("Optimize"));
        SmartAnalysisService.Completed += OnAnalysisCompleted;
        _ = RefreshAsync();
    }

    private static IReadOnlyList<CleanTarget> SafeTargets() => CleanCatalog.All()
        .Where(target => target.SafeByDefault)
        .ToList();

    private async Task RefreshAsync()
    {
        try
        {
            var windows = CleanCatalog.Windows().SelectMany(g => g.Targets).Where(t => t.SafeByDefault).ToList();
            var apps = CleanCatalog.Applications().SelectMany(g => g.Targets).Where(t => t.SafeByDefault).ToList();
            var browsers = CleanCatalog.Browsers().SelectMany(g => g.Targets).Where(t => t.SafeByDefault).ToList();
            var recycle = windows.First(t => t.Kind == RootKind.RecycleBin);

            var totals = await Task.Run(() =>
            {
                var windowsAndApps = windows.Concat(apps).Sum(t => _engine.Measure(t, CancellationToken.None));
                var browserBytes = browsers.Sum(t => _engine.Measure(t, CancellationToken.None));
                var recycleBytes = _engine.Measure(recycle, CancellationToken.None);
                return (WindowsAndApps: windowsAndApps, Browsers: browserBytes, Recycle: recycleBytes);
            });

            Tiles.Clear();
            Tiles.Add(new Tile("Windows и приложения", Size.Format(totals.WindowsAndApps), "ShieldCheckmark24"));
            Tiles.Add(new Tile("Браузеры", Size.Format(totals.Browsers), "Globe24"));
            Tiles.Add(new Tile("Корзина", Size.Format(totals.Recycle), "Broom24"));
            OnPropertyChanged(nameof(Tiles));

            Summary = $"К уборке в системе: {Size.Format(totals.WindowsAndApps + totals.Browsers)}";
        }
        catch (Exception)
        {
            Summary = "Не удалось оценить объём мусора";
        }
    }

    private async Task ScanHealthAsync()
    {
        if (_busy) return;

        _busy = true;
        RaiseOverviewCommands();
        Summary = "Проверяю мусор, автозагрузку и фоновые процессы…";
        try
        {
            await RefreshAsync();
            var junkSummary = Summary;
            var report = await SmartAnalysisService.ScanAsync();
            UpdateOptimization(report);
            Summary = junkSummary + $" · рекомендаций {report.RecommendationCount}";
        }
        catch (Exception ex)
        {
            Summary = "Комплексная проверка не завершилась: " + ex.Message;
        }
        finally
        {
            _busy = false;
            RaiseOverviewCommands();
        }
    }

    private void OnAnalysisCompleted(AnalysisReport report)
    {
        var app = System.Windows.Application.Current;
        if (app is null || app.Dispatcher.CheckAccess())
        {
            UpdateOptimization(report);
            return;
        }

        app.Dispatcher.BeginInvoke(new Action(() => UpdateOptimization(report)));
    }

    private void UpdateOptimization(AnalysisReport report)
    {
        OptimizationText = $"Автозагрузка: {report.EnabledAutostartCount} из {report.AutostartCount} включено · " +
                           $"рекомендаций: {report.RecommendationCount}";
        HasRecommendations = report.RecommendationCount > 0;
    }

    private void RaiseOverviewCommands()
    {
        CleanOneClickCommand.RaiseCanExecuteChanged();
        ScanHealthCommand.RaiseCanExecuteChanged();
    }

    private async Task OneClickAsync()
    {
        _busy = true;
        RaiseOverviewCommands();
        Summary = "Очищаю…";
        try
        {
            var targets = SafeTargets();
            var report = await Task.Run(() => _engine.Clean(targets, CancellationToken.None));
            var message = $"Освобождено {Size.Format(report.FreedBytes)} · удалено {report.Deleted} элементов";
            await RefreshAsync();
            Summary = message;
        }
        catch (Exception ex)
        {
            Summary = $"Очистка не завершилась: {ex.Message}";
        }
        finally
        {
            _busy = false;
            RaiseOverviewCommands();
        }
    }
}
