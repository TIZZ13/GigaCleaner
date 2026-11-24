using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace GigaCleaner.Controls;

/// <summary>
/// Стеклянная карточка Liquid Glass: полупрозрачная основа, двойная окантовка,
/// мягкая тень, живой блик за курсором и плавная «левитация» при наведении.
/// Шаблон задаётся в Themes/Styles.xaml (стиль Card.Glass), имя PART_Spotlight обязательно.
/// </summary>
public sealed class GlassCard : ContentControl
{
    public static readonly DependencyProperty SpotlightBrushProperty =
        DependencyProperty.Register(nameof(SpotlightBrush), typeof(Brush), typeof(GlassCard),
            new PropertyMetadata(null));

    private RadialGradientBrush? _spotlight;
    private double _spotlightRadius;

    static GlassCard()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(GlassCard),
            new FrameworkPropertyMetadata(typeof(GlassCard)));
    }

    public GlassCard()
    {
        MouseEnter += OnMouseEnter;
        MouseMove += OnMouseMove;
        MouseLeave += OnMouseLeave;
    }

    /// <summary>Радиальный блик: центр перемещается за курсором.</summary>
    public Brush? SpotlightBrush
    {
        get => (Brush?)GetValue(SpotlightBrushProperty);
        set => SetValue(SpotlightBrushProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _spotlight = GetTemplateChild("PART_Spotlight") as RadialGradientBrush;
        if (_spotlight is null && SpotlightBrush is RadialGradientBrush radial)
            _spotlight = radial.Clone();

        // Скруглённый клип: контент не вылезает за углы стекла (чинит «обрезанные углы»).
        if (GetTemplateChild("GlassSurface") is Border glass)
        {
            glass.SizeChanged += (_, _) =>
            {
                var r = Math.Min(glass.CornerRadius.TopLeft, Math.Max(1, Math.Min(glass.ActualWidth, glass.ActualHeight) / 2));
                glass.Clip = new RectangleGeometry(
                    new Rect(0, 0, Math.Max(0, glass.ActualWidth), Math.Max(0, glass.ActualHeight)), r, r);
            };
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_spotlight is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var position = e.GetPosition(this);
        var center = new Point(position.X / ActualWidth, position.Y / ActualHeight);
        _spotlight.Center = center;
        _spotlight.GradientOrigin = center;
        if (_spotlightRadius <= 0) _spotlightRadius = 0.9;
        _spotlight.RadiusX = _spotlightRadius;
        _spotlight.RadiusY = _spotlightRadius;
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        Animate("SpotlightOpacity", 0.0, 0.7);
        AnimateLift(-1.5, 1.005);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        Animate("SpotlightOpacity", 0.7, 0.0);
        AnimateLift(0, 1.0);
    }

    private void Animate(string property, double from, double to)
    {
        if (_spotlight is null) return;
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        _spotlight.BeginAnimation(System.Windows.Media.Brush.OpacityProperty, animation);
    }

    private void AnimateLift(double translateY, double scale)
    {
        var transform = RenderTransform as TransformGroup;
        if (transform is null) return;

        var translate = (TranslateTransform)transform.Children[0];
        var scaleTransform = (ScaleTransform)transform.Children[1];

        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(translateY, TimeSpan.FromMilliseconds(220))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(scale, TimeSpan.FromMilliseconds(220))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(scale, TimeSpan.FromMilliseconds(220))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}