using System.Text;
using Microsoft.Toolkit.Uwp.Notifications;

namespace GigaCleaner.Notifier;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var messageFile = GetMessageFile(args);
            if (messageFile is null || !File.Exists(messageFile)) return 1;

            var lines = File.ReadAllLines(messageFile, Encoding.UTF8);
            var title = lines.Length > 0 && !string.IsNullOrWhiteSpace(lines[0])
                ? lines[0]
                : "GigaCleaner";
            var body = lines.Length > 1 && !string.IsNullOrWhiteSpace(lines[1])
                ? lines[1]
                : "Очистка завершена.";

            var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
            var builder = new ToastContentBuilder()
                .AddText(title)
                .AddText(body);
            if (File.Exists(iconPath))
                builder.AddAppLogoOverride(new Uri("file:///" + iconPath), ToastGenericAppLogoCrop.Circle);
            builder.Show();

            try { File.Delete(messageFile); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            return 0;
        }
        catch (Exception ex)
        {
            Log(ex);
            return 1;
        }
    }

    private static string? GetMessageFile(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--message-file", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static void Log(Exception ex)
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GigaCleaner", "logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(
                Path.Combine(folder, "notifier.log"),
                $"{DateTime.Now:O} {ex}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch (Exception)
        {
            // Уведомление не должно падать из-за недоступного журнала.
        }
    }
}
