using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using GigaCleaner.Services;
using Size = GigaCleaner.Services.Size;

namespace GigaCleaner.ViewModels;

public sealed class ToolsViewModel : Observable
{
    private readonly RelayCommand _rescanPrograms;
    private readonly RelayCommand<InstalledProgramVm> _uninstallProgram;
    private readonly RelayCommand _scanDisk;
    private readonly RelayCommand _cancelDisk;
    private readonly RelayCommand<DiskFolderVm> _openDiskFolder;
    private readonly RelayCommand _findDuplicates;
    private readonly RelayCommand _cancelDuplicates;
    private readonly RelayCommand<DuplicateGroupVm> _openDuplicateFile;
    private readonly RelayCommand _refreshRestore;
    private readonly RelayCommand _createRestore;
    private readonly RelayCommand _openRestoreProtection;
    private readonly RelayCommand _openDiskManagement;
    private readonly RelayCommand _openStorageSettings;

    private List<InstalledProgramVm> _allPrograms = [];
    private bool _loaded;
    private bool _programsBusy;
    private bool _diskBusy;
    private bool _duplicatesBusy;
    private bool _restoreBusy;
    private string _programSearch = "";
    private string _programStatus = "Список ещё не загружен.";
    private string _diskPath = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
    private string _diskStatus = "Выберите диск и нажмите «Сканировать».";
    private string _diskSummary = "";
    private string _duplicatePath = GetDefaultDuplicatePath();
    private string _duplicateStatus = "Укажите папку и нажмите «Найти дубликаты».";
    private string _duplicateSummary = "";
    private string _restoreStatus = Elevation.IsAdministrator
        ? "Нажмите «Обновить», чтобы прочитать точки восстановления."
        : "Для точек восстановления нужны права администратора.";
    private string _eraseStatus = "";
    private CancellationTokenSource? _diskCancel;
    private CancellationTokenSource? _duplicateCancel;

    public ToolsViewModel()
    {
        _rescanPrograms = new RelayCommand(() => _ = ScanProgramsAsync(), () => !_programsBusy);
        _uninstallProgram = new RelayCommand<InstalledProgramVm>(
            UninstallProgram, item => !_programsBusy && item is not null);
        _scanDisk = new RelayCommand(() => _ = ScanDiskAsync(), () => !_diskBusy);
        _cancelDisk = new RelayCommand(CancelDisk, () => _diskBusy);
        _openDiskFolder = new RelayCommand<DiskFolderVm>(OpenDiskFolder, item => item is not null);
        _findDuplicates = new RelayCommand(() => _ = FindDuplicatesAsync(), () => !_duplicatesBusy);
        _cancelDuplicates = new RelayCommand(CancelDuplicates, () => _duplicatesBusy);
        _openDuplicateFile = new RelayCommand<DuplicateGroupVm>(
            OpenDuplicateFile, item => item is not null && item.Group.Files.Count > 0);
        _refreshRestore = new RelayCommand(() => _ = RefreshRestoreAsync(), () => !_restoreBusy);
        _createRestore = new RelayCommand(() => _ = CreateRestoreAsync(),
            () => !_restoreBusy && CanCreateRestorePoint);
        _openRestoreProtection = new RelayCommand(OpenRestoreProtection);
        _openDiskManagement = new RelayCommand(() => OpenSystemTool("diskmgmt.msc"));
        _openStorageSettings = new RelayCommand(() => OpenSystemTool("ms-settings:storagesense"));
    }

    public ObservableCollection<InstalledProgramVm> Programs { get; } = [];
    public ObservableCollection<DiskFolderVm> DiskFolders { get; } = [];
    public ObservableCollection<DuplicateGroupVm> DuplicateGroups { get; } = [];
    public ObservableCollection<RestorePointVm> RestorePoints { get; } = [];

