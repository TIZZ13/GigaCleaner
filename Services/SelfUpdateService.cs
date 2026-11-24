using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace GigaCleaner.Services;

/// <summary>
/// Автообновление приложения: по URL проверяет файл version.json
/// вида { "version": "1.0.1", "setup": "https://.../GigaCleaner-Setup-1.0.1.exe" }
/// и при наличии новой версии предлагает скачать и запустить установщик.
/// </summary>
public static class SelfUpdateService
{
    /// <summary>
    /// Адрес файла version.json вида { "version": "1.0.1", "setup": "https://.../GigaCleaner-Setup-1.0.1.exe" }.
    /// Хостится на GitHub Pages: https://tizz13.github.io/GigaCleaner-Site/version.json
    /// </summary>
    public static string VersionUrl { get; set; } = "https://tizz13.github.io/GigaCleaner-Site/version.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public sealed record UpdateInfo(string Version, string SetupUrl);

    /// <summary>Проверяет серверную версию и при наличии обновления спрашивает пользователя.</summary>
    public static async Task<bool> CheckAndPromptAsync(Window owner)
    {
        try
        {
            var update = await FetchAsync().ConfigureAwait(true);
            if (update is null)
            {
                MessageBox.Show(owner, "Не удалось проверить обновления. Проверьте подключение к интернету.",
                    "GigaCleaner", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            if (!IsNewer(update.Version))
            {
                MessageBox.Show(owner, "Установлена последняя версия.",
                    "GigaCleaner", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            var answer = MessageBox.Show(
                $"Доступна новая версия GigaCleaner {update.Version}.\n\nСкачать установщик?",
                "Обновление", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return false;

            await DownloadAndLaunchAsync(update.SetupUrl).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("Self-update failed: " + ex);
            MessageBox.Show(owner, "Не удалось проверить обновления: " + ex.Message,
                "GigaCleaner", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>
    /// Тихая проверка при старте: не показывает ошибок и окон, если всё актуально.
    /// При наличии новой версии — предлагает скачать (окно поверх главного).
    /// </summary>
    public static async Task CheckQuietAsync()
    {
        try
        {
            var update = await FetchAsync().ConfigureAwait(true);
            if (update is null || !IsNewer(update.Version)) return;

            var answer = MessageBox.Show(
                $"Доступна новая версия GigaCleaner {update.Version}.\n\nСкачать установщик?",
                "Обновление", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            await DownloadAndLaunchAsync(update.SetupUrl).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Write("Quiet self-update check failed: " + ex);
        }
    }

    private static bool IsNewer(string version)
    {
        var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        return Version.TryParse(version, out var available)
            && string.IsNullOrWhiteSpace(VersionUrl) == false
            && available > current;
    }

    private static async Task<UpdateInfo?> FetchAsync()
    {
        if (string.IsNullOrWhiteSpace(VersionUrl)) return null;
        using var response = await Http.GetAsync(VersionUrl).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        var root = doc.RootElement;
        var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
        var setup = root.TryGetProperty("setup", out var s) ? s.GetString() : null;
        return string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(setup)
            ? null
            : new UpdateInfo(version!, setup!);
    }

    private static async Task DownloadAndLaunchAsync(string url)
    {
        var setupPath = Path.Combine(Path.GetTempPath(), "GigaCleaner-Setup.exe");
        var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
        await File.WriteAllBytesAsync(setupPath, bytes).ConfigureAwait(false);
        Process.Start(new ProcessStartInfo(setupPath) { UseShellExecute = true });
    }
}