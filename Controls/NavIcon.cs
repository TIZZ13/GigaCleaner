using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;

namespace GigaCleaner.Controls;

/// <summary>Иконка FluentSystemIcons для пункта меню: <c>nav:NavIcon Symbol="Broom24"</c>.</summary>
public sealed class NavIcon : ContentControl
{
    public static readonly DependencyProperty SymbolProperty =
        DependencyProperty.Register(nameof(Symbol), typeof(Wpf.Ui.Controls.SymbolRegular), typeof(NavIcon),
            new PropertyMetadata(Wpf.Ui.Controls.SymbolRegular.QuestionCircle24, OnVisualChanged));

    public static readonly DependencyProperty SymbolForegroundProperty =
        DependencyProperty.Register(nameof(SymbolForeground), typeof(Brush), typeof(NavIcon),
            new PropertyMetadata(Brushes.Gray, OnVisualChanged));

    public Wpf.Ui.Controls.SymbolRegular Symbol
    {
        get => (Wpf.Ui.Controls.SymbolRegular)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public Brush SymbolForeground
    {
        get => (Brush)GetValue(SymbolForegroundProperty);
        set => SetValue(SymbolForegroundProperty, value);
    }

    // Родительский шаблон подставляет сюда Traverse<TemplateBinding> для цвета.
    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((NavIcon)d).Apply();

    private void Apply()
    {
        if (Content is Wpf.Ui.Controls.SymbolIcon icon)
        {
            icon.Symbol = Symbol;
            icon.Foreground = SymbolForeground;
        }
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        if (Content is null)
        {
            Content = new Wpf.Ui.Controls.SymbolIcon { FontSize = 16, Margin = new Thickness(0, 0, 10, 0) };
            Apply();
        }
    }
}
