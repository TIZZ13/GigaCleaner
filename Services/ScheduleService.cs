using System.IO;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Win32.TaskScheduler;
using RegisteredTask = Microsoft.Win32.TaskScheduler.Task;

namespace GigaCleaner.Services;

public sealed record ScheduleStatus(
    bool Exists,
    bool Enabled,
    ScheduleFrequency Frequency,
    int Hour,
    int Minute,
    DateTime? NextRun,
    string? Error);

/// <summary>Расписание фоновой очистки: планировщик заданий Windows, RunLevel.Highest, аргумент --silent.</summary>
public static class ScheduleService
{
    public const string CleanTaskName = "GigaCleaner Automatic Cleanup";

    public static ScheduleStatus Apply(ScheduleFrequency frequency, int hour, int minute)
    {
        hour = Math.Clamp(hour, 0, 23);
        minute = Math.Clamp(minute, 0, 59);

        if (frequency == ScheduleFrequency.Off)
        {
            Disable();
            return GetStatus();
        }

        var processPath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("Не удалось определить путь к GigaCleaner.exe.");
        var (exe, arguments) = BuildLaunch(processPath, "--silent");

        using var service = new TaskService();
        using var definition = BuildDefinition(service, frequency, hour, minute, exe, arguments);
        using var registered = service.RootFolder.RegisterTaskDefinition(CleanTaskName, definition);
        return GetStatus();
    }

    internal static TaskDefinition BuildDefinition(TaskService service, ScheduleFrequency frequency,
        int hour, int minute, string executable, string arguments)
    {
        if (frequency == ScheduleFrequency.Off)
            throw new ArgumentOutOfRangeException(nameof(frequency));

        var definition = service.NewTask();
        definition.RegistrationInfo.Description = "GigaCleaner: фоновая очистка компьютера";
        definition.Principal.UserId = WindowsIdentity.GetCurrent().Name;
        definition.Principal.LogonType = TaskLogonType.InteractiveToken;
        definition.Principal.RunLevel = TaskRunLevel.Highest;

        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.StartWhenAvailable = true;
        definition.Settings.ExecutionTimeLimit = TimeSpan.FromHours(1);
        definition.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
        definition.Settings.Enabled = true;

        var start = DateTime.Today.AddHours(hour).AddMinutes(minute);
        if (start <= DateTime.Now) start = start.AddDays(1);

        Trigger trigger = frequency switch
        {
            ScheduleFrequency.Daily => new DailyTrigger(1) { StartBoundary = start },
            ScheduleFrequency.Weekly => new WeeklyTrigger(DaysOfTheWeek.Monday, 1) { StartBoundary = start },
            ScheduleFrequency.Biweekly => new WeeklyTrigger(DaysOfTheWeek.Monday, 2) { StartBoundary = start },
            ScheduleFrequency.Monthly => new MonthlyTrigger(1, MonthsOfTheYear.AllMonths)
            {
                StartBoundary = start,
                DaysOfMonth = [1],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(frequency)),
        };
        definition.Triggers.Add(trigger);
        definition.Actions.Add(new ExecAction(executable, arguments, AppContext.BaseDirectory));
        return definition;
    }

    public static void Disable()
    {
        using var service = new TaskService();
        service.RootFolder.DeleteTask(CleanTaskName, false);
    }

    public static ScheduleStatus GetStatus()
    {
        try
        {
            using var service = new TaskService();
            using var task = service.GetTask(CleanTaskName);
            if (task is null)
                return new ScheduleStatus(false, false, ScheduleFrequency.Off, 12, 0, null, null);

            using var definition = task.Definition;
            var trigger = definition.Triggers.FirstOrDefault();
            var start = trigger?.StartBoundary ?? DateTime.MinValue;
            var hour = start == DateTime.MinValue ? 12 : start.Hour;
            var minute = start == DateTime.MinValue ? 0 : start.Minute;

            var frequency = trigger switch
            {
                DailyTrigger { DaysInterval: 1 } => ScheduleFrequency.Daily,
                WeeklyTrigger { WeeksInterval: 2 } => ScheduleFrequency.Biweekly,
                WeeklyTrigger => ScheduleFrequency.Weekly,
                MonthlyTrigger => ScheduleFrequency.Monthly,
                _ => ScheduleFrequency.Off,
            };

            var next = task.NextRunTime == DateTime.MinValue ? (DateTime?)null : task.NextRunTime;
            return new ScheduleStatus(true, task.Enabled, frequency, hour, minute, next, null);
        }
        catch (Exception ex)
        {
            return new ScheduleStatus(false, false, ScheduleFrequency.Off, 12, 0, null, ex.Message);
        }
    }

    private static (string Executable, string Arguments) BuildLaunch(string processPath, string arguments) =>
        (processPath, arguments);
}
