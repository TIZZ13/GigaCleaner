using System.Windows;
using System.Windows.Controls;
using GigaCleaner.ViewModels;

namespace GigaCleaner.Views;

public partial class ToolsPage : UserControl
{
    private readonly ToolsViewModel _viewModel;

    public ToolsPage()
    {
        InitializeComponent();
        _viewModel = new ToolsViewModel();
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

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) =>
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(ProgramSearchBox?.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

    // Длинные пути при фокусе иначе скроллятся к концу и остаются обрезанными после потери фокуса.
    private void OnPathGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.CaretIndex = 0;
            box.ScrollToHome();
        }
    }

    private void OnPathLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.CaretIndex = 0;
            box.ScrollToHome();
        }
    }
}
