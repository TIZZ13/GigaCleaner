using System.Diagnostics;
using System.IO;

namespace GigaCleaner.Services;

/// <summary>Запускает только штатный деинсталлятор из UninstallString. Папки и ключи не удаляются.</summary>
public static class UninstallLauncher
{
    public static (bool Ok, string Message) Launch(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return (false, "У программы нет команды удаления.");

        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        var (exe, arguments) = Split(expanded);
        if (string.IsNullOrWhiteSpace(exe))
            return (false, "Не удалось разобрать команду удаления.");

        if (Path.GetFileName(exe).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) &&
            arguments.StartsWith("/I", StringComparison.OrdinalIgnoreCase))
            arguments = "/X" + arguments[2..];

        try
        {
            var startInfo = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
            };
            Process.Start(startInfo);
            return (true, "Запущен штатный деинсталлятор. Дождитесь его завершения.");
        }
        catch (Exception ex)
        {
            return (false, "Не удалось запустить деинсталлятор: " + ex.Message);
        }
    }

    private static (string? Executable, string Arguments) Split(string command)
    {
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end <= 1) return (null, string.Empty);
            return (command[1..end], command[(end + 1)..].Trim());
        }

        var exeEnd = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeEnd >= 0)
        {
            var end = exeEnd + 4;
            return (command[..end].Trim(), command[end..].Trim());
        }

        var space = command.IndexOf(' ');
        return space < 0
            ? (command, string.Empty)
            : (command[..space], command[(space + 1)..].Trim());
    }
}