    public RelayCommand RescanProgramsCommand => _rescanPrograms;
    public RelayCommand<InstalledProgramVm> UninstallProgramCommand => _uninstallProgram;
    public RelayCommand ScanDiskCommand => _scanDisk;
    public RelayCommand CancelDiskCommand => _cancelDisk;
    public RelayCommand<DiskFolderVm> OpenDiskFolderCommand => _openDiskFolder;
    public RelayCommand FindDuplicatesCommand => _findDuplicates;
    public RelayCommand CancelDuplicatesCommand => _cancelDuplicates;
    public RelayCommand<DuplicateGroupVm> OpenDuplicateFileCommand => _openDuplicateFile;
    public RelayCommand RefreshRestoreCommand => _refreshRestore;
    public RelayCommand CreateRestorePointCommand => _createRestore;
    public RelayCommand OpenRestoreProtectionCommand => _openRestoreProtection;
    public RelayCommand OpenDiskManagementCommand => _openDiskManagement;
    public RelayCommand OpenStorageSettingsCommand => _openStorageSettings;

    public bool IsEmptyPrograms => Programs.Count == 0 && !_programsBusy;

    public string ProgramSearch
    {
        get => _programSearch;
        set
        {
            if (Set(ref _programSearch, value)) ApplyProgramFilter();
        }
    }

    public string ProgramStatus
    {
        get => _programStatus;
        private set => Set(ref _programStatus, value);
    }

    public string ProgramCountText =>
        $"{Programs.Count} из {_allPrograms.Count}";

    public string DiskPath
    {
        get => _diskPath;
        set => Set(ref _diskPath, value);
    }

    public string DiskStatus
    {
        get => _diskStatus;
        private set => Set(ref _diskStatus, value);
    }

    public string DiskSummary
    {
        get => _diskSummary;
        private set => Set(ref _diskSummary, value);
    }

    public string DuplicatePath
    {
        get => _duplicatePath;
        set => Set(ref _duplicatePath, value);
    }

    public string DuplicateStatus
    {
        get => _duplicateStatus;
        private set => Set(ref _duplicateStatus, value);
    }

    public string DuplicateSummary
    {
        get => _duplicateSummary;
        private set => Set(ref _duplicateSummary, value);
    }

    public string RestoreStatus
    {
        get => _restoreStatus;
        private set => Set(ref _restoreStatus, value);
    }

    public string EraseStatus
    {
        get => _eraseStatus;
        private set => Set(ref _eraseStatus, value);
    }

