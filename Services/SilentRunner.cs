namespace GigaCleaner.Services;

public static class SilentRunner
{
    public static async Task<CleanReport> RunAsync(CancellationToken ct = default)
    {
        AppLog.Write("Silent cleanup started");

        var engine = new CleanerEngine();
        var targets = CleanCatalog.BackgroundSafe();
        var report = await Task.Run(() => engine.Clean(targets, ct), ct);

        AppLog.Write($"Silent cleanup finished: freed={report.FreedBytes}, deleted={report.Deleted}, " +
                     $"skipped={report.Skipped}, failed={report.Failed}");

        var notification = NotificationService.Notify("GigaCleaner",
            $"Очищено {Size.Format(report.FreedBytes)}. Компьютер работает быстро");
        if (!notification.Ok)
            AppLog.Write("Silent notification failed: " + notification.Error);

        return report;
    }
}
