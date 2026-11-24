using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using GigaCleaner.Services;

namespace GigaCleaner.ViewModels;

public sealed class RegistryViewModel : Observable
{
    private readonly RelayCommand _scan;
    private readonly RelayCommand _fix;
    private readonly RelayCommand _openBackups;
    private readonly RelayCommand _selectAll;
    private bool _busy;
    private string _status = "Сканирую реестр…";
    private string _selectionText = "Выбрано 0 записей";

    public RegistryViewModel()
    {
        _scan = new RelayCommand(() => _ = ScanAsync(), () => !_busy);
        _fix = new RelayCommand(() => _ = FixAsync(), () => !_busy && SelectedCount > 0);
        _openBackups = new RelayCommand(OpenBackups);
        _selectAll = new RelayCommand(SelectAll, () => !_busy && Issues.Count > 0);
    }

    public ObservableCollection<RegistryIssueVm> Issues { get; } = [];
    public RelayCommand ScanCommand => _scan;
    public RelayCommand FixCommand => _fix;
    public RelayCommand OpenBackupsCommand => _openBackups;
    public RelayCommand SelectAllCommand => _selectAll;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string SelectionText
    {
        get => _selectionText;
        private set => Set(ref _selectionText, value);
    }

    public bool IsEmpty => Issues.Count == 0 && !_busy;

    public void EnsureLoaded()
    {
        if (_scan.CanExecute(null) && Issues.Count == 0 && _status == "Сканирую реестр…")
            _ = ScanAsync();
    }

    private int SelectedCount => Issues.Count(item => item.IsSelected);

    private async Task ScanAsync()
    {
        if (_busy) return;

        _busy = true;
        RefreshCommands();
        Status = "Сканирую ветки деинсталляции и App Paths…";
        try
        {
            var issues = await Task.Run(RegistryCleanerService.Scan);
            Apply(issues);
            Status = issues.Count == 0
                ? "Битых записей не найдено. Изменения не требуются."
                : "Проверьте выбранные записи. Перед исправлением будет создан .reg-бэкап.";
        }
        catch (Exception ex)
        {
            Status = "Ошибка сканирования реестра: " + ex.Message;
        }
        finally
        {
            _busy = false;
            RefreshCommands();
        }
    }

    private async Task FixAsync()
    {
        var selected = Issues.Where(item => item.IsSelected).Select(item => item.Issue).ToList();
        if (selected.Count == 0) return;

        var answer = MessageBox.Show(
            $"Исправить {selected.Count} записей реестра?\n\n" +
            "Сначала будет создан .reg-бэкап изменяемых веток. " +
            "Без успешного бэкапа исправление не выполняется.",
            "Реестр",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        _busy = true;
        RefreshCommands();
        Status = "Создаю .reg-бэкап и исправляю…";
        try
        {
            var result = await Task.Run(() => RegistryCleanerService.Repair(selected));
            Status = result.Message;
            if (!result.Ok) return;

            var issues = await Task.Run(RegistryCleanerService.Scan);
            Apply(issues);
            Status = result.Message;
        }
        catch (Exception ex)
        {
            Status = "Ошибка исправления реестра: " + ex.Message;
        }
        finally
        {
            _busy = false;
            RefreshCommands();
        }
    }

    private void Apply(IReadOnlyList<RegistryIssue> issues)
    {
        Issues.Clear();
        foreach (var issue in issues)
        {
            var item = new RegistryIssueVm(issue);
            item.Changed += UpdateSelection;
            Issues.Add(item);
        }
        OnPropertyChanged(nameof(IsEmpty));
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        SelectionText = $"Выбрано {SelectedCount} из {Issues.Count}";
        RefreshCommands();
    }

    private void SelectAll()
    {
        foreach (var item in Issues) item.IsSelected = true;
        UpdateSelection();
    }

    private void OpenBackups()
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GigaCleaner", "Backups", "Registry");
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = "Не удалось открыть папку бэкапов: " + ex.Message;
        }
    }

    private void RefreshCommands()
    {
        _scan.RaiseCanExecuteChanged();
        _fix.RaiseCanExecuteChanged();
        _selectAll.RaiseCanExecuteChanged();
    }
}

public sealed class RegistryIssueVm : Observable
{
    private bool _selected;

    public RegistryIssueVm(RegistryIssue issue)
    {
        Issue = issue;
    }

    public RegistryIssue Issue { get; }
    public string Category => Issue.Category;
    public string Title => Issue.Title;
    public string Detail => Issue.Detail;
    public string KeyPath => Issue.FullKeyPath;
    public event Action? Changed;

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value)) Changed?.Invoke();
        }
    }
}
