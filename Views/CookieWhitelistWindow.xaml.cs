using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using GigaCleaner.Services;

namespace GigaCleaner.Views;

public partial class CookieWhitelistWindow : Window
{
    private readonly ObservableCollection<DomainOption> _domains = [];

    public CookieWhitelistWindow()
    {
        InitializeComponent();
        foreach (var domain in SettingsStore.CookieWhitelist)
            _domains.Add(new DomainOption(domain, true));
        DomainsList.ItemsSource = _domains;
    }

    private void OnAddDomain(object sender, RoutedEventArgs e) => AddDomain();

    private void OnDomainKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddDomain();
        e.Handled = true;
    }

    private void AddDomain()
    {
        var normalized = DomainName.TryNormalize(DomainInput.Text);
        if (normalized is null)
        {
            ShowError("Введите домен, например example.com");
            return;
        }

        if (_domains.Any(option => option.Domain == normalized))
        {
            ShowError("Этот домен уже в списке");
            return;
        }

        _domains.Add(new DomainOption(normalized, true));
        DomainInput.Text = string.Empty;
        HideError();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            SettingsStore.SaveCookieWhitelist(
                _domains.Where(option => option.IsChecked).Select(option => option.Domain));
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось сохранить: {ex.Message}");
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Экспорт белого списка",
            FileName = "cookie-whitelist.txt",
            Filter = "Текстовый файл (*.txt)|*.txt",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllLines(dialog.FileName,
                _domains.Where(option => option.IsChecked).Select(option => option.Domain));
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось экспортировать: {ex.Message}");
        }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Импорт белого списка",
            Filter = "Текстовый файл (*.txt)|*.txt|Все файлы (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var imported = File.ReadAllLines(dialog.FileName)
                .Select(line => DomainName.TryNormalize(line))
                .Where(domain => domain is not null)
                .Select(domain => domain!)
                .Where(domain => _domains.All(option => option.Domain != domain))
                .ToList();
            foreach (var domain in imported)
                _domains.Add(new DomainOption(domain, true));
            HideError();
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось импортировать: {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
}

public sealed class DomainOption(string domain, bool isChecked)
{
    public string Domain { get; } = domain;
    public bool IsChecked { get; set; } = isChecked;
}
