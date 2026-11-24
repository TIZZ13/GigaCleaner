using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GigaCleaner.ViewModels;

namespace GigaCleaner.Views;

public partial class AutostartTab : UserControl
{
    private readonly AutostartViewModel _viewModel;

    public AutostartTab()
    {
        InitializeComponent();
        _viewModel = new AutostartViewModel();
        DataContext = _viewModel;
        Loaded += OnLoaded;
        IsVisibleChanged += OnVisibleChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => QueueLoad();

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) QueueLoad();
    }

    private void QueueLoad()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsVisible) _viewModel.EnsureLoaded();
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private static string? ExtractExecutable(string command)
    {
        var text = command.Trim();
        if (text.Length == 0) return null;

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var path = text.Split(' ', 2)[0];
        return path.Contains("\\") ? path : null;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (!TryGetEntry(sender, out var entry)) return;
        var exe = ExtractExecutable(entry.Detail);
        var folder = exe is null ? null : Path.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); }
        catch (Exception) { }
    }

    private void OnLaunch(object sender, RoutedEventArgs e)
    {
        if (!TryGetEntry(sender, out var entry)) return;
        var exe = ExtractExecutable(entry.Detail);
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    private static bool TryGetEntry(object sender, out AutostartEntryVm entry)
    {
        entry = null!;
        if (sender is not MenuItem item) return false;
        if (item.DataContext is not AutostartEntryVm vm) return false;
        if (vm.IsProtected) return false;
        entry = vm;
        return true;
    }
}
