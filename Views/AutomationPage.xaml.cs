using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GigaCleaner.ViewModels;

namespace GigaCleaner.Views;

public partial class AutomationPage : System.Windows.Controls.UserControl
{
    private static readonly Regex DigitsOnly = new("^[0-9]$", RegexOptions.Compiled);

    public AutomationPage()
    {
        InitializeComponent();
        DataContext = new AutomationViewModel();
    }

    private void OnTimeInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !DigitsOnly.IsMatch(e.Text);
    }

    private void OnTimeLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (box.Text.Length == 1) box.Text = "0" + box.Text;
    }
}