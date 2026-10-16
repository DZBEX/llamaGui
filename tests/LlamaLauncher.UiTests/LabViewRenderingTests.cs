using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LlamaLauncher.App;
using LlamaLauncher.App.ViewModels.Lab;
using LlamaLauncher.Infrastructure;
using LlamaLauncher.Infrastructure.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LlamaLauncher.UiTests;

/// <summary>
/// 决策模型实验室 UI 冒烟与渲染测试：
/// 用真实主题资源与依赖容器解析实验室（含 5 个子页），在离屏窗口中进行布局与渲染，
/// 每位子页输出 PNG 到 artifacts/lab-preview/ 供人工检查，并断言关键区域有内容（非空白）。
/// </summary>
[Collection("数据目录串行")]
public sealed class LabViewRenderingTests
{
    [Fact]
    public void 实验室各子页可渲染并输出截图()
    {
        RunOnSta(() =>
        {
            Mark("start");
            EnsureApplicationResources();
            Mark("app-resources");

            var tempRoot = Path.Combine(Path.GetTempPath(), "LlamaLauncherLabTests", Guid.NewGuid().ToString("N"));
            var originalRoot = Environment.GetEnvironmentVariable("LLAMALAUNCHER_DATA_ROOT");
            try
            {
                Environment.SetEnvironmentVariable("LLAMALAUNCHER_DATA_ROOT", tempRoot);
                var services = new ServiceCollection();
                services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
                InfrastructureServiceCollectionExtensions.AddLlamaLauncherCore(services, new ConfigurationBuilder().Build());
                services.AddAppShellServices(() => Task.CompletedTask);

                var provider = services.BuildServiceProvider();
                try
                {
                    provider.GetRequiredService<SqliteDatabase>().InitializeAsync().GetAwaiter().GetResult();
                    Mark("db-ready");

                    var lab = provider.GetRequiredService<LabViewModel>();
                    Mark("lab-vm");
                    var view = new App.Views.LabView { DataContext = lab, Width = 1280, Height = 820 };
                    Mark("lab-view");

                    PumpLayout(view);
                    Mark("pumped");

                    RenderAndSave(view, 1280, 820, "lab-00-model-debug.png");
                    Mark("rendered-0");
                    for (var i = 0; i < lab.Tabs.Count; i++)
                    {
                        lab.CurrentTab = lab.Tabs[i];
                        PumpLayout(view);

                        // 设计器页需要初始化画布（无窗口消息循环时 Loaded 不会触发）。
                        if (lab.Tabs[i].Page is WorkflowDesignerViewModel && FindDesignerView(view) is { } designerView)
                        {
                            designerView.InitializePreview();
                            PumpLayout(view);
                        }

                        RenderAndSave(view, 1280, 820, $"lab-{i + 1:00}-{TabSlug(lab.Tabs[i].Title)}.png");
                        Mark("rendered-" + (i + 1));
                    }

                    Mark("done");
                    Mark("probe-star=" + ProbeStarColumnWidth());
                }
                finally
                {
                    provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("LLAMALAUNCHER_DATA_ROOT", originalRoot);
                try
                {
                    if (Directory.Exists(tempRoot))
                    {
                        Directory.Delete(tempRoot, recursive: true);
                    }
                }
                catch (Exception)
                {
                }
            }
        });
    }

    private static string TabSlug(string title) => title switch
    {
        "模型调试" => "model-debug",
        "工作流设计与调试" => "designer",
        "测试用例" => "test-cases",
        "批量测试" => "batch-test",
        "运行历史" => "history",
        _ => "tab",
    };

    /// <summary>
    /// 探针：记录星号列在离屏布局下的实际宽度。
    /// 说明：无窗口消息循环时 WPF 不会完成星号列的最终分配（恒为最小宽度），
    /// 因此实验室视图中的关键列使用显式像素宽度以保证任意环境下布局稳定。
    /// </summary>
    private static string ProbeStarColumnWidth()
    {
        var grid = new System.Windows.Controls.DataGrid
        {
            Width = 300,
            Height = 80,
            ItemsSource = new[] { "x" },
        };
        grid.Columns.Add(new System.Windows.Controls.DataGridTextColumn
        {
            Header = "星号列",
            Binding = new System.Windows.Data.Binding("."),
            Width = new System.Windows.Controls.DataGridLength(1.5, System.Windows.Controls.DataGridLengthUnitType.Star),
        });
        grid.Columns.Add(new System.Windows.Controls.DataGridTextColumn
        {
            Header = "固定列",
            Binding = new System.Windows.Data.Binding("."),
            Width = new System.Windows.Controls.DataGridLength(80),
        });
        grid.Measure(new Size(300, 80));
        grid.Arrange(new Rect(0, 0, 300, 80));
        grid.UpdateLayout();
        return $"star={grid.Columns[0].ActualWidth:0} fixed={grid.Columns[1].ActualWidth:0}";
    }

    private static void Mark(string step)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "lab-test-progress.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + " " + step + Environment.NewLine);
        }
        catch
        {
        }
    }

    private static void EnsureApplicationResources()
    {
        if (System.Windows.Application.Current is null)
        {
            var app = new App.App();
            app.InitializeComponent();
        }
    }

    private static void PumpLayout(FrameworkElement element)
    {
        element.Measure(new Size(element.Width, element.Height));
        element.Arrange(new Rect(0, 0, element.Width, element.Height));
        element.UpdateLayout();
    }

    /// <summary>在可视树中查找设计器视图（用于离屏初始化画布）。</summary>
    private static App.Views.WorkflowDesignerView? FindDesignerView(DependencyObject root)
    {
        if (root is App.Views.WorkflowDesignerView designer)
        {
            return designer;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindDesignerView(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static void RenderAndSave(FrameworkElement element, int width, int height, string fileName)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        // 断言页面不是纯色空白：采样统计不同颜色数。
        var distinct = new HashSet<int>();
        for (var y = 4; y < bitmap.PixelHeight - 4; y += 12)
        {
            for (var x = 4; x < bitmap.PixelWidth - 4; x += 12)
            {
                var offset = y * stride + x * 4;
                distinct.Add((pixels[offset] << 16) | (pixels[offset + 1] << 8) | pixels[offset + 2]);
            }
        }

        Assert.True(distinct.Count > 20, $"{fileName} 渲染结果近似空白（颜色数 {distinct.Count}），可能存在布局或资源问题。");

        var directory = Path.Combine(FindRepoRoot(), "artifacts", "lab-preview");
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, fileName));
        encoder.Save(stream);
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
        if (!thread.Join(TimeSpan.FromMinutes(2)))
        {
            throw new TimeoutException("STA 测试线程超过 2 分钟未完成（可能存在调度器死锁）。");
        }

        if (error is not null)
        {
            throw error;
        }
    }
}
