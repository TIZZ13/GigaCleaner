using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using GigaCleaner.Services;
using WinForms = System.Windows.Forms;

namespace GigaCleaner.Views;

public partial class HotkeySettingsWindow : Window
{
    private readonly Ellipse[] _dots;
    private int _modifiers;
    private int _key;
    private bool _capturing;

    public HotkeySettingsWindow()
    {
        InitializeComponent();
        _dots = new[] { Dot1, Dot2, Dot3 };

        var (modifiers, key) = SettingsStore.Hotkey;
        _modifiers = modifiers;
        _key = key;
        CaptureLabel.Text = FormatCombination(_modifiers, _key);

        PreviewKeyDown += OnCaptureKey;
    }

    private void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        if (_capturing) return;

        _capturing = true;
        HintText.Text = "Зажмите новую комбинацию…";

        // Текущий бинд плавно исчезает, вместо него волна из трёх точек.
        Fade(CaptureLabel, 1, 0, 160, () =>
        {
            StartWave();
            Fade(WaitingPanel, 0, 1, 220);
        });
    }

    private void OnCaptureKey(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;

        var modifiers = ModifierKeysToFlags();
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Только модификаторы сами по себе — ждём основную клавишу.
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            // Отмена: возвращаем текущий бинд.
            CancelCapture();
            e.Handled = true;
            return;
        }

        var code = KeyInterop.VirtualKeyFromKey(key);
        if (code == 0 || modifiers == 0) return;

        _modifiers = modifiers;
        _key = code;
        _capturing = false;

        // Волна исчезает, плавно появляется новый бинд.
        Fade(WaitingPanel, 1, 0, 160, () =>
        {
            StopWave();
            CaptureLabel.Text = FormatCombination(_modifiers, _key);
            Fade(CaptureLabel, 0, 1, 220);
        });

        HintText.Text = $"Новое значение: {FormatCombination(_modifiers, _key)}";
        e.Handled = true;
    }

    private void CancelCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        HintText.Text = "Захват отменён. Текущая комбинация сохранена.";

        Fade(WaitingPanel, 1, 0, 160, () =>
        {
            StopWave();
            CaptureLabel.Text = FormatCombination(_modifiers, _key);
            Fade(CaptureLabel, 0, 1, 220);
        });
    }

    private void StartWave()
    {
        for (var i = 0; i < _dots.Length; i++)
        {
            var wave = new DoubleAnimation(0.25, 1, System.TimeSpan.FromMilliseconds(360))
            {
                BeginTime = System.TimeSpan.FromMilliseconds(i * 120),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            _dots[i].BeginAnimation(OpacityProperty, wave);
        }
    }

    private void StopWave()
    {
        foreach (var dot in _dots) dot.BeginAnimation(OpacityProperty, null);
    }

    private int ModifierKeysToFlags()
    {
        var flags = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) flags |= HotkeyService.ModControl;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) flags |= HotkeyService.ModShift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) flags |= HotkeyService.ModAlt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) flags |= HotkeyService.ModWin;
        return flags;
    }

    private static void Fade(FrameworkElement element, double from, double to, int ms, Action? done = null)
    {
        var animation = new DoubleAnimation(from, to, System.TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        if (done is not null) animation.Completed += (_, _) => done();
        element.BeginAnimation(OpacityProperty, animation);
    }

    public static string FormatCombination(int modifiers, int key)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((modifiers & HotkeyService.ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & HotkeyService.ModShift) != 0) parts.Add("Shift");
        if ((modifiers & HotkeyService.ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & HotkeyService.ModWin) != 0) parts.Add("Win");
        if (key != 0) parts.Add(((WinForms.Keys)key).ToString());
        return parts.Count == 0 ? "не задана" : string.Join(" + ", parts);
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _capturing = false;
        _modifiers = HotkeyService.ModShift;
        _key = (int)WinForms.Keys.C;
        StopWave();
        CaptureLabel.Text = FormatCombination(_modifiers, _key);
        WaitingPanel.Opacity = 0;
        HintText.Text = "Сброшено на Shift + C.";
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_modifiers == 0 || _key == 0)
        {
            HintText.Text = "Комбинация не задана. Укажите модификатор и клавишу.";
            return;
        }

        SettingsStore.SaveHotkey(_modifiers, _key);
        if (Owner is MainWindow main) HotkeyService.Rebind(main);
        DialogResult = true;
    }
}