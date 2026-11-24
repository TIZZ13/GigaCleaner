using System.Windows;
using GigaCleaner.Services;

namespace GigaCleaner.Views;

public partial class AutostartPromptWindow : Window
{
    private bool _accept;

    public AutostartPromptWindow()
    {
        InitializeComponent();
    }

    public static bool ShowAndApply()
    {
        var window = new AutostartPromptWindow();
        window.ShowDialog();
        SettingsStore.MarkAutostartPromptShown();
        return window._accept;
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        SetResult(true);
    }

    private void OnDecline(object sender, RoutedEventArgs e)
    {
        SetResult(false);
    }

    private void SetResult(bool accept)
    {
        _accept = accept;
        if (accept)
            AutostartService.SetSelfAutostart(true);
        Close();
    }
}