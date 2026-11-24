namespace GigaCleaner.Services;

public sealed record DriverUpdate(
    string Title,
    string DriverClass,
    string Manufacturer,
    string Model,
    long SizeBytes,
    bool Downloaded);

public sealed record DriverScanResult(
    bool Ok,
    IReadOnlyList<DriverUpdate> Updates,
    string Message);

public static class WindowsUpdateService
{
    public static DriverScanResult ScanDrivers()
    {
        try
        {
            var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (sessionType is null)
                return new DriverScanResult(false, [],
                    "Windows Update недоступен на этой системе.");

            dynamic session = Activator.CreateInstance(sessionType)
                              ?? throw new InvalidOperationException("Не удалось открыть Windows Update.");
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic result = searcher.Search("IsInstalled=0 and Type='Driver'");
            dynamic updates = result.Updates;
            var count = Convert.ToInt32(updates.Count);
            var list = new List<DriverUpdate>(count);

            for (var i = 0; i < count; i++)
            {
                try
                {
                    dynamic update = updates.Item(i);
                    list.Add(new DriverUpdate(
                        Safe(() => (string?)update.Title ?? "", "Драйвер"),
                        Safe(() => (string?)update.DriverClass ?? "", ""),
                        Safe(() => (string?)update.DriverManufacturer ?? "", ""),
                        Safe(() => (string?)update.DriverModel ?? "", ""),
                        Safe(() => Convert.ToInt64(update.MaxDownloadSize), 0),
                        Safe(() => Convert.ToBoolean(update.IsDownloaded), false)));
                }
                catch (Exception ex)
                {
                    AppLog.Write("Driver update read failed: " + ex.Message);
                }
            }

            return new DriverScanResult(true, list,
                list.Count == 0
                    ? "Доступных обновлений драйверов не найдено."
                    : $"Доступно обновлений драйверов: {list.Count}.");
        }
        catch (Exception ex)
        {
            return new DriverScanResult(false, [],
                "Не удалось проверить драйверы Windows Update: " + ex.Message);
        }
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception) { return fallback; }
    }
}
