using System.IO;
using System.Text.RegularExpressions;

namespace GigaCleaner.Services;

/// <summary>
/// Белый список целей очистки. Сканирования диска нет: обрабатываются только перечисленные пути.
/// Program Files, AppData\Roaming, Documents, Desktop и System32 в список не входят никогда.
/// </summary>
public static class CleanCatalog
{
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    private static readonly EnumerationOptions Listed =
        new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    private static readonly Regex ThumbDb =
        new(@"^thumbcache_.*\.db$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed record Group(string Key, string Title, string? Hint, IReadOnlyList<CleanTarget> Targets);

    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static CleanTarget Make(IEnumerable<string> paths, string title, string note,
        RootKind kind = RootKind.FolderContents, TimeSpan minAge = default, Regex? fileFilter = null,
        bool safe = true, string? process = null, string? service = null)
        => new()
        {
            Paths = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Title = title,
            Note = note,
            Kind = kind,
            MinAge = minAge,
            FileFilter = fileFilter,
            SafeByDefault = safe,
            ProcessName = process,
            ServiceName = service,
        };

    public static IReadOnlyList<Group> Windows()
    {
        var temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var systemTemp = Path.Combine(Win, "Temp");
        var groups = new List<Group>
        {
            new("temp", "Временные файлы", null,
            [
                Make([temp], "Временные файлы пользователя", "Старше 24 часов, не заняты процессами", minAge: Day),
                Make([systemTemp], "Временные файлы системы", "Старше 24 часов, не заняты процессами", minAge: Day),
            ]),
            new("reports", "Дампы и журналы Windows", null,
            [
                Make([Path.Combine(Win, "Minidump")], "Дампы BSOD", "Отчёты о сбоях"),
                Make([Path.Combine(Win, "LiveKernelReports")], "LiveKernelReports", "Отчёты сбоев драйверов"),
                Make([Path.Combine(Win, "Logs", "CBS")], "Журналы CBS", "Логи обслуживания компонентов"),
                Make([Path.Combine(Win, "Logs", "DISM")], "Журналы DISM", "Логи обслуживания образов"),
                Make([Path.Combine(Win, "Logs", "MoSetup")], "Журналы Setup", "Логи установщика обновлений"),
                Make([Path.Combine(Win, "Logs", "WindowsUpdate")], "Журналы Windows Update", "Старше 24 часов", minAge: Day),
                Make([Path.Combine(Win, "Panther")], "Журналы установки", "Старше 24 часов", minAge: Day),
                Make([Path.Combine(Win, "MEMORY.DMP"), Path.Combine(Win, "MEMORY.TMP")],
                    "MEMORY.DMP", "Полный дамп памяти при сбое", kind: RootKind.FileDelete),
            ]),
            new("recycle", "Корзина и буфер", null,
            [
                new() { Kind = RootKind.RecycleBin, Title = "Корзина", Note = "На всех локальных дисках" },
                new() { Kind = RootKind.Clipboard, Title = "Буфер обмена", Note = "Текущее содержимое" },
            ]),
            new("thumbs", "Кэш эскизов", null,
            [
                Make([Path.Combine(Local, "Microsoft", "Windows", "Explorer")], "Кэш эскизов",
                    "Перестроится автоматически", fileFilter: ThumbDb),
            ]),
        };

        var systemCaches = new List<CleanTarget>
        {
            new() { Kind = RootKind.DnsCache, Title = "Кэш DNS", Note = "Сброс сопоставлений доменов" },
        };
        var updateCache = Path.Combine(Win, "SoftwareDistribution", "Download");
        if (Directory.Exists(updateCache))
            systemCaches.Add(Make([updateCache], "Кэш обновлений Windows",
                "Служба Windows Update будет перезапущена", kind: RootKind.ServiceFiles,
                safe: false, service: "wuauserv"));

        groups.Add(new("system", "Системные кэши", null, systemCaches));
        return groups;
    }

    public static IReadOnlyList<Group> Browsers()
    {
        var groups = new List<Group>();
        Add(ChromiumGroup("chrome", "Google Chrome", Path.Combine(Local, "Google", "Chrome", "User Data"), "chrome"));
        Add(ChromiumGroup("edge", "Microsoft Edge", Path.Combine(Local, "Microsoft", "Edge", "User Data"), "msedge"));
        Add(ChromiumGroup("yandex", "Яндекс Браузер", Path.Combine(Local, "Yandex", "YandexBrowser", "User Data"), "browser"));
        Add(ChromiumGroup("brave", "Brave", Path.Combine(Local, "BraveSoftware", "Brave-Browser", "User Data"), "brave"));
        Add(FirefoxGroup());
        Add(OperaGroup());
        return groups;

        void Add(Group? group)
        {
            if (group is not null) groups.Add(group);
        }
    }

    private static Group? ChromiumGroup(string key, string title, string userData, string process)
    {
        var profiles = ChromiumProfiles(userData);
        if (profiles.Count == 0) return null;

        var cachePaths = profiles
            .SelectMany(p => new[]
            {
                Path.Combine(p, "Cache"),
                Path.Combine(p, "Code Cache"),
                Path.Combine(p, "Service Worker", "CacheStorage"),
            })
            .Concat([Path.Combine(userData, "ShaderCache"), Path.Combine(userData, "GrShaderCache")]);

        var historyPaths = profiles.SelectMany(p => new[]
        {
            Path.Combine(p, "History"), Path.Combine(p, "History-journal"),
            Path.Combine(p, "History-wal"), Path.Combine(p, "History-shm"),
        });

        var cookiePaths = profiles.SelectMany(p => new[]
        {
            Path.Combine(p, "Network", "Cookies"), Path.Combine(p, "Cookies"),
        });

        var autofillPaths = profiles.Select(p => Path.Combine(p, "Web Data"));
        var sessionPaths = profiles.Select(p => Path.Combine(p, "Sessions"));

        var targets = new List<CleanTarget>
        {
            Make(cachePaths, "Кэш", "Временные файлы страниц и скриптов"),
            Make(historyPaths, "История", "Посещённые страницы и загрузки",
                kind: RootKind.FileDelete, process: process),
            Make(cookiePaths, "Cookies", "Вход сохраняется для сайтов из белого списка",
                kind: RootKind.BrowserCookies, process: process),
            Make(autofillPaths, "Автозаполнение", "Формы и адреса; банковские карты не затрагиваются",
                kind: RootKind.BrowserAutofill, process: process),
            Make(sessionPaths, "Сессии", "Открытые вкладки. Отключено по умолчанию",
                process: process, safe: false),
        };

        return new Group(key, title, "История, cookies и автозаполнение очищаются только при закрытом браузере.", targets);
    }

    private static IReadOnlyList<string> ChromiumProfiles(string userData)
    {
        if (!Directory.Exists(userData)) return [];
        try
        {
            return Directory.GetDirectories(userData, "*", Listed)
                .Where(dir =>
                {
                    var name = Path.GetFileName(dir);
                    return name is "Default" or "Guest Profile" ||
                           name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase);
                })
                .ToArray();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static Group? FirefoxGroup()
    {
        var profilesRoot = Path.Combine(Local, "Mozilla", "Firefox", "Profiles");
        if (!Directory.Exists(profilesRoot)) return null;

        try
        {
            var caches = Directory.GetDirectories(profilesRoot, "*", Listed)
                .Select(profile => Path.Combine(profile, "cache2"))
                .Where(Directory.Exists)
                .ToArray();

            if (caches.Length == 0) return null;
            return new Group("firefox", "Firefox",
                "История и cookies не затрагиваются: профиль хранится в AppData\\Roaming и защищён правилом.",
                [Make(caches, "Кэш", "Временные файлы страниц")]);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static Group? OperaGroup()
    {
        var caches = new[]
        {
            Path.Combine(Local, "Opera Software", "Opera Stable", "Cache"),
            Path.Combine(Local, "Opera Software", "Opera Stable", "Code Cache"),
            Path.Combine(Local, "Opera Software", "Opera GX Stable", "Cache"),
            Path.Combine(Local, "Opera Software", "Opera GX Stable", "Code Cache"),
        }.Where(Directory.Exists).ToArray();

        if (caches.Length == 0) return null;
        return new Group("opera", "Opera",
            "История и cookies не затрагиваются: профиль хранится в AppData\\Roaming и защищён правилом.",
            [Make(caches, "Кэш", "Временные файлы страниц")]);
    }

    public static IReadOnlyList<Group> Applications()
    {
        var targets = new List<CleanTarget>();

        Add("Discord", "Кэш и GPU-кэш",
            Path.Combine(Local, "Discord", "Cache"),
            Path.Combine(Local, "Discord", "Code Cache"),
            Path.Combine(Local, "Discord", "GPUCache"));
        Add("Telegram", "Кэш медиафайлов",
            Path.Combine(Local, "Telegram Desktop", "tdata", "user_data", "cache"));
        Add("Visual Studio Code", "Кэш и скомпилированные данные",
            Path.Combine(Local, "Microsoft", "Code", "Cache"),
            Path.Combine(Local, "Microsoft", "Code", "CachedData"));
        Add("Microsoft Office", "Кэш документов",
            Path.Combine(Local, "Microsoft", "Office", "16.0", "OfficeFileCache"));
        Add("Steam", "Веб-кэш клиента",
            Path.Combine(Local, "Steam", "htmlcache"));
        Add("Adobe", "Media Cache",
            Path.Combine(Local, "Adobe", "Common", "Media Cache"));
        Add("NuGet", "Кэш пакетов .NET",
            Path.Combine(Local, "NuGet", "v3-cache"));

        return targets.Count == 0
            ? []
            : [new Group("apps", "Приложения", null, targets)];

        void Add(string title, string note, params string[] paths)
        {
            var existing = paths.Where(Directory.Exists).ToArray();
            if (existing.Length > 0) targets.Add(Make(existing, title, note));
        }
    }

    public static IEnumerable<CleanTarget> All() =>
        Windows().Concat(Browsers()).Concat(Applications()).SelectMany(g => g.Targets);

    /// <summary>Фоновая очистка не стирает личные данные браузеров и буфер обмена.</summary>
    public static IReadOnlyList<CleanTarget> BackgroundSafe() => All()
        .Where(target => target.SafeByDefault &&
                         target.Kind != RootKind.Clipboard &&
                         target.ProcessName is null)
        .ToList();
}
