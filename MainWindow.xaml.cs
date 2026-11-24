using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GigaCleaner.Services;
using GigaCleaner.ViewModels;
using Wpf.Ui.Controls;
using WinForms = System.Windows.Forms;

namespace GigaCleaner;

public partial class MainWindow : FluentWindow
{
    private WinForms.NotifyIcon? _trayIcon;
    private bool _shutdownRequested;

    public MainWindow()
    {
        DataContext = new MainViewModel();
        InitializeComponent();

        // Mica поддерживается только в Windows 11; на Windows 10 нужен плотный фон.
        if (Environment.OSVersion.Version.Build < 22000)
            Background = (Brush)FindResource("AppBackgroundBrush");

        UpdateThemeButton();
        NavigationService.Requested += OnNavigationRequested;
        Closed += OnWindowClosed;
        CreateTrayIcon();
        InitLanguageCombo();
        ApplyLanguage();

        if (DataContext is MainViewModel viewModel)
            viewModel.PropertyChanged += OnPageChanged;

        Loaded += (_, _) => MoveNavIndicator();
        SourceInitialized += (_, _) =>
        {
            HotkeyService.HotkeyPressed += OnHotkeyClean;
            HotkeyService.Register(this);
        };
    }

    private void OnHotkeyClean()
    {
        // В трее (окно скрыто) хоткей не работает.
        if (!IsVisible) return;

        // Показываем окно и запускаем безопасную очистку в фоне.
        Dispatcher.BeginInvoke(() =>
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            Activate();

            _ = Task.Run(() =>
            {
                try
                {
                    var engine = new CleanerEngine();
                    var report = engine.Clean(CleanCatalog.BackgroundSafe(), CancellationToken.None);
                    var notification = NotificationService.Notify("GigaCleaner",
                        $"Очищено {Services.Size.Format(report.FreedBytes)}. Пропущено {report.Skipped}, не удалось {report.Failed}.");
                    if (!notification.Ok)
                        AppLog.Write("Hotkey clean notification failed: " + notification.Error);
                }
                catch (Exception ex)
                {
                    AppLog.Write("Hotkey clean failed: " + ex.Message);
                }
            });
        });
    }

    private void OnNavIndicatorChanged()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(MoveNavIndicator));
    }

    private void MoveNavIndicator()
    {
        if (NavIndicator is null || NavPanel is null) return;

        RadioButton? selected = null;
        foreach (var child in NavPanel.Children)
        {
            if (child is RadioButton radio && radio.IsChecked == true)
            {
                selected = radio;
                break;
            }
        }
        if (selected is null || selected.ActualHeight < 1) return;

        // Пункты и индикатор в одном Grid: позиция пункта относительно NavPanel — та же.
        var pos = selected.TranslatePoint(new Point(0, 0), NavPanel);
        var target = pos.Y;

        NavIndicator.Height = selected.ActualHeight;

        var current = NavIndicator.RenderTransform is TranslateTransform tt ? tt.Y : 0;
        var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        NavIndicator.RenderTransform = new TranslateTransform(0, 0);
        ((TranslateTransform)NavIndicator.RenderTransform).BeginAnimation(
            TranslateTransform.YProperty, animation);
    }

    private void OnPageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Selected)) return;

        // Анимируем контейнер страниц и передвигаем индикатор активного пункта меню.
        if (PagesHost is not null) Glide(PagesHost);
        OnNavIndicatorChanged();
    }

    /// <summary>Плавный переход контейнера страниц: мягкий fade + лёгкий подъём.</summary>
    private static void Glide(FrameworkElement element)
    {
        var duration = TimeSpan.FromMilliseconds(180);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var storyboard = new Storyboard();

        var opacity = new DoubleAnimation(0.6, 1.0, duration) { EasingFunction = ease };
        Storyboard.SetTarget(opacity, element);
        Storyboard.SetTargetProperty(opacity, new PropertyPath(OpacityProperty));
        storyboard.Children.Add(opacity);

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = new TranslateTransform(0, 6);
        var translate = new DoubleAnimation(6, 0, duration) { EasingFunction = ease };
        Storyboard.SetTarget(translate, element.RenderTransform);
        Storyboard.SetTargetProperty(translate, new PropertyPath(TranslateTransform.YProperty));
        storyboard.Children.Add(translate);

        storyboard.Begin();
        storyboard.Completed += (_, _) =>
        {
            element.Opacity = 1;
            element.RenderTransform = null;
        };
    }

    private void CreateTrayIcon()
    {
        _trayIcon = new WinForms.NotifyIcon
        {
            Text = "GigaCleaner",
            Visible = true,
            Icon = LoadTrayIcon(),
            ContextMenuStrip = new WinForms.ContextMenuStrip(),
        };
        _trayIcon.ContextMenuStrip.Items.Add(Services.Localization.T("open"), null, (_, _) => ShowFromTray());
        _trayIcon.ContextMenuStrip.Items.Add(new WinForms.ToolStripSeparator());
        _trayIcon.ContextMenuStrip.Items.Add(Services.Localization.T("exit"), null, (_, _) => ExitFromTray());
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private static System.Drawing.Icon? LoadTrayIcon()
    {
        try
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            return stream is null ? null : new System.Drawing.Icon(stream.Stream);
        }
        catch (Exception)
        {
            return System.Drawing.SystemIcons.Application;
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _shutdownRequested = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        Application.Current.Shutdown();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_shutdownRequested)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private void OnThemeToggle(object sender, RoutedEventArgs e)
    {
        // Короткий crossfade: окно мягко гаснет, тема меняется, затем проявляется.
        const int fadeMs = 110;
        var fade = new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(fadeMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) =>
        {
            ThemeService.Toggle();

            if (Environment.OSVersion.Version.Build < 22000)
                Background = (Brush)FindResource("AppBackgroundBrush");

            UpdateThemeButton();

            var fadeIn = new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(fadeMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            BeginAnimation(OpacityProperty, fadeIn);
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void UpdateThemeButton()
    {
        var dark = ThemeService.Current == AppTheme.Dark;
        ThemeLabel.Text = dark ? "Светлая тема" : "Тёмная тема";
        ThemeIcon.Symbol = dark ? SymbolRegular.WeatherSunny24 : SymbolRegular.WeatherMoon24;
    }

    private void OnHotkeySettings(object sender, RoutedEventArgs e)
    {
        var window = new Views.HotkeySettingsWindow { Owner = this };
        window.ShowDialog();

        var (modifiers, key) = Services.SettingsStore.Hotkey;
        if (HotkeyButton is not null)
            HotkeyButton.Content = "Хоткей: " + Views.HotkeySettingsWindow.FormatCombination(modifiers, key);
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        new Views.AboutWindow { Owner = this }.ShowDialog();
    }

    private void InitLanguageCombo()
    {
        LanguageCombo.Items.Clear();
        foreach (var language in Services.Localization.Supported)
            LanguageCombo.Items.Add(Services.Localization.NameOf(language));
        LanguageCombo.SelectedIndex = (int)Services.Localization.Current;
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageCombo.SelectedIndex < 0) return;
        var language = (Services.AppLanguage)LanguageCombo.SelectedIndex;
        Services.Localization.Set(language);
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        NavOverviewText.Text = Services.Localization.T("nav.overview");
        NavCleanText.Text = Services.Localization.T("nav.clean");
        NavOptimizeText.Text = Services.Localization.T("nav.optimize");
        NavRegistryText.Text = Services.Localization.T("nav.registry");
        NavUpdatesText.Text = Services.Localization.T("nav.updates");
        NavToolsText.Text = Services.Localization.T("nav.tools");
        NavAutomationText.Text = Services.Localization.T("nav.automation");
        ThemeLabel.Text = Services.Localization.T(ThemeService.Current == AppTheme.Dark ? "theme.light" : "theme.dark");
        AboutButton.Content = Services.Localization.T("about");
        var (modifiers, key) = Services.SettingsStore.Hotkey;
        HotkeyButton.Content = Services.Localization.T("hotkey") + ": " + Views.HotkeySettingsWindow.FormatCombination(modifiers, key);
    }

    private void OnNavigationRequested(string page)
    {
        if (DataContext is MainViewModel viewModel) viewModel.Selected = page;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        NavigationService.Requested -= OnNavigationRequested;
        Closed -= OnWindowClosed;
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}