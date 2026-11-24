using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GigaCleaner.Services;

public sealed record WingetUpdate(
    string Name,
    string Id,
    string CurrentVersion,
    string AvailableVersion,
    string Source);

public sealed record WingetScanResult(
    bool Ok,
    IReadOnlyList<WingetUpdate> Updates,
    string Message);

public sealed record WingetUpdateResult(bool Ok, string Message);

public static class WingetService
{
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    public static WingetScanResult Scan()
    {
        var executable = FindExecutable();
        if (executable is null)
            return new WingetScanResult(false, [],
                "winget не найден. Установите или обновите App Installer из Microsoft Store.");

        var result = Run(executable,
            ["upgrade", "--include-unknown", "--accept-source-agreements",
             "--disable-interactivity", "--source", "winget"], 120000);

        var updates = Parse(result.Stdout);
        if (updates.Count > 0)
        {
            return new WingetScanResult(true, updates,
                $"Доступно обновлений программ: {updates.Count}.");
        }

        if (result.ExitCode == 0)
            return new WingetScanResult(true, [], "Обновлений программ не найдено.");

        return new WingetScanResult(false, [],
            "winget завершился с ошибкой: " + Tail(result.Error));
    }

    public static WingetUpdateResult Update(WingetUpdate update)
    {
        var executable = FindExecutable();
        if (executable is null)
            return new WingetUpdateResult(false, "winget не найден.");

        // Крупные установщики (Epic Games, Adobe и т.п.) качают сотни мегабайт:
        // 5 минут часто не хватает, поэтому даём 15 минут на одну попытку.
        const int timeoutMs = 900000;

        var args = new List<string>
        {
            "upgrade", "--id", update.Id, "--exact", "--silent",
            "--accept-package-agreements", "--accept-source-agreements",
            "--disable-interactivity",
        };
        if (!string.IsNullOrWhiteSpace(update.Source))
        {
            args.Add("--source");
            args.Add(update.Source);
        }

        var result = Run(executable, args, timeoutMs);
        if (result.ExitCode == 0)
            return new WingetUpdateResult(true, $"Обновлено: {update.Name}.");

        // Частая причина MSI-ошибки 1603: запущенный процесс лаунчера (Epic, Steam...)
        // не даёт установщику перезаписать собственные файлы. Закрываем его и повторяем
        // с --force (переустановка поверх чинит частичное состояние).
        var processName = KnownLauncherProcess(update.Id);
        var closed = processName is not null && CloseProcess(processName);

        var forceResult = Run(executable,
            args.Append("--force").ToArray(), timeoutMs);
        if (forceResult.ExitCode == 0)
            return new WingetUpdateResult(true,
                $"Обновлено: {update.Name} (повтор с принудительной установкой" +
                (closed ? "; закрыт процесс " + processName : "") + ").");

        if (processName is not null && !closed)
            return new WingetUpdateResult(false,
                $"{update.Name}: установщик не смог перезаписать файлы, потому что запущен процесс «{processName}». " +
                "Закройте приложение и повторите обновление, либо перезапустите компьютер.");

        var reason = Describe(result, forceResult);
        return new WingetUpdateResult(false, $"{update.Name}: {reason}");
    }

    /// <summary>Для тяжёлых лаунчеров возвращаем имя процесса, который блокирует установщик.</summary>
    private static string? KnownLauncherProcess(string packageId) => packageId switch
    {
        "EpicGames.EpicGamesLauncher" => "EpicGamesLauncher",
        "Valve.Steam" => "steam",
        "Spotify.Spotify" => "Spotify",
        "Discord.Discord" => "Discord",
        "Logitech.GHUB" => "lghub",
        _ => null,
    };

