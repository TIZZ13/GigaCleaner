using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GigaCleaner.Services;

public enum SleepLevel
{
    None,
    LowPriority,
    Suspended,
}

public sealed record SleepActionResult(bool Ok, string Message);

/// <summary>
/// Управление сном процессов. Хранит исходные приоритеты и состояние на диске,
/// чтобы разбудить приложения при закрытии или после аварийного завершения GigaCleaner.
/// </summary>
public sealed class SleepService : IDisposable
{
    private const int ProcessSetInformation = 0x0200;
    private const int ProcessQueryInformation = 0x0400;
    private const int ProcessSuspendResume = 0x0800;
    private const int ProcessQueryLimitedInformation = 0x1000;

    private const int ProcessIoPriority = 33;
    private const int IoPriorityVeryLow = 0;
    private const int IoPriorityNormal = 2;
    private const uint IdlePriorityClass = 0x00000040;

    private static readonly Lazy<SleepService> Lazy = new(() => new SleepService());
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly int ActionAccess =
        ProcessSetInformation | ProcessQueryInformation | ProcessQueryLimitedInformation;

    private static readonly int SuspendAccess = ActionAccess | ProcessSuspendResume;

    private readonly object _gate = new();
    private readonly Dictionary<int, SleepRecord> _records = new();
    private readonly string _stateFile;
    private readonly string _lockFile;
    private readonly FileStream? _lock;
    private readonly int _ownerPid;
    private readonly long _ownerStartTicks;
    private bool _disposed;
    private bool _closing;

    public static SleepService Instance => Lazy.Value;

