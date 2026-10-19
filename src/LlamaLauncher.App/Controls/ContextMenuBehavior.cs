using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace LlamaLauncher.App.Controls;

/// <summary>
/// 让带 <see cref="FrameworkElement.ContextMenu"/> 的按钮同时支持左键与右键打开菜单。
/// 右键仍由 WPF 原生处理（按鼠标位置弹出）；左键打开时菜单贴在按钮下方。
/// </summary>
public static class ContextMenuBehavior
{
    public static readonly DependencyProperty OpenOnLeftClickProperty = DependencyProperty.RegisterAttached(
        "OpenOnLeftClick",
        typeof(bool),
        typeof(ContextMenuBehavior),
        new PropertyMetadata(false, OnOpenOnLeftClickChanged));

    public static bool GetOpenOnLeftClick(DependencyObject element)
        => (bool)element.GetValue(OpenOnLeftClickProperty);

    public static void SetOpenOnLeftClick(DependencyObject element, bool value)
        => element.SetValue(OpenOnLeftClickProperty, value);

    private static void OnOpenOnLeftClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button)
        {
            return;
        }

        button.Click -= OnButtonClick;
        if (e.NewValue is true)
        {
            button.Click += OnButtonClick;
        }
    }

    private static void OnButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor || anchor.ContextMenu is not { } menu)
        {
            return;
        }

        if (menu.IsOpen)
        {
            menu.IsOpen = false;
            return;
        }

        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 2;
        menu.IsOpen = true;
        e.Handled = true;
    }
}
