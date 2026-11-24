using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GigaCleaner.Controls;

/// <summary>
/// Плавная прокрутка колесом мыши в 60 fps.
/// Использование: ScrollBehavior.EnableSmooth="True" на ScrollViewer.
/// Анимация идёт через CompositionTarget.Rendering — по одному вызову на каждый
/// кадр рендера (синхронно с vsync), а не через DispatcherTimer (который даёт 30 fps).
/// </summary>
public static class ScrollBehavior
{
    public static readonly DependencyProperty EnableSmoothProperty =
        DependencyProperty.RegisterAttached(
            "EnableSmooth", typeof(bool), typeof(ScrollBehavior),
            new PropertyMetadata(false, OnEnableSmoothChanged));

    public static bool GetEnableSmooth(DependencyObject obj) => (bool)obj.GetValue(EnableSmoothProperty);
    public static void SetEnableSmooth(DependencyObject obj, bool value) => obj.SetValue(EnableSmoothProperty, value);

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "SmoothState", typeof(SmoothState), typeof(ScrollBehavior),
            new PropertyMetadata(null));

    private static void OnEnableSmoothChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer) return;

        if ((bool)e.NewValue)
        {
            if (viewer.GetValue(StateProperty) is not SmoothState state)
            {
                state = new SmoothState(viewer);
                viewer.SetValue(StateProperty, state);
            }
            viewer.PreviewMouseWheel += state.OnWheel;
        }
        else
        {
            if (viewer.GetValue(StateProperty) is SmoothState state)
            {
                viewer.PreviewMouseWheel -= state.OnWheel;
                state.Stop();
                viewer.SetValue(StateProperty, null);
            }
        }
    }

    private sealed class SmoothState
    {
        private const double DurationSeconds = 0.3;
        private const double MillisPerNotch = 132;

        private readonly ScrollViewer _viewer;
        private double _target;
        private double _start;
        private long _startedTicks;
        private bool _running;

        public SmoothState(ScrollViewer viewer) => _viewer = viewer;

        public void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (_viewer.ScrollableHeight <= 0) return;

            e.Handled = true;

            // Абсолютная цель: копим дельту, чтобы много быстрых «кликов» колеса
            // давали один большой плавный рывок, а не прерывистые подскоки.
            // Колесо вверх (Delta > 0) прокручивает вверх — offset уменьшается.
            _target = Math.Clamp(_target - e.Delta / 120.0 * MillisPerNotch,
                0, _viewer.ScrollableHeight);

            Start();
        }

        private void Start()
        {
            // Каждое новое колесо перезапускает easing от текущей позиции к новой цели.
            _start = _viewer.VerticalOffset;
            _startedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_running) return;

            _running = true;
            CompositionTarget.Rendering += OnFrame;
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            CompositionTarget.Rendering -= OnFrame;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            var elapsed = (double)(System.Diagnostics.Stopwatch.GetTimestamp() - _startedTicks) /
                          System.Diagnostics.Stopwatch.Frequency;
            var t = Math.Min(1.0, elapsed / DurationSeconds);

            // Cubic ease-out: быстрый старт, мягкое завершение.
            var eased = 1 - Math.Pow(1 - t, 3);
            _viewer.ScrollToVerticalOffset(_start + (_target - _start) * eased);

            if (t >= 1.0) Stop();
        }
    }
}