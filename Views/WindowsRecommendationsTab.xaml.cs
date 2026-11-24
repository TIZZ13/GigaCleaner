using System.Windows;
using System.Windows.Controls;
using GigaCleaner.ViewModels;

namespace GigaCleaner.Views;

public partial class WindowsRecommendationsTab : UserControl
{
    private readonly WindowsOptimizationViewModel _viewModel;

    public WindowsRecommendationsTab()
    {
        InitializeComponent();
        _viewModel = new WindowsOptimizationViewModel();
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