    public SleepService()
    {
        _ownerPid = Environment.ProcessId;
        _ownerStartTicks = GetStartTicks(Process.GetCurrentProcess());

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GigaCleaner");
        _stateFile = Path.Combine(folder, "sleep-state.json");
        _lockFile = Path.Combine(folder, "sleep.lock");

        try
        {
            Directory.CreateDirectory(folder);
            _lock = new FileStream(_lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            IsAvailable = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            IsAvailable = false;
        }

        if (IsAvailable) RecoverOrphans();
    }

    public bool IsAvailable { get; }
    public int RecoveredCount { get; private set; }

    public SleepLevel GetLevel(int pid)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(pid, out var record)) return SleepLevel.None;
            return record.Suspended ? SleepLevel.Suspended : SleepLevel.LowPriority;
        }
    }

    public int ManagedCount
    {
        get
        {
            lock (_gate) return _records.Count;
        }
    }

    public IReadOnlyList<int> ManagedPids
    {
        get
        {
            lock (_gate) return _records.Keys.ToArray();
        }
    }

    public SleepActionResult LowerPriority(int pid)
    {
        if (!IsAvailable) return Fail("Управление сном уже открыто в другом экземпляре GigaCleaner.");

        lock (_gate)
        {
            if (_disposed) return Fail("Служба сна уже остановлена.");
            if (_records.ContainsKey(pid)) return new SleepActionResult(true, "Приоритет уже снижен.");

            using var process = TryGetProcess(pid, out var processError);
            if (process is null) return Fail(processError);

            if (!ProcessGuard.TryGetEligible(process, out _, out var reason)) return Fail(reason);

            var startTicks = GetStartTicks(process);
            if (startTicks == 0) return Fail("Не удалось определить время запуска процесса.");

            var handle = OpenProcess(ActionAccess, false, pid);
            if (handle == IntPtr.Zero) return Fail("Windows не разрешила изменить процесс.");

            try
            {
                var priority = GetPriorityClass(handle);
                if (priority == 0) return Fail("Не удалось прочитать приоритет процесса.");

                var ioPriority = QueryIoPriority(handle);
                if (!SetPriorityClass(handle, IdlePriorityClass))
                    return Fail("Не удалось снизить приоритет CPU.");

                if (!SetIoPriority(handle, IoPriorityVeryLow))
                {
                    SetPriorityClass(handle, priority);
                    return Fail("Не удалось снизить приоритет ввода-вывода.");
                }

                _records[pid] = new SleepRecord
                {
                    Pid = pid,
                    Name = process.ProcessName,
                    StartTicks = startTicks,
                    PriorityClass = priority,
                    IoPriority = ioPriority,
                    Suspended = false,
                };
                SaveState();
                return new SleepActionResult(true, "Приоритет CPU и ввода-вывода снижен.");
            }
            catch (Exception ex)
            {
                return Fail("Ошибка: " + ex.Message);
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }

    public SleepActionResult Suspend(int pid)
    {
        if (!IsAvailable) return Fail("Управление сном уже открыто в другом экземпляре GigaCleaner.");

        lock (_gate)
        {
            if (_disposed) return Fail("Служба сна уже остановлена.");

            using var process = TryGetProcess(pid, out var processError);
            if (process is null) return Fail(processError);

            if (!ProcessGuard.TryGetEligible(process, out _, out var reason)) return Fail(reason);

            if (_records.TryGetValue(pid, out var existing))
            {
                if (existing.Suspended) return new SleepActionResult(true, "Процесс уже заморожен.");
                if (!IsSameProcess(process, existing.StartTicks))
                    return Fail("PID уже принадлежит другому процессу.");

                var suspendHandle = OpenProcess(SuspendAccess, false, pid);
                if (suspendHandle == IntPtr.Zero) return Fail("Windows не разрешила заморозить процесс.");
                try
                {
                    if (NtSuspendProcess(suspendHandle) != 0) return Fail("Не удалось заморозить процесс.");
                    existing.Suspended = true;
                    SaveState();
                    return new SleepActionResult(true, "Процесс заморожен. Его можно разбудить в любой момент.");
                }
                finally
                {
                    CloseHandle(suspendHandle);
                }
            }

            var startTicks = GetStartTicks(process);
            if (startTicks == 0) return Fail("Не удалось определить время запуска процесса.");

            var handle = OpenProcess(SuspendAccess, false, pid);
            if (handle == IntPtr.Zero) return Fail("Windows не разрешила заморозить процесс.");

            try
            {
                var priority = GetPriorityClass(handle);
                if (priority == 0) return Fail("Не удалось прочитать приоритет процесса.");

                var ioPriority = QueryIoPriority(handle);
                if (!SetPriorityClass(handle, IdlePriorityClass))
                    return Fail("Не удалось снизить приоритет CPU.");

                if (!SetIoPriority(handle, IoPriorityVeryLow))
                {
                    SetPriorityClass(handle, priority);
                    return Fail("Не удалось снизить приоритет ввода-вывода.");
                }

                if (NtSuspendProcess(handle) != 0)
                {
                    SetPriorityClass(handle, priority);
                    SetIoPriority(handle, ioPriority);
                    return Fail("Не удалось заморозить процесс.");
                }

                _records[pid] = new SleepRecord
                {
                    Pid = pid,
                    Name = process.ProcessName,
                    StartTicks = startTicks,
                    PriorityClass = priority,
                    IoPriority = ioPriority,
                    Suspended = true,
                };
                SaveState();
                return new SleepActionResult(true, "Процесс заморожен. Он проснётся при закрытии GigaCleaner.");
            }
            catch (Exception ex)
            {
                return Fail("Ошибка: " + ex.Message);
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }

    public SleepActionResult Resume(int pid)
    {
        lock (_gate)
        {
            if (_disposed) return Fail("Служба сна уже остановлена.");
            return ResumeUnsafe(pid);
        }
    }

    public void ResumeAll()
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var pid in _records.Keys.ToArray()) ResumeUnsafe(pid);
        }
    }

    private SleepActionResult ResumeUnsafe(int pid)
    {
        if (!_records.TryGetValue(pid, out var record))
            return new SleepActionResult(true, "Процесс не находится под управлением сна.");

        if (!IsSameProcess(pid, record.StartTicks))
        {
            _records.Remove(pid);
            SaveState();
            return new SleepActionResult(true, "Процесс уже завершён.");
        }

        var handle = OpenProcess(SuspendAccess, false, pid);
        if (handle == IntPtr.Zero)
            return Fail("Windows не разрешила разбудить процесс. Он останется в журнале для повторной попытки.");

        try
        {
            if (record.Suspended && NtResumeProcess(handle) != 0)
                return Fail("Не удалось возобновить процесс.");

            SetPriorityClass(handle, record.PriorityClass);
            SetIoPriority(handle, record.IoPriority);

            _records.Remove(pid);
            SaveState();
            return new SleepActionResult(true, "Процесс разбужен, приоритеты восстановлены.");
        }
        catch (Exception ex)
        {
            return Fail("Ошибка пробуждения: " + ex.Message);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private void RecoverOrphans()
    {
        try
        {
            if (!File.Exists(_stateFile)) return;

            var state = JsonSerializer.Deserialize<SleepState>(File.ReadAllText(_stateFile));
            if (state?.Items is not { Count: > 0 })
            {
                DeleteState();
                return;
            }

            if (state.OwnerPid > 0 && IsSameProcess(state.OwnerPid, state.OwnerStartTicks))
            {
                // Записи принадлежат живому экземпляру GigaCleaner — не трогаем их.
                return;
            }

            var remaining = new List<SleepRecord>();
            foreach (var record in state.Items)
            {
                var sameProcess = IsSameProcess(record.Pid, record.StartTicks);
                if (record.Suspended)
                {
                    if (TryResumeOrphan(record))
                    {
                        RecoveredCount++;
                        continue;
                    }

                    if (sameProcess) remaining.Add(record);
                    continue;
                }

                if (sameProcess) remaining.Add(record);
            }

            if (remaining.Count == 0)
            {
                DeleteState();
            }
            else
            {
                foreach (var record in remaining) _records[record.Pid] = record;
                SaveState();
            }

            if (RecoveredCount > 0)
                AppLog.Write($"Sleep recovery: resumed {RecoveredCount} process(es)");
        }
        catch (Exception ex)
        {
            AppLog.Write("Sleep recovery failed: " + ex);
        }
    }

    private static bool TryResumeOrphan(SleepRecord record)
    {
        if (!IsSameProcess(record.Pid, record.StartTicks)) return true;

        var handle = OpenProcess(SuspendAccess, false, record.Pid);
        if (handle == IntPtr.Zero) return false;

        try
        {
            if (record.Suspended && NtResumeProcess(handle) != 0) return false;
            SetPriorityClass(handle, record.PriorityClass);
            SetIoPriority(handle, record.IoPriority);
            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private void SaveState()
    {
        try
        {
            if (_records.Count == 0)
            {
                DeleteState();
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            var state = new SleepState
            {
                OwnerPid = _closing ? 0 : _ownerPid,
                OwnerStartTicks = _closing ? 0 : _ownerStartTicks,
                Items = _records.Values.ToList(),
            };
            var temp = _stateFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temp, _stateFile, true);
        }
        catch (Exception ex)
        {
            AppLog.Write("Sleep state save failed: " + ex);
        }
    }

    private void DeleteState()
    {
        try { if (File.Exists(_stateFile)) File.Delete(_stateFile); }
        catch (Exception) { }
    }

    private static Process? TryGetProcess(int pid, out string error)
    {
        try
        {
            error = string.Empty;
            return Process.GetProcessById(pid);
        }
        catch (Exception)
        {
            error = "Процесс уже завершён.";
            return null;
        }
    }

    private static bool IsSameProcess(int pid, long startTicks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return IsSameProcess(process, startTicks);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsSameProcess(Process process, long startTicks)
    {
        if (startTicks == 0) return true;
        var actual = GetStartTicks(process);
        return actual != 0 && actual == startTicks;
    }

    private static long GetStartTicks(Process process)
    {
        try { return process.StartTime.ToUniversalTime().Ticks; }
        catch (Exception) { return 0; }
    }

    private static int QueryIoPriority(IntPtr handle)
    {
        var status = NtQueryInformationProcess(handle, ProcessIoPriority, out var value, sizeof(int), out _);
        return status == 0 ? value : IoPriorityNormal;
    }

    private static bool SetIoPriority(IntPtr handle, int priority)
    {
        var value = priority;
        return NtSetInformationProcess(handle, ProcessIoPriority, ref value, sizeof(int)) == 0;
    }

    private static SleepActionResult Fail(string message) => new(false, message);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;

            _closing = true;
            foreach (var pid in _records.Keys.ToArray())
            {
                if (_records[pid].Suspended) ResumeUnsafe(pid);
            }

            _disposed = true;
            SaveState();
        }

        _lock?.Dispose();
    }

    private sealed class SleepState
    {
        public int OwnerPid { get; set; }
        public long OwnerStartTicks { get; set; }
        public List<SleepRecord> Items { get; set; } = [];
    }

    private sealed class SleepRecord
    {
        public int Pid { get; set; }
        public string Name { get; set; } = string.Empty;
        public long StartTicks { get; set; }
        public uint PriorityClass { get; set; }
        public int IoPriority { get; set; }
        public bool Suspended { get; set; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetPriorityClass(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr process);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr process);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr process, int informationClass, out int information, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(
        IntPtr process, int informationClass, ref int information, int length);
}