    private static bool CloseProcess(string name)
    {
        try
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(5000))
                        process.Kill();
                }
                catch (Exception) { }
                finally { process.Dispose(); }
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Describe((int ExitCode, string Stdout, string Error) first,
        (int ExitCode, string Stdout, string Error) second)
    {
        // winget пишет причину сбоя в stdout (например, "Installer failed with exit code 0x80200010"),
        // stderr при этом может быть пустым — собираем диагностику из обоих потоков.
        foreach (var entry in new[] { first, second })
        {
            var text = (entry.Stdout + "\n" + entry.Error).Trim();
            if (text.Length == 0) continue;

            var interesting = text
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line =>
                    line.Contains("Installer", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("exit code", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("process", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("admin", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("elevat", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("доступ", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("процесс", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("админ", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("installer failed", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (interesting.Count > 0)
                return string.Join(" | ", interesting.Take(3));

            var tail = text.Length <= 300 ? text : text[^300..];
            return tail;
        }

        if (first.ExitCode == -1 || second.ExitCode == -1)
            return "истекло время ожидания winget. Установщик мог не успеть за 15 минут — попробуйте ещё раз или обновите вручную.";

        return "winget завершился с ошибкой без пояснения (код " + first.ExitCode + "). Закройте приложение, которое обновляете, и повторите.";
    }

    public static string? FindExecutable()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(local)) return local;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;

        foreach (var folder in path.Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim(), "winget.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception) { }
        }
        return null;
    }

    private static IReadOnlyList<WingetUpdate> Parse(string output)
    {
        var clean = Ansi.Replace(output, string.Empty).Replace("\r", string.Empty);
        var lines = clean.Split('\n');
        var headerIndex = -1;
        var idStart = -1;
        var versionStart = -1;
        var availableStart = -1;
        var sourceStart = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var id = FindColumn(line, "ИД", "Id");
            var version = FindColumn(line, "Версия", "Version");
            var available = FindColumn(line, "Доступно", "Available");
            if (id < 0 || version < 0 || available < 0) continue;

            headerIndex = i;
            idStart = id;
            versionStart = version;
            availableStart = available;
            sourceStart = FindColumn(line, "Источник", "Source");
            break;
        }

        if (headerIndex < 0 || idStart <= 0 || versionStart <= idStart || availableStart <= versionStart)
            return [];

        var updates = new List<WingetUpdate>();
        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.TrimStart().StartsWith("-")) continue;
            if (line.Length <= availableStart) continue;

            var name = Slice(line, 0, idStart);
            var id = Slice(line, idStart, versionStart);
            var current = Slice(line, versionStart, availableStart);
            var available = sourceStart > 0
                ? Slice(line, availableStart, sourceStart)
                : Slice(line, availableStart, line.Length);
            var source = sourceStart > 0
                ? Slice(line, sourceStart, line.Length)
                : "winget";

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
            if (id.Contains(' ') || available.Contains(' ')) continue;
            if (name == "Имя" || string.Equals(name, "Name", StringComparison.OrdinalIgnoreCase)) continue;

            updates.Add(new WingetUpdate(name, id, current, available,
                string.IsNullOrWhiteSpace(source) ? "winget" : source));
        }

        return updates
            .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static int FindColumn(string header, params string[] names)
    {
        foreach (var name in names)
        {
            var index = header.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) return index;
        }
        return -1;
    }

    private static string Slice(string line, int start, int end)
    {
        if (start >= line.Length) return string.Empty;
        end = Math.Min(end, line.Length);
        return end <= start ? string.Empty : line[start..end].Trim();
    }

    private static (int ExitCode, string Stdout, string Error) Run(
        string executable, IReadOnlyList<string> arguments, int timeoutMs)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process is null) return (-1, string.Empty, "winget не запустился.");

            // Читаем оба потока параллельно: последовательное ReadToEnd по stderr
            // может заблокировать процесс при большом объёме вывода (дедлок).
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch (Exception) { }
                return (-1, stdoutTask.GetAwaiter().GetResult(),
                    "Истекло время ожидания winget.");
            }

            return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(),
                stderrTask.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    private static string Tail(string text)
    {
        var value = text.Trim();
        return value.Length <= 500 ? value : value[^500..];
    }
}
