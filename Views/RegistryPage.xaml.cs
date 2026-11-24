using System.Windows;
using System.Windows.Controls;
using GigaCleaner.ViewModels;

namespace GigaCleaner.Views;

public partial class RegistryPage : UserControl
{
    private readonly RegistryViewModel _viewModel;

    public RegistryPage()
    {
        InitializeComponent();
        _viewModel = new RegistryViewModel();
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
}
