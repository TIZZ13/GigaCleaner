using System.IO;
using System.Text.Json;

namespace GigaCleaner.Services;

public sealed class AppSettings
{
    public List<string>? CookieWhitelist { get; set; }
    public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Off;
    public int ScheduleHour { get; set; } = 12;
    public int ScheduleMinute { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.Light;
    public int HotkeyModifiers { get; set; } = 4;   // MOD_SHIFT = 0x0004
    public int HotkeyKey { get; set; } = (int)System.Windows.Forms.Keys.C;
    public List<WindowsServiceBackup>? ServiceBackups { get; set; }
    public bool? HibernationWasEnabled { get; set; }
    public bool? AutostartPromptShown { get; set; }
}

public sealed class WindowsServiceBackup
{
    public string Name { get; set; } = string.Empty;
    public int StartMode { get; set; }
    public bool WasRunning { get; set; }
}

public sealed record SavedSchedule(ScheduleFrequency Frequency, int Hour, int Minute);

/// <summary>Настройки приложения в %AppData%\GigaCleaner\settings.json.</summary>
public static class SettingsStore
{
    private static readonly object Gate = new();
    private static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GigaCleaner");
    private static readonly string SettingsFile = Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly string[] Defaults =
    [
        "google.com", "youtube.com", "vk.com", "yandex.ru", "ya.ru",
        "mail.ru", "steamcommunity.com", "steampowered.com", "github.com", "discord.com",
    ];

    private static AppSettings? _settings;

    public static IReadOnlyList<string> CookieWhitelist
    {
        get
        {
            lock (Gate) return EnsureLoaded().CookieWhitelist!.ToArray();
        }
    }

    public static SavedSchedule Schedule
    {
        get
        {
            lock (Gate)
            {
                var settings = EnsureLoaded();
                return new SavedSchedule(settings.Frequency, settings.ScheduleHour, settings.ScheduleMinute);
            }
        }
    }

    public static AppTheme Theme
    {
        get
        {
            lock (Gate) return EnsureLoaded().Theme;
        }
    }

    public static IReadOnlyList<WindowsServiceBackup> ServiceBackups
    {
        get
        {
            lock (Gate)
            {
                return EnsureLoaded().ServiceBackups?
                    .Select(item => new WindowsServiceBackup
                    {
                        Name = item.Name,
                        StartMode = item.StartMode,
                        WasRunning = item.WasRunning,
                    })
                    .ToArray() ?? [];
            }
        }
    }

    public static WindowsServiceBackup? GetServiceBackup(string name)
    {
        lock (Gate)
        {
            var item = EnsureLoaded().ServiceBackups?
                .FirstOrDefault(backup => string.Equals(backup.Name, name, StringComparison.OrdinalIgnoreCase));
            return item is null
                ? null
                : new WindowsServiceBackup
                {
                    Name = item.Name,
                    StartMode = item.StartMode,
                    WasRunning = item.WasRunning,
                };
        }
    }

    public static void SaveServiceBackup(string name, int startMode, bool wasRunning)
    {
        lock (Gate)
        {
            var settings = EnsureLoaded();
            settings.ServiceBackups ??= [];
            var existing = settings.ServiceBackups
                .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                settings.ServiceBackups.Add(new WindowsServiceBackup
                {
                    Name = name,
                    StartMode = startMode,
                    WasRunning = wasRunning,
                });
            }
            else
            {
                existing.StartMode = startMode;
                existing.WasRunning = wasRunning;
            }
            Save(settings);
        }
    }

    public static void RemoveServiceBackup(string name)
    {
        lock (Gate)
        {
            var settings = EnsureLoaded();
            if (settings.ServiceBackups is null) return;
            var removed = settings.ServiceBackups.RemoveAll(
                item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) Save(settings);
        }
    }

    public static bool? HibernationWasEnabled
    {
        get
        {
            lock (Gate) return EnsureLoaded().HibernationWasEnabled;
        }
    }

    public static void SaveHibernationBackup(bool wasEnabled)
    {
        lock (Gate)
        {
            var settings = EnsureLoaded();
            settings.HibernationWasEnabled = wasEnabled;
            Save(settings);
        }
    }

    public static void ClearHibernationBackup()
    {
        lock (Gate)
        {
            var settings = EnsureLoaded();
            if (settings.HibernationWasEnabled is null) return;
            settings.HibernationWasEnabled = null;
            Save(settings);
        }
    }

    public static void SaveTheme(AppTheme theme)
    {
        lock (Gate)
        {
            var settings = EnsureLoaded();
            settings.Theme = Enum.IsDefined(typeof(AppTheme), theme) ? theme : AppTheme.Light;
            Save(settings);
        }
    }

    public static bool AutostartPromptShown
    {
        get
        {
            lock (Gate) return EnsureLoaded().AutostartPromptShown == true;
        }
    }

    public static void MarkAutostartPromptShown()
    {
        lock (Gate)
        {
            var settings = EnsureLoaded();
            settings.AutostartPromptShown = true;
            Save(settings);
        }
    }

    public static void SaveCookieWhitelist(IEnumerable<string> domains)
    {
        lock (Gate)
        {
            var settings = EnsureLoaded();
            settings.CookieWhitelist = [.. Clean(domains)];
            Save(settings);
        }
    }

    public static void SaveSchedule(ScheduleFrequency frequency, int hour, int minute)
    {
        lock (Gate)
        {
            var settings = EnsureLoaded();
            settings.Frequency = Enum.IsDefined(typeof(ScheduleFrequency), frequency)
                ? frequency
                : ScheduleFrequency.Off;
            settings.ScheduleHour = Math.Clamp(hour, 0, 23);
            settings.ScheduleMinute = Math.Clamp(minute, 0, 59);
            Save(settings);
        }
    }

    public static (int Modifiers, int Key) Hotkey
    {
        get
        {
            lock (Gate)
            {
                var settings = EnsureLoaded();
                return (settings.HotkeyModifiers, settings.HotkeyKey);
            }
        }
    }

    public static void SaveHotkey(int modifiers, int key)
    {
        lock (Gate)
        {
            var settings = EnsureLoaded();
            settings.HotkeyModifiers = modifiers;
            settings.HotkeyKey = key;
            Save(settings);
        }
    }

    private static AppSettings EnsureLoaded()
    {
        if (_settings is not null) return _settings;

        var settings = new AppSettings();
        try
        {
            if (File.Exists(SettingsFile))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Повреждённые настройки не должны мешать очистке.
        }

        settings.CookieWhitelist = settings.CookieWhitelist is null
            ? [.. Defaults]
            : [.. Clean(settings.CookieWhitelist)];
        if (!Enum.IsDefined(typeof(ScheduleFrequency), settings.Frequency))
            settings.Frequency = ScheduleFrequency.Off;
        if (!Enum.IsDefined(typeof(AppTheme), settings.Theme))
            settings.Theme = AppTheme.Light;
        settings.ScheduleHour = Math.Clamp(settings.ScheduleHour, 0, 23);
        settings.ScheduleMinute = Math.Clamp(settings.ScheduleMinute, 0, 59);

        _settings = settings;
        return settings;
    }

    private static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Folder);
        var temp = SettingsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, SettingsFile, true);
        _settings = settings;
    }

    private static string[] Clean(IEnumerable<string> domains) => domains
        .Select(DomainName.TryNormalize)
        .Where(domain => domain is not null)
        .Select(domain => domain!)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}
