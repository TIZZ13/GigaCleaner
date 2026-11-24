using GigaCleaner.ViewModels;

namespace GigaCleaner.Views;

public partial class OverviewPage : System.Windows.Controls.UserControl
{
    public OverviewPage()
    {
        InitializeComponent();
        DataContext = new OverviewViewModel();
    }
}
