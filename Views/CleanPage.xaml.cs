using System.Windows;
using GigaCleaner.ViewModels;

namespace GigaCleaner.Views;

public partial class CleanPage : System.Windows.Controls.UserControl
{
    public CleanPage()
    {
        InitializeComponent();
        DataContext = new CleanViewModel();
    }

    private void OnCookieWhitelist(object sender, RoutedEventArgs e)
    {
        var window = new CookieWhitelistWindow { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }
}
