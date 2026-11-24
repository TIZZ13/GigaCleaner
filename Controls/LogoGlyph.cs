using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace GigaCleaner.Controls;

/// <summary>
/// Логотип GigaCleaner: объёмная буква «G» из жидкого стекла (как в кнопках) с желе-физикой.
/// Форма — настоящий глиф «G» шрифта (всегда правильный).
/// Стекло: полупрозрачная заливка GlassFillBrush + светлые кромки + верхний блик.
/// Шейдерные искажения: лёгкая хроматическая кайма (две тени цветных по краям).
/// Физика: курсор тянет букву с инерцией, клик — затухающие колебания.
/// </summary>
public sealed class LogoGlyph : Grid
{
    private readonly Path _extrude = new();
    private readonly Path _body = new();
    private readonly Path _glow = new();
    private readonly Path _rim = new();
    private readonly Path _caustic = new();
    private readonly Grid _host = new();

    private double _posX, _posY;
    private double _velX, _velY;
    private double _scaleX = 1, _scaleY = 1;
    private double _angular;
    private bool _wobbling;
    private double _wobbleTime;
    private DateTime _lastTick = DateTime.UtcNow;

    public LogoGlyph()
    {
        IsHitTestVisible = true;
        Cursor = Cursors.Hand;
        Focusable = false;

        _host.Children.Add(_extrude);
        _host.Children.Add(_caustic);
        _host.Children.Add(_body);
        _host.Children.Add(_glow);
        _host.Children.Add(_rim);
        _host.RenderTransform = new TransformGroup();
        Children.Add(_host);

        MouseMove += OnGlyphMove;
        MouseEnter += (_, _) => { };
        MouseLeave += (_, _) => { _posX = _posY = 0; _velX = _velY = 0; };
        MouseLeftButtonDown += (_, _) => Punch();

        Loaded += (_, _) =>
        {
            CompositionTarget.Rendering += OnRenderTick;
            Rebuild();
        };
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRenderTick;
        SizeChanged += (_, _) => Rebuild();
    }

    private void Punch()
    {
        _wobbling = true;
        _wobbleTime = 0;
    }

    private void OnGlyphMove(object sender, MouseEventArgs e)
    {
        if (ActualWidth < 2 || ActualHeight < 2) return;
        var pos = e.GetPosition(this);
        var cx = ActualWidth / 2;
        var cy = ActualHeight / 2;
        var targetX = (pos.X - cx) / cx;
        var targetY = (pos.Y - cy) / cy;

        var dt = (DateTime.UtcNow - _lastTick).TotalSeconds;
        _lastTick = DateTime.UtcNow;
        if (dt <= 0 || dt > 0.05) dt = 0.016;

        const double stiffness = 26;
        const double damping = 6.0;
        var ax = (targetX - _posX) * stiffness - _velX * damping;
        var ay = (targetY - _posY) * stiffness - _velY * damping;
        _velX += ax * dt;
        _velY += ay * dt;
        _posX += _velX * dt;
        _posY += _velY * dt;

        var speed = Math.Min(1.1, Math.Sqrt(_velX * _velX + _velY * _velY) / 2.5);
        _scaleX = 1 + speed * 0.10;
        _scaleY = 1 - speed * 0.06;
        _angular = Math.Clamp((targetX - _posX) * 0.06, -0.12, 0.12);
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (_wobbling) Wobble();
        ApplyTransform();
    }

    private void Wobble()
    {
        _wobbleTime += 0.016;
        var t = _wobbleTime;
        var damp = Math.Exp(-3.0 * t);
        _scaleX = 1 + Math.Sin(t * 13) * 0.09 * damp;
        _scaleY = 1 - Math.Sin(t * 13 + 1.3) * 0.09 * damp;
        _angular = Math.Sin(t * 8) * 0.08 * damp;
        if (t > 1.5)
        {
            _wobbling = false;
            _scaleX = _scaleY = 1;
            _angular = 0;
        }
    }

    private void ApplyTransform()
    {
        if (_host.RenderTransform is not TransformGroup group) return;
        group.Children.Clear();
        group.Children.Add(new RotateTransform(_angular));
        group.Children.Add(new ScaleTransform(_scaleX, _scaleY));
        _host.RenderTransformOrigin = new Point(0.5, 0.5);
    }

