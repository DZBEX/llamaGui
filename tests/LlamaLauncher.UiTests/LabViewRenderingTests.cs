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
/// 用真实主题资源与依赖容器解析实验室（含 6 个子页），在离屏窗口中进行布局与渲染，
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

                    // 与应用截图一致的宽屏尺寸，验证单命令栏和侧栏在 1536×767 下无溢出。
                    lab.CurrentTab = lab.Tabs.First(t => t.Page is WorkflowDesignerViewModel);
                    view.Width = 1536;
                    view.Height = 767;
                    PumpLayout(view);
                    if (FindDesignerView(view) is { } wideDesignerView)
                    {
                        wideDesignerView.InitializePreview();
                        PumpLayout(view);
                    }

                    RenderAndSave(view, 1536, 767, "lab-07-designer-wide.png");
                    Mark("rendered-wide");

                    // 窄窗口：命令栏应整组换行，三组操作都不能被裁切或相互覆盖。
                    view.Width = 1080;
                    view.Height = 720;
                    PumpLayout(view);
                    if (FindDesignerView(view) is { } compactDesignerView)
                    {
                        compactDesignerView.InitializePreview();
                        PumpLayout(view);
                        RenderAndSave(view, 1080, 720, "lab-08-designer-compact.png");
                        AssertCommandGroupsInsideBar(compactDesignerView);
                    }
                    Mark("rendered-compact");

                    // 运行中：暂停/继续/单步/停止均保留文字，状态与模式仍在同一行可见。
                    if (lab.Tabs.First(t => t.Page is WorkflowDesignerViewModel).Page is WorkflowDesignerViewModel runningVm)
                    {
                        runningVm.IsRunActive = true;
                        runningVm.RunStateText = "运行中";
                        PumpLayout(view);
                        RenderAndSave(view, 1080, 720, "lab-09-designer-running.png");
                        if (FindDesignerView(view) is { } runningDesignerView)
                        {
                            AssertCommandGroupsInsideBar(runningDesignerView);
                        }
                        runningVm.IsRunActive = false;
                        runningVm.RunStateText = "未运行";
                    }
                    Mark("rendered-running");

                    // 游戏测试在窄窗口下应保留完整棋盘，顶部命令自动换行，决策详情仍可滚动查看。
                    lab.CurrentTab = lab.Tabs.First(t => t.Page is GameLabViewModel);
                    view.Width = 1080;
                    view.Height = 720;
                    PumpLayout(view);
                    RenderAndSave(view, 1080, 720, "lab-10-game-compact.png");
                    Mark("rendered-game-compact");

                    if (lab.Tabs.First(t => t.Page is GameLabViewModel).Page is GameLabViewModel gameVm)
                    {
                        foreach (var game in gameVm.Games.Where(g => g.Key is "gomoku" or "tetris"))
                        {
                            gameVm.SelectedGame = game;
                            PumpLayout(view);
                            RenderAndSave(view, 1080, 720, $"lab-11-game-{game.Key}.png");
                        }
                    }
                    Mark("rendered-all-games");

                    Mark("done");
                    Mark("probe-star=" + ProbeStarColumnWidth());

                    // 回归：带分支标签连线的工作流画布渲染。
                    // 历史缺陷：分支连线中点通过 GetFlattenedPathGeometry().Figures[0] 计算，
                    // 退化路径会得到空 Figures 并在渲染回调中抛异常（未处理 → 进程崩溃）。
                    RenderAndSaveCanvasWithBranchEdges();
                    Mark("canvas-branches-ok");
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
        "游戏测试" => "game-test",
        "运行历史" => "history",
        _ => "tab",
    };

    /// <summary>渲染包含分支标签连线的画布（回归历史渲染崩溃缺陷）。</summary>
    private static void RenderAndSaveCanvasWithBranchEdges()
    {
        var workflow = new LlamaLauncher.Domain.Workflows.WorkflowDefinition
        {
            Id = "canvas-branch-test",
            Name = "分支渲染回归",
        };
        workflow.Nodes.Add(LlamaLauncher.Domain.Workflows.NodeTypeCatalog.CreateNode(
            LlamaLauncher.Domain.Workflows.NodeTypeCatalog.Start, "n1", 60, 120));
        var decision = LlamaLauncher.Domain.Workflows.NodeTypeCatalog.CreateNode(
            LlamaLauncher.Domain.Workflows.NodeTypeCatalog.Decision, "n2", 320, 120);
        workflow.Nodes.Add(decision);
        var rules = LlamaLauncher.Domain.Workflows.NodeTypeCatalog.CreateNode(
            LlamaLauncher.Domain.Workflows.NodeTypeCatalog.Rules, "n3", 600, 120);
        rules.Config = LlamaLauncher.Domain.Workflows.WorkflowJson.ToConfigObject(
            new LlamaLauncher.Domain.Workflows.RulesNodeConfig
            {
                OnFailure = LlamaLauncher.Domain.Workflows.RuleFailureAction.Branch,
            });
        workflow.Nodes.Add(rules);
        workflow.Nodes.Add(LlamaLauncher.Domain.Workflows.NodeTypeCatalog.CreateNode(
            LlamaLauncher.Domain.Workflows.NodeTypeCatalog.Transform, "n4", 860, 60));
        workflow.Nodes.Add(LlamaLauncher.Domain.Workflows.NodeTypeCatalog.CreateNode(
            LlamaLauncher.Domain.Workflows.NodeTypeCatalog.Transform, "n5", 860, 220));
        workflow.Edges.Add(new LlamaLauncher.Domain.Workflows.WorkflowEdge
        {
            Id = "e1", SourceNodeId = "n1", TargetNodeId = "n2",
        });
        workflow.Edges.Add(new LlamaLauncher.Domain.Workflows.WorkflowEdge
        {
            Id = "e2", SourceNodeId = "n2", TargetNodeId = "n3",
        });
        workflow.Edges.Add(new LlamaLauncher.Domain.Workflows.WorkflowEdge
        {
            Id = "e3", SourceNodeId = "n3", TargetNodeId = "n4", BranchId = "pass",
        });
        workflow.Edges.Add(new LlamaLauncher.Domain.Workflows.WorkflowEdge
        {
            Id = "e4", SourceNodeId = "n3", TargetNodeId = "n5", BranchId = "fail",
        });

        var canvas = new App.Controls.WorkflowCanvasControl { Width = 1000, Height = 520 };
        canvas.SetDefinition(workflow);
        canvas.Measure(new Size(1000, 520));
        canvas.Arrange(new Rect(0, 0, 1000, 520));
        canvas.UpdateLayout();

        var bitmap = new RenderTargetBitmap(1000, 520, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);

        var directory = Path.Combine(FindRepoRoot(), "artifacts", "lab-preview");
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, "lab-06-canvas-branches.png"));
        encoder.Save(stream);
    }

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

    /// <summary>断言命令栏的三组操作均位于容器内，且没有被裁切成不可用的窄条。</summary>
    private static void AssertCommandGroupsInsideBar(App.Views.WorkflowDesignerView designer)
    {
        var commandBar = designer.FindName("CommandBar") as FrameworkElement
            ?? throw new InvalidOperationException("未找到命令栏 CommandBar。");
        var barBounds = commandBar.TransformToAncestor(designer)
            .TransformBounds(new Rect(new Point(0, 0), commandBar.RenderSize));

        foreach (var name in new[] { "WorkflowCommandGroup", "RunCommandGroup", "CanvasCommandGroup" })
        {
            var group = designer.FindName(name) as FrameworkElement
                ?? throw new InvalidOperationException($"未找到命令组 {name}。");
            var bounds = group.TransformToAncestor(designer)
                .TransformBounds(new Rect(new Point(0, 0), group.RenderSize));
            Assert.True(group.ActualWidth > 100, $"{name} 宽度过窄（{group.ActualWidth:0.#}），操作可能被裁切。");
            Assert.True(bounds.Left >= barBounds.Left - 0.5
                        && bounds.Top >= barBounds.Top - 0.5
                        && bounds.Right <= barBounds.Right + 0.5
                        && bounds.Bottom <= barBounds.Bottom + 0.5,
                $"{name} 超出命令栏范围：组 {bounds}，命令栏 {barBounds}。");
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
