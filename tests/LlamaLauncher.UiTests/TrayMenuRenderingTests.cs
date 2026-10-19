using System.IO;
using LlamaLauncher.App.Services;
using Xunit;
using DrawingColor = System.Drawing.Color;
using DrawingFont = System.Drawing.Font;
using DrawingPoint = System.Drawing.Point;
using Forms = System.Windows.Forms;

namespace LlamaLauncher.UiTests;

/// <summary>托盘右键菜单：主题色解析与 WinForms 渲染回归。</summary>
[Collection("数据目录串行")]
public sealed class TrayMenuRenderingTests
{
    [Fact]
    public void 托盘菜单主题色跟随应用主题()
    {
        RunOnSta(() =>
        {
            EnsureApplicationResources();

            var palette = TrayService.ResolveMenuPalette();

            Assert.Equal(ReadThemeColor("App.Surface"), palette.Surface);
            Assert.Equal(ReadThemeColor("App.Border"), palette.Border);
            Assert.Equal(ReadThemeColor("App.Text.Primary"), palette.TextPrimary);
            Assert.Equal(ReadThemeColor("App.Text.Secondary"), palette.TextSecondary);
            Assert.Equal(ReadThemeColor("App.Accent"), palette.Accent);
            Assert.Equal(DrawingColor.White, palette.HoverText);
        });
    }

    [Fact]
    public void 托盘右键菜单可渲染为主题样式()
    {
        RunOnSta(() =>
        {
            EnsureApplicationResources();

            var palette = TrayService.ResolveMenuPalette();
            using var font = new DrawingFont("Microsoft YaHei UI", 9.5F);
            using var menu = new Forms.ContextMenuStrip
            {
                AutoSize = true,
                DropShadowEnabled = true,
                Font = font,
                Padding = new Forms.Padding(4),
                Renderer = new TrayMenuRenderer(palette),
                ShowCheckMargin = false,
                ShowImageMargin = false,
            };

            menu.Items.Add(CreateItem("显示主窗口"));
            menu.Items.Add(new Forms.ToolStripSeparator());
            var instanceItem = CreateItem("本地实例（运行中）");
            instanceItem.DropDownItems.Add(CreateItem("停止"));
            menu.Items.Add(instanceItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(CreateItem("退出 LlamaLauncher"));

            menu.Show(new DrawingPoint(40, 40));
            Forms.Application.DoEvents();
            menu.PerformLayout();
            menu.Items[2].Select();
            Forms.Application.DoEvents();

            var width = Math.Max(menu.Width, 1);
            var height = Math.Max(menu.Height, 1);
            using var bitmap = new System.Drawing.Bitmap(width, height);
            menu.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, width, height));

            var directory = Path.Combine(FindRepoRoot(), "artifacts", "menu-preview");
            Directory.CreateDirectory(directory);
            bitmap.Save(Path.Combine(directory, "tray-menu-dark.png"), System.Drawing.Imaging.ImageFormat.Png);
            menu.Close();

            Forms.ToolStripMenuItem CreateItem(string text) => new(text)
            {
                AutoSize = true,
                Font = font,
                ForeColor = palette.TextPrimary,
                Padding = new Forms.Padding(8, 4, 8, 4),
            };
        });
    }

    private static DrawingColor ReadThemeColor(string key)
    {
        var brush = Assert.IsType<System.Windows.Media.SolidColorBrush>(
            System.Windows.Application.Current!.FindResource(key));
        var color = brush.Color;
        return DrawingColor.FromArgb(color.A, color.R, color.G, color.B);
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
            throw new TimeoutException("托盘菜单 STA 测试超过 1 分钟未完成。");
        }

        if (error is not null)
        {
            throw error;
        }
    }
}
