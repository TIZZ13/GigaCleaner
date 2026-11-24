using System.Collections.ObjectModel;
using GigaCleaner.Services;
using ByteSize = GigaCleaner.Services.Size;

namespace GigaCleaner.ViewModels;

public sealed class WindowsOptimizationViewModel : Observable
{
    private readonly RelayCommand _refresh;
    private bool _loaded;
    private bool _busy;
    private string _status = "Сканирую службы и встроенные компоненты…";
    private string _summary = "Проверка выполняется только при открытии вкладки.";

    public WindowsOptimizationViewModel()
    {
        _refresh = new RelayCommand(() => _ = RefreshAsync(), () => !_busy);
    }

    public ObservableCollection<WindowsRecommendationVm> Items { get; } = [];
    public RelayCommand RefreshCommand => _refresh;

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

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
        Status = "Сканирую службы, автозапуск и системные компоненты…";
        try
        {
            var items = await Task.Run(WindowsOptimizationService.Scan);
            Apply(items);
            Status = items.Count == 0
                ? "Неиспользуемых встроенных компонентов не найдено."
                : "Изменения применяются только по кнопке и сохраняют исходное состояние.";
        }
        catch (Exception ex)
        {
            Status = "Ошибка сканирования: " + ex.Message;
        }
        finally
        {
            _busy = false;
            _refresh.RaiseCanExecuteChanged();
        }
    }

    public async Task ToggleAsync(WindowsRecommendationVm item)
    {
        if (_busy) return;

        _busy = true;
        _refresh.RaiseCanExecuteChanged();
        try
        {
            var result = await Task.Run(() => WindowsOptimizationService.Toggle(item.Recommendation));
            Status = result.Message;
            if (!result.Ok) return;

            var items = await Task.Run(WindowsOptimizationService.Scan);
            Apply(items);
        }
        catch (Exception ex)
        {
            Status = "Ошибка изменения: " + ex.Message;
        }
        finally
        {
            _busy = false;
            _refresh.RaiseCanExecuteChanged();
        }
    }

    private void Apply(IReadOnlyList<WindowsRecommendation> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(new WindowsRecommendationVm(item, this));
        Summary = items.Count == 0
            ? "Встроенные функции Windows не требуют вмешательства."
            : $"Найдено рекомендаций: {items.Count}";
    }
}

public sealed class WindowsRecommendationVm
{
    public WindowsRecommendationVm(WindowsRecommendation recommendation, WindowsOptimizationViewModel owner)
    {
        Recommendation = recommendation;
        ToggleCommand = new RelayCommand(() => _ = owner.ToggleAsync(this), () => recommendation.CanToggle);
    }

    public WindowsRecommendation Recommendation { get; }
    public string Title => Recommendation.Title;
    public string Category => Recommendation.Category;
    public string Reason => Recommendation.Reason;
    public string ActionText => Recommendation.ActionText;
    public bool IsApplied => Recommendation.IsApplied;
    public bool CanAct => Recommendation.CanToggle;
    public RelayCommand ToggleCommand { get; }

    public string SavingsText
    {
        get
        {
            if (IsApplied) return "Уже отключено GigaCleaner · можно вернуть одной кнопкой";

            var parts = new List<string>();
            if (Recommendation.MemoryBytes > 0)
                parts.Add($"до {ByteSize.Format(Recommendation.MemoryBytes)} ОЗУ");
            if (Recommendation.DiskBytes > 0)
                parts.Add($"{ByteSize.Format(Recommendation.DiskBytes)} на диске");

            return parts.Count == 0
                ? "Экономия появится после отключения"
                : "Можно освободить: " + string.Join(" · ", parts);
        }
    }
}