    public bool CanCreateRestorePoint => Elevation.IsAdministrator;

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _ = ScanProgramsAsync();
    }

    private async Task ScanProgramsAsync()
    {
        if (_programsBusy) return;

        _programsBusy = true;
        RefreshProgramCommands();
        ProgramStatus = "Читаю список установленных программ…";
        try
        {
            var apps = await Task.Run(() => InstalledProgramsService.Scan());
            _allPrograms = apps.Select(app => new InstalledProgramVm(app)).ToList();
            ApplyProgramFilter();
            ProgramStatus = apps.Count == 0
                ? "Установленные программы не найдены."
                : "Выберите программу и запустите её штатный деинсталлятор.";
        }
        catch (Exception ex)
        {
            ProgramStatus = "Не удалось прочитать список программ: " + ex.Message;
        }
        finally
        {
            _programsBusy = false;
            RefreshProgramCommands();
        }
    }

    private void ApplyProgramFilter()
    {
        var query = ProgramSearch.Trim();
        Programs.Clear();
        foreach (var item in _allPrograms)
        {
            if (query.Length > 0 &&
                !item.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                continue;
            Programs.Add(item);
        }

        OnPropertyChanged(nameof(ProgramCountText));
        OnPropertyChanged(nameof(IsEmptyPrograms));
        RefreshProgramCommands();
    }

    private void UninstallProgram(InstalledProgramVm item)
    {
        var answer = MessageBox.Show(
            $"Запустить удаление «{item.Name}»?\n\n" +
            "Откроется штатный деинсталлятор программы. GigaCleaner не удаляет файлы и ключи реестра сам.",
            "Удаление программы",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        var result = UninstallLauncher.Launch(item.App.UninstallString);
        ProgramStatus = result.Ok
            ? result.Message + " После завершения нажмите «Обновить список»."
            : result.Message;
    }

    private async Task ScanDiskAsync()
    {
        if (_diskBusy) return;

        var path = DiskPath.Trim().Trim('"');
        if (!Directory.Exists(path))
        {
            DiskStatus = "Папка или диск не найдены. Укажите существующий путь, например C:\\.";
            return;
        }

        _diskBusy = true;
        _diskCancel = new CancellationTokenSource();
        RefreshDiskCommands();
        DiskFolders.Clear();
        DiskSummary = "";
        DiskStatus = "Считаю размеры папок…";
        try
        {
            var progress = new Progress<string>(message => DiskStatus = message);
            var token = _diskCancel.Token;
            var result = await Task.Run(
                () => DiskAnalyzerService.Analyze(path, progress, token), token);

            foreach (var folder in result.Folders)
                DiskFolders.Add(new DiskFolderVm(folder));
            DiskSummary = $"{Size.Format(result.TotalBytes)} · {result.TotalFiles:N0} файлов";
            DiskStatus = result.Folders.Count == 0
                ? "В выбранной папке нет доступных файлов или папок."
                : "Готово. Размеры отсортированы по убыванию.";
        }
        catch (OperationCanceledException)
        {
            DiskStatus = "Сканирование отменено.";
        }
        catch (Exception ex)
        {
            DiskStatus = "Не удалось проанализировать диск: " + ex.Message;
        }
        finally
        {
            _diskCancel.Dispose();
            _diskCancel = null;
            _diskBusy = false;
            RefreshDiskCommands();
        }
    }

    private void CancelDisk()
    {
        _diskCancel?.Cancel();
        DiskStatus = "Останавливаю сканирование…";
    }

    private void OpenDiskFolder(DiskFolderVm item)
    {
        var result = ToolsLauncher.OpenFolder(item.Folder.Path);
        if (!result.Ok) DiskStatus = result.Message;
    }

    private async Task FindDuplicatesAsync()
    {
        if (_duplicatesBusy) return;

        var path = DuplicatePath.Trim().Trim('"');
        if (!Directory.Exists(path))
        {
            DuplicateStatus = "Папка не найдена. Укажите существующий путь.";
            return;
        }

        _duplicatesBusy = true;
        _duplicateCancel = new CancellationTokenSource();
        RefreshDuplicateCommands();
        DuplicateGroups.Clear();
        DuplicateSummary = "";
        DuplicateStatus = "Собираю файлы и сравниваю содержимое…";
        try
        {
            var progress = new Progress<string>(message => DuplicateStatus = message);
            var token = _duplicateCancel.Token;
            var result = await Task.Run(
                () => DuplicateFinderService.Find(path, progress, token), token);

            const int displayLimit = 100;
            foreach (var group in result.Groups.Take(displayLimit))
                DuplicateGroups.Add(new DuplicateGroupVm(group));

            var wasted = result.Groups.Sum(group => group.WastedBytes);
            DuplicateSummary = result.Groups.Count == 0
                ? "Дубликатов не найдено."
                : $"Найдено групп: {result.Groups.Count} · потенциально {Size.Format(wasted)}";
            if (result.Truncated)
                DuplicateSummary += " · проверка остановлена на 20 000 файлах";
            if (result.Groups.Count > displayLimit)
                DuplicateSummary += $" · показаны первые {displayLimit} групп";

            DuplicateStatus = "Готово. Файлы не удаляются автоматически — проверьте копии вручную.";
        }
        catch (OperationCanceledException)
        {
            DuplicateStatus = "Поиск дубликатов отменён.";
        }
        catch (Exception ex)
        {
            DuplicateStatus = "Не удалось найти дубликаты: " + ex.Message;
        }
        finally
        {
            _duplicateCancel.Dispose();
            _duplicateCancel = null;
            _duplicatesBusy = false;
            RefreshDuplicateCommands();
        }
    }

    private void CancelDuplicates()
    {
        _duplicateCancel?.Cancel();
        DuplicateStatus = "Останавливаю поиск…";
    }

    private void OpenDuplicateFile(DuplicateGroupVm item)
    {
        var result = ToolsLauncher.SelectFile(item.Group.Files[0]);
        if (!result.Ok) DuplicateStatus = result.Message;
    }

    private async Task RefreshRestoreAsync()
    {
        if (_restoreBusy) return;

        _restoreBusy = true;
        RefreshRestoreCommands();
        RestoreStatus = "Читаю точки восстановления…";
        try
        {
            var result = await Task.Run(RestorePointService.List);
            RestorePoints.Clear();
            foreach (var point in result.Points)
                RestorePoints.Add(new RestorePointVm(point));
            RestoreStatus = result.Message;
        }
        catch (Exception ex)
        {
            RestoreStatus = "Не удалось прочитать точки: " + ex.Message;
        }
        finally
        {
            _restoreBusy = false;
            RefreshRestoreCommands();
        }
    }

    private async Task CreateRestoreAsync()
    {
        if (!CanCreateRestorePoint)
        {
            RestoreStatus = "Создание точек доступно только с правами администратора.";
            return;
        }

        var answer = MessageBox.Show(
            "Создать точку восстановления Windows?\n\n" +
            "Windows сохранит текущее состояние системных файлов и настроек.",
            "Точка восстановления",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        _restoreBusy = true;
        RefreshRestoreCommands();
        RestoreStatus = "Создаю точку восстановления…";
        try
        {
            var result = await Task.Run(() =>
                RestorePointService.Create("GigaCleaner: контрольная точка"));
            RestoreStatus = result.Message;
            if (result.Ok) await RefreshRestoreAsync();
        }
        catch (Exception ex)
        {
            RestoreStatus = "Не удалось создать точку: " + ex.Message;
        }
        finally
        {
            _restoreBusy = false;
            RefreshRestoreCommands();
        }
    }

    private void OpenRestoreProtection()
    {
        var result = RestorePointService.OpenProtection();
        if (!result.Ok) RestoreStatus = result.Message;
    }

    private void OpenSystemTool(string target)
    {
        var result = ToolsLauncher.Open(target);
        EraseStatus = result.Ok ? "" : result.Message;
    }

    private void RefreshProgramCommands()
    {
        _rescanPrograms.RaiseCanExecuteChanged();
        _uninstallProgram.RaiseCanExecuteChanged();
    }

    private void RefreshDiskCommands()
    {
        _scanDisk.RaiseCanExecuteChanged();
        _cancelDisk.RaiseCanExecuteChanged();
    }

    private void RefreshDuplicateCommands()
    {
        _findDuplicates.RaiseCanExecuteChanged();
        _cancelDuplicates.RaiseCanExecuteChanged();
    }

    private void RefreshRestoreCommands()
    {
        _refreshRestore.RaiseCanExecuteChanged();
        _createRestore.RaiseCanExecuteChanged();
    }

    private static string GetDefaultDuplicatePath()
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads)
            ? downloads
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}

public sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
        => parameter is T value && (canExecute?.Invoke(value) ?? true);

    public void Execute(object? parameter)
    {
        if (parameter is T value) execute(value);
    }

    public void RaiseCanExecuteChanged()
        => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class InstalledProgramVm(InstalledApp app)
{
    public InstalledApp App { get; } = app;
    public string Name => App.Name;
    public string PublisherText => string.IsNullOrWhiteSpace(App.Publisher)
        ? "Издатель не указан"
        : App.Publisher;
    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(App.Version)) parts.Add("Версия " + App.Version);
            if (App.SizeBytes > 0) parts.Add(Size.Format(App.SizeBytes));
            if (App.InstallDate is { } date) parts.Add("от " + date.ToString("dd.MM.yyyy"));
            return string.Join(" · ", parts);
        }
    }

    public string SearchText => Name + " " + App.Publisher + " " + App.Version;
}

