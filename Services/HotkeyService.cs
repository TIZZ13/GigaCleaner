using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GigaCleaner.Services;

/// <summary>
/// Глобальная горячая клавиша. По умолчанию Shift+C (MOD_SHIFT + C).
/// Регистрируется через RegisterHotKey и работает, даже когда окно свёрнуто/в трее.
/// </summary>
public static class HotkeyService
{
    public const int ModAlt = 0x0001;
    public const int ModControl = 0x0002;
    public const int ModShift = 0x0004;
    public const int ModWin = 0x0008;

    private const int WmHotkey = 0x0312;
    private const int DefaultId = 0xC1E2;

    public static event Action? HotkeyPressed;

    private static HwndSource? _source;
    private static IntPtr _hwnd;
    private static int _registeredModifiers;
    private static int _registeredKey;

    /// <summary>Регистрирует хоткей из настроек. Вызывается после появления окна.</summary>
    public static void Register(Window window)
    {
        var (modifiers, key) = SettingsStore.Hotkey;
        if (modifiers == 0 || key == 0) return;

        try
        {
            var helper = new WindowInteropHelper(window);
            _hwnd = helper.Handle;
            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);

            if (RegisterHotKey(_hwnd, DefaultId, (uint)modifiers, (uint)key))
            {
                _registeredModifiers = modifiers;
                _registeredKey = key;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Hotkey register failed: " + ex.Message);
        }
    }

    /// <summary>Перерегистрирует хоткей после изменения в настройках.</summary>
    public static void Rebind(Window window)
    {
        Unregister();
        Register(window);
    }

    public static void Unregister()
    {
        try
        {
            if (_source is not null)
            {
                if (_hwnd != IntPtr.Zero && _registeredKey != 0)
                    UnregisterHotKey(_hwnd, DefaultId);
                _source.RemoveHook(WndProc);
                _source = null;
                _hwnd = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Hotkey unregister failed: " + ex.Message);
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == DefaultId)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}