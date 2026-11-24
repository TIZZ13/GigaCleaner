using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using GigaCleaner.Services;

namespace GigaCleaner.ViewModels;

public sealed class UpdatesViewModel : Observable
{
    private readonly RelayCommand _scan;
    private readonly RelayCommand _openWindowsUpdate;
    private bool _loaded;
    private bool _busy;
    private string _status = "Проверяю обновления…";
    private string _driverStatus = "Список драйверов ещё не проверялся.";

    public UpdatesViewModel()
    {
        _scan = new RelayCommand(() => _ = ScanAsync(), () => !_busy);
        _openWindowsUpdate = new RelayCommand(OpenWindowsUpdate);
    }

    public ObservableCollection<DriverUpdateVm> Drivers { get; } = [];

    public RelayCommand ScanCommand => _scan;
    public RelayCommand OpenWindowsUpdateCommand => _openWindowsUpdate;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string DriverStatus
    {
        get => _driverStatus;
        private set => Set(ref _driverStatus, value);
    }

    public bool IsEmpty
    {
        get => Drivers.Count == 0 && !_busy;
    }

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _ = ScanAsync();
    }

    private async Task ScanAsync()
    {
        if (_busy) return;

        _busy = true;
        RefreshCommands();
        Status = "Проверяю драйверы через Windows Update…";
        try
        {
            var driverTask = Task.Run(WindowsUpdateService.ScanDrivers);
            await driverTask;

            ApplyDrivers(await driverTask);
            Status = "Проверка завершена. Драйверы устанавливаются по кнопке в Центре обновления Windows.";
        }
        catch (Exception ex)
        {
            Status = "Ошибка проверки обновлений: " + ex.Message;
        }
        finally
        {
            _busy = false;
            RefreshCommands();
        }
    }

    private void ApplyDrivers(DriverScanResult result)
    {
        Drivers.Clear();
        foreach (var update in result.Updates)
            Drivers.Add(new DriverUpdateVm(update));
        DriverStatus = result.Message;
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void OpenWindowsUpdate()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:windowsupdate") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = "Не удалось открыть Центр обновления Windows: " + ex.Message;
        }
    }

    private void RefreshCommands()
    {
        _scan.RaiseCanExecuteChanged();
    }
}

public sealed class DriverUpdateVm
{
    public DriverUpdateVm(DriverUpdate update)
    {
        Update = update;
    }

    public DriverUpdate Update { get; }
    public string Title => Update.Title;
    public string Details => string.Join(" · ", new[] { Update.DriverClass, Update.Manufacturer, Update.Model }
        .Where(value => !string.IsNullOrWhiteSpace(value)));
    public string SizeText => Update.SizeBytes > 0
        ? $"{GigaCleaner.Services.Size.Format(Update.SizeBytes)}"
        : "размер не указан";
    public string StateText => Update.Downloaded ? "Загружен" : "Будет загружен при установке";
}