public sealed class DiskFolderVm(DiskFolder folder)
{
    public DiskFolder Folder { get; } = folder;
    public string Name => Folder.Name;
    public string Path => Folder.Path;
    public string SizeText => Size.Format(Folder.Bytes);
    public string FilesText => $"{Folder.Files:N0} файлов";
}

public sealed class DuplicateGroupVm(DuplicateGroup group)
{
    public DuplicateGroup Group { get; } = group;
    public string Title => $"{Group.Files.Count} копии · по {Size.Format(Group.Size)}";
    public string WastedText => "Можно освободить " + Size.Format(Group.WastedBytes);
    public string FilesPreview
    {
        get
        {
            var visible = string.Join(Environment.NewLine, Group.Files.Take(5));
            return Group.Files.Count > 5
                ? visible + Environment.NewLine + $"… ещё {Group.Files.Count - 5}"
                : visible;
        }
    }
}

public sealed class RestorePointVm(RestorePointInfo point)
{
    public string Description => point.Description;
    public string CreatedText => point.CreatedAt == DateTime.MinValue
        ? "Дата неизвестна"
        : point.CreatedAt.ToString("dd.MM.yyyy HH:mm");
    public string TypeText => point.Type;
    public string SequenceText => "№ " + point.SequenceNumber;
}
