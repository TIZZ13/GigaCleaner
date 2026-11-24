using System.IO;
using System.Text.RegularExpressions;

namespace GigaCleaner.Services;

public enum RootKind
{
    FolderContents,
    FileDelete,
    RecycleBin,
    Clipboard,
    DnsCache,
    ServiceFiles,
    BrowserCookies,
    BrowserAutofill,
}

/// <summary>Одна цель очистки: проверенные пути или системное действие.</summary>
public sealed class CleanTarget
{
    public IReadOnlyList<string> Paths { get; init; } = [];
    public required string Title { get; init; }
    public required string Note { get; init; }
    public RootKind Kind { get; init; }

    /// <summary>Минимальный возраст файла. Ноль — ограничение не применяется.</summary>
    public TimeSpan MinAge { get; init; }

    /// <summary>Фильтр имени файла. Null — все файлы; иначе только совпавшие.</summary>
    public Regex? FileFilter { get; init; }

    /// <summary>Cookies, сессии и кэш обновлений отключены по умолчанию.</summary>
    public bool SafeByDefault { get; init; } = true;

    /// <summary>Данные браузера трогаем только при закрытом процессе.</summary>
    public string? ProcessName { get; init; }

    /// <summary>Служба, которую нужно остановить перед очисткой и вернуть после.</summary>
    public string? ServiceName { get; init; }
}

public static class Size
{
    public static long OfFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    public static string Format(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} {units[unit]}"
            : value.ToString(unit == 1 ? "0" : "0.0") + $" {units[unit]}";
    }
}
