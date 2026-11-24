using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;

namespace GigaCleaner.Services;

/// <summary>
/// Защита от повторного запуска GUI: глобальный Mutex + NamedPipe,
/// чтобы второй экземпляр развернул окно первого и завершился.
/// Фоновый запуск (--silent) не конкурирует за мьютекс и не мешает окну.
/// </summary>
public static class SingleInstance
{
    public const string MutexName = @"Global\GigaCleaner_SingleInstance_Mutex";
    public const string PipeName = @"GigaCleaner_SingleInstance_Pipe";

    /// <summary>Возвращает true, если этот процесс стал единственным GUI-владельцем.</summary>
    public static bool TryAcquireGui()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out var createdNew);
            return createdNew;
        }
        catch (UnauthorizedAccessException)
        {
            // Мьютекс уже создан другим экземпляром — это и есть «уже запущен».
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>Просит первый экземпляр развернуть окно и завершает второй процесс.</summary>
    public static void NotifyExistingAndExit()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client);
            writer.Write("show");
            writer.Flush();
        }
        catch (Exception ex)
        {
            AppLog.Write("SingleInstance notify failed: " + ex.Message);
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    /// <summary>Запускает в первом экземпляре приёмник команд показа окна.</summary>
    public static void StartPipeListener(Action showWindow)
    {
        var thread = new System.Threading.Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    _ = server.WaitForConnectionAsync().ConfigureAwait(false);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    var message = reader.ReadToEnd();
                    if (message.Length > 0)
                    {
                        Application.Current?.Dispatcher?.BeginInvoke(DispatcherPriority.Normal, showWindow);
                    }
                }
                catch (IOException)
                {
                    // Таймаут соединения — продолжаем слушать.
                }
                catch (Exception ex)
                {
                    AppLog.Write("SingleInstance pipe error: " + ex.Message);
                }
            }
        })
        { IsBackground = true };
        thread.Start();
        _pipeThread = thread;
    }

    public static void Release()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (Exception)
        {
        }
        _mutex?.Dispose();
        _mutex = null;
    }

    private static Mutex? _mutex;
    private static System.Threading.Thread? _pipeThread;
}