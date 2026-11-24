using System.IO;
using System.Text;

namespace GigaCleaner.Services;

public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GigaCleaner", "logs");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var file = Path.Combine(Folder, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // Журнал не должен ломать очистку.
        }
    }
}
