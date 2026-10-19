using System.Windows;
using System.Windows.Media;

namespace LlamaLauncher.App.Controls;

/// <summary>
/// 单色图标控件：按名称加载 PNG 图标（<see cref="AppIcons"/>）当作不透明度蒙版，再用 <see cref="Fill"/> 着色。
/// 同一份白色图标资源因此可以跟随主题、悬停与选中态换色（浅色/深色主题共用）。
/// </summary>
public sealed class AppIcon : FrameworkElement
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon),
        typeof(string),
        typeof(AppIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill),
        typeof(Brush),
        typeof(AppIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public AppIcon()
    {
        Width = 16;
        Height = 16;
    }

    /// <summary>图标名称（Assets/Icons 下的 PNG 文件名，不含扩展名）。</summary>
    public string? Icon
    {
        get => (string?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>着色画刷；默认取主题主文本色。</summary>
    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (AppIcons.Get(Icon) is not { } source)
        {
            return;
        }

        var brush = Fill ?? TryFindResource("App.Text.Primary") as Brush;
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (brush is null || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        drawingContext.PushOpacityMask(new ImageBrush(source) { Stretch = Stretch.Uniform });
        drawingContext.DrawRectangle(brush, null, bounds);
        drawingContext.Pop();
    }
}
