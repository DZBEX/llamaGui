using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LlamaLauncher.App.Controls;
using Xunit;

namespace LlamaLauncher.UiTests;

/// <summary>
/// 项目右键菜单的主题样式与交互回归：
/// 1) 菜单面板、菜单项、分隔线使用统一主题资源，圆角与高亮可正常渲染；
/// 2) 低频操作的「⋯」按钮左键和右键都能打开菜单。
/// </summary>
[Collection("数据目录串行")]
public sealed class ContextMenuRenderingTests
{
    [Fact]
    public void 右键菜单使用统一主题样式并可渲染()
    {
        RunOnSta(() =>
        {
            EnsureApplicationResources();

            var menu = CreateMenu();
            var menuStyle = Assert.IsType<Style>(System.Windows.Application.Current!.FindResource(typeof(ContextMenu)));
            Assert.NotNull(System.Windows.Application.Current.FindResource(typeof(MenuItem)));
            menu.Style = menuStyle;
            menu.ApplyTemplate();
            menu.Measure(new Size(280, double.PositiveInfinity));
            var menuSize = menu.DesiredSize;
            menu.Arrange(new Rect(0, 0, menuSize.Width, menuSize.Height));
            menu.UpdateLayout();

            Assert.IsType<Style>(menu.Style);
            Assert.Equal(220, menu.MinWidth);

            var item = Assert.IsType<MenuItem>(menu.Items[0]);
            Assert.Equal(32, item.MinHeight);
            Assert.True(item.ActualWidth > 0, $"菜单项未完成布局（宽度 {item.ActualWidth:0.#}）。");

            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(menuSize.Width),
                (int)Math.Ceiling(menuSize.Height),
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(menu);

            var directory = Path.Combine(FindRepoRoot(), "artifacts", "menu-preview");
            Directory.CreateDirectory(directory);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, "context-menu-dark.png"));
            encoder.Save(stream);
        });
    }

    [Fact]
    public void 低频操作按钮左键可以打开右键菜单()
    {
        RunOnSta(() =>
        {
            EnsureApplicationResources();

            var menu = CreateMenu();
            var button = new Button
            {
                Content = "⋯",
                ContextMenu = menu,
            };
            ContextMenuBehavior.SetOpenOnLeftClick(button, true);

            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.True(menu.IsOpen);
            Assert.Same(button, menu.PlacementTarget);
            Assert.Equal(PlacementMode.Bottom, menu.Placement);

            menu.IsOpen = false;
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(menu.IsOpen);
            menu.IsOpen = false;
        });
    }

    private static ContextMenu CreateMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "另存版本快照" });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "导入工作流…" });
        menu.Items.Add(new MenuItem { Header = "导出工作流…" });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "删除工作流" });
        return menu;
    }

    private static void EnsureApplicationResources()
    {
        if (System.Windows.Application.Current is null)
        {
            var app = new App.App();
            app.InitializeComponent();
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LlamaLauncher.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(1)))
        {
            throw new TimeoutException("右键菜单 STA 测试超过 1 分钟未完成。");
        }

        if (error is not null)
        {
            throw error;
        }
    }
}
