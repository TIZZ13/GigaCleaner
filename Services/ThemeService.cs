using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace GigaCleaner.Services;

public enum AppTheme
{
    Light,
    Dark,
}

public static class ThemeService
{
    private const string PaletteLight = "Themes/Palette.Light.xaml";
    private const string PaletteDark = "Themes/Palette.Dark.xaml";

    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static void Initialize()
    {
        Apply(SettingsStore.Theme, persist: false);
    }

    public static void Toggle()
    {
        Apply(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark, persist: true);
    }

    public static void Apply(AppTheme theme, bool persist)
    {
        if (!Enum.IsDefined(typeof(AppTheme), theme)) theme = AppTheme.Light;

        var app = Application.Current;
        if (app is null)
        {
            Current = theme;
            return;
        }

        try
        {
            var backdrop = Environment.OSVersion.Version.Build >= 22000
                ? WindowBackdropType.Mica
                : WindowBackdropType.None;

            ApplicationThemeManager.Apply(
                theme == AppTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light,
                backdrop,
                updateAccent: false);

            var palette = new ResourceDictionary
            {
                Source = new Uri(theme == AppTheme.Dark ? PaletteDark : PaletteLight, UriKind.Relative),
            };
            var dictionaries = app.Resources.MergedDictionaries;
            var current = dictionaries.FirstOrDefault(dictionary =>
                dictionary.Source?.OriginalString.Contains("Palette.Light.xaml", StringComparison.OrdinalIgnoreCase) == true ||
                dictionary.Source?.OriginalString.Contains("Palette.Dark.xaml", StringComparison.OrdinalIgnoreCase) == true);

            if (current is null) dictionaries.Add(palette);
            else dictionaries[dictionaries.IndexOf(current)] = palette;

            Current = theme;
            if (persist) SettingsStore.SaveTheme(theme);
        }
        catch (Exception ex)
        {
            AppLog.Write("Theme apply failed: " + ex);
        }
    }
}