    private void Rebuild()
    {
        var w = Math.Max(8, ActualWidth);
        var h = Math.Max(8, ActualHeight);

        var geo = GlyphGeometry(w, h + 2);   // чуть выше, чтобы массивнее
        if (geo is null) return;

        // 1. Тёмная грань-экструзия — объём (свет падает сверху).
        _extrude.Data = geo.Clone();
        _extrude.RenderTransform = new TranslateTransform(0, Math.Max(2, h * 0.12));
        _extrude.Fill = new SolidColorBrush(Color.FromArgb(150, 16, 22, 36));
        _extrude.Stroke = null;

        // 2. Лёгкая тень для глубины (стекло отбрасывает мягкую тень, но не светится).
        _caustic.Data = geo.Clone();
        _caustic.RenderTransform = new TranslateTransform(0, Math.Max(1, h * 0.05));
        _caustic.Fill = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
        _caustic.Opacity = 1;
        _caustic.Stroke = null;
        _caustic.Effect = new DropShadowEffect
        {
            BlurRadius = 4,
            ShadowDepth = 0,
            Color = Colors.Black,
            Opacity = 0.5,
        };

        // 3. Тело — стеклянная заливка: светлый верх-лево → прозрачное стекло → светлый низ.
        _body.Data = geo;
        _body.Fill = BuildGlassBrush(w, h);
        _body.Stroke = null;

        // 3a. Мягкое свечение по телу: буква читается как объёмное стекло, а не пустой контур.
        _glow.Data = geo;
        _glow.Fill = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
        _glow.Stroke = null;
        _glow.RenderTransform = new TranslateTransform(0, Math.Max(0.3, h * 0.012));
        _glow.Effect = new DropShadowEffect
        {
            BlurRadius = 3,
            ShadowDepth = 0,
            Color = Color.FromArgb(80, 255, 255, 255),
            Opacity = 0.7,
        };

        // 4. Тонкий светлый кант (внутренняя кромка стекла) — едва заметный, без свечения.
        _rim.Data = geo;
        _rim.Fill = null;
        _rim.Stroke = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
        _rim.StrokeThickness = Math.Max(0.7, w * 0.02);
        _rim.StrokeLineJoin = PenLineJoin.Round;
        _rim.Effect = null;

        _body.Effect = new DropShadowEffect
        {
            BlurRadius = 8,
            ShadowDepth = 3,
            Direction = 270,
            Color = Colors.Black,
            Opacity = 0.45,
        };
    }

    private static Brush? TryFindBrush(string key)
    {
        try
        {
            if (Application.Current?.FindResource(key) is Brush brush) return brush.Clone();
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>Берём глиф «G» из шрифта — форма всегда идеальная.</summary>
    private static Geometry? GlyphGeometry(double w, double h)
    {
        try
        {
            var font = new FontFamily("Segoe UI");
            var typeface = new Typeface(font, FontStyles.Normal, FontWeights.Black, FontStretches.Normal);
            var formatted = new FormattedText("G", CultureInfo.GetCultureInfo("en-us"),
                FlowDirection.LeftToRight, typeface, h * 0.92, Brushes.Black, 1.0);
            var geo = formatted.BuildGeometry(new Point(0, 0));
            var bounds = geo.Bounds;
            if (bounds.Width < 1 || bounds.Height < 1) return null;

            var offsetX = (w - bounds.Width) / 2 - bounds.X;
            var offsetY = (h - bounds.Height) / 2 - bounds.Y;
            geo.Transform = new TranslateTransform(offsetX, offsetY);
            return geo;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Brush BuildGlassBrush(double w, double h)
    {
        var radial = new RadialGradientBrush
        {
            Center = new Point(0.28, 0.18),
            GradientOrigin = new Point(0.28, 0.18),
            RadiusX = 0.9,
            RadiusY = 0.9,
            ColorInterpolationMode = ColorInterpolationMode.ScRgbLinearInterpolation,
        };
        // Прозрачное стекло: маленький белый блик сверху-слева, тело почти прозрачное,
        // едва светлая кромка. Никакого неона и насыщенного цвета.
        radial.GradientStops.Add(new GradientStop(Color.FromArgb(0xCC, 255, 255, 255), 0.00));
        radial.GradientStops.Add(new GradientStop(Color.FromArgb(0x2E, 255, 255, 255), 0.18));
        radial.GradientStops.Add(new GradientStop(Color.FromArgb(0x0A, 255, 255, 255), 0.5));
        radial.GradientStops.Add(new GradientStop(Color.FromArgb(0x1C, 255, 255, 255), 0.85));
        radial.GradientStops.Add(new GradientStop(Color.FromArgb(0x55, 255, 255, 255), 1.0));
        return radial;
    }
}