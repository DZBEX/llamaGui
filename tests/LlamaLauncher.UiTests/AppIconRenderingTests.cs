using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LlamaLauncher.App.Controls;
using LlamaLauncher.Domain.Workflows;
using Xunit;

namespace LlamaLauncher.UiTests;

/// <summary>
/// 界面图标（Assets/Icons 下的 PNG + AppIcon 蒙版着色）回归测试：
/// 1) 代码引用的图标资源全部已嵌入，且为 64×64 PNG（缺失会让界面对应位置变成空白）；
/// 2) AppIcon 渲染按 Fill 着色、未覆盖区域保持透明；
/// 3) 表格单元格样式带省略号与悬停提示（长文本不得再被硬截断）。
/// </summary>
[Collection("数据目录串行")]
public sealed class AppIconRenderingTests
{
    private static readonly string[] NavIcons =
    {
        "nav-dashboard", "nav-instances", "nav-models", "nav-profiles", "nav-chat", "nav-benchmark",
        "nav-lab", "nav-hardware", "nav-updates", "nav-logs", "nav-history", "nav-settings",
    };

    private static readonly string[] LabTabIcons =
    {
        "tab-debug", "tab-designer", "tab-testcases", "tab-batch", "nav-history",
    };

    private static readonly string[] CommonIcons =
    {
        "state-play", "state-pause", "state-stop", "state-check", "state-error", "state-timeout",
        "state-skip", "state-cancel", "state-pending", "warn", "ui-close", "ui-plus", "ui-minus",
        "ui-undo", "ui-redo", "star",
    };

    [Fact]
    public void 全部界面图标资源已嵌入且为标准尺寸()
    {
        RunOnSta(() =>
        {
            EnsurePackScheme();

            var names = NavIcons
                .Concat(LabTabIcons)
                .Concat(CommonIcons)
                .Concat(NodeTypeCatalog.All.Select(d => AppIcons.NodeIconName(d.TypeId)))
                .Distinct()
                .ToList();

            Assert.DoesNotContain(names, string.IsNullOrWhiteSpace);
            foreach (var name in names)
            {
                var stream = System.Windows.Application.GetResourceStream(AppIcons.Uri(name!))?.Stream;
                Assert.NotNull(stream);
                using (stream)
                {
                    var frame = BitmapFrame.Create(stream!, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    Assert.Equal(64, frame.PixelWidth);
                    Assert.Equal(64, frame.PixelHeight);
                    Assert.NotNull(AppIcons.Get(name));
                }
            }
        });
    }

    [Fact]
    public void 运行状态图标映射覆盖全部枚举取值()
    {
        foreach (var state in Enum.GetValues<NodeRunState>())
        {
            Assert.NotNull(AppIcons.Get(AppIcons.RunStateIconName(state)));
        }

        foreach (var state in Enum.GetValues<WorkflowRunState>())
        {
            Assert.NotNull(AppIcons.Get(AppIcons.RunStateIconName(state)));
        }

        Assert.NotNull(AppIcons.Get(AppIcons.RunStateIconName((NodeRunState?)null)));
    }

    [Fact]
    public void 未知图标名返回空而不抛异常()
        => Assert.Null(AppIcons.Get("no-such-icon"));

    [Fact]
    public void 图标控件按填充色着色且背景透明()
    {
        RunOnSta(() =>
        {
            var icon = new AppIcon
            {
                Icon = "state-check",
                Fill = Brushes.Red,
                Width = 16,
                Height = 16,
            };
            icon.Measure(new Size(16, 16));
            icon.Arrange(new Rect(0, 0, 16, 16));
            icon.UpdateLayout();

            var bitmap = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(icon);
            var stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);

            // 圆环外的角落必须透明（图标为蒙版着色，不能画出底片）
            Assert.Equal(0, pixels[3]);

            // 圆环笔画应为红色（按 Fill 着色，而不是原图的白色）
            var foundRed = false;
            for (var i = 0; i + 3 < pixels.Length; i += 4)
            {
                var b = pixels[i];
                var g = pixels[i + 1];
                var r = pixels[i + 2];
                var a = pixels[i + 3];
                if (a > 200 && r > 180 && g < 90 && b < 90)
                {
                    foundRed = true;
                    break;
                }
            }

            Assert.True(foundRed, "AppIcon 未按 Fill 着色渲染。");
        });
    }

    [Fact]
    public void 表格单元格样式提供省略号与悬停提示()
    {
        var assemblyName = typeof(AppIcon).Assembly.GetName().Name;
        var controls = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{assemblyName};component/Themes/Controls.xaml"),
        };

        var style = Assert.IsType<Style>(controls["App.CellText"]);
        var setters = style.Setters.OfType<Setter>().ToList();
        Assert.Contains(
            setters,
            s => s.Property == TextBlock.TextTrimmingProperty && Equals(s.Value, TextTrimming.CharacterEllipsis));
        Assert.Contains(setters, s => s.Property == ToolTipService.ToolTipProperty);

        // 隐式 TextBlock 样式同样收尾省略号：防止任意窄容器里的硬截断
        var implicitText = Assert.IsType<Style>(controls[typeof(TextBlock)]);
        Assert.Contains(
            implicitText.Setters.OfType<Setter>(),
            s => s.Property == TextBlock.TextTrimmingProperty && Equals(s.Value, TextTrimming.CharacterEllipsis));
    }

    private static void EnsurePackScheme()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
            typeof(System.IO.Packaging.PackUriHelper).TypeHandle);
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
        thread.Join();
        if (error is not null)
        {
            throw error;
        }
    }

}
