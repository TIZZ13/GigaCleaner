using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace GigaCleaner.Views;

public partial class AboutWindow : Window
{
    /// <summary>Ссылка на сайт/репозиторий — зададим, когда появится; пока ведёт на страницу проекта.</summary>
    private const string ProjectUrl = "https://github.com/";

    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        VersionText.Text = "Версия " + version;
    }

    private void OnOpenSite(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Services.AppLog.Write("Open site failed: " + ex.Message);
        }
    }

    private void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        _ = Services.SelfUpdateService.CheckAndPromptAsync(this);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}