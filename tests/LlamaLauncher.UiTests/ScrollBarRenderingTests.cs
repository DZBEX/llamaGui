using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace LlamaLauncher.UiTests;

/// <summary>
/// 自定义滚动条渲染回归测试：
/// 用真实主题资源渲染 ScrollViewer，做像素级断言（细胶囊滑块、透明轨道），
/// 同时把渲染结果保存到 artifacts/scrollbar-preview/ 供人工查看。
/// 若滚动条退化为原生样式（宽滑块/灰色轨道），这些断言会失败。
/// </summary>
public sealed class ScrollBarRenderingTests
{
    [Theory]
    [InlineData("Theme.Dark.xaml", 88, 81, 77, 33, 29, 27)]
    [InlineData("Theme.Light.xaml", 207, 201, 198, 248, 246, 245)]
    public void ScrollViewer_UsesCapsuleThumbAndTransparentTrack(
        string themeFile,
        byte thumbB,
        byte thumbG,
        byte thumbR,
        byte hostB,
        byte hostG,
        byte hostR)
        => RunOnSta(() =>
        {
            var (theme, controls) = LoadThemeDictionaries(themeFile);

            // ---- 垂直滚动条 ----
            var verticalHost = BuildHost(theme, controls, vertical: true);
            const int width = 220;
            const int height = 170;
            var pixels = RenderToBgra(verticalHost, width, height, out var stride);

            // 滑块位于轨道右侧的一列窄区间（宽 12 的滚动条、缩进 3 → 可视滑块约 6px 宽）
            var thumbPixel = PixelAt(pixels, stride, 214, 25);
            Assert.True(
                Near(thumbPixel, (thumbB, thumbG, thumbR), 16),
                $"滑块颜色异常：B={thumbPixel.B:X2} G={thumbPixel.G:X2} R={thumbPixel.R:X2}（期望接近主题滚动条色）。");

            // 滑块左侧留白应为透明（透出宿主背景）；原生样式在这里会画出宽滑块/轨道。
            var gutterPixel = PixelAt(pixels, stride, 210, 25);
            Assert.True(
                Near(gutterPixel, (hostB, hostG, hostR), 10),
                $"滑块应为细胶囊（左侧留白透出背景），实际像素 B={gutterPixel.B:X2} G={gutterPixel.G:X2} R={gutterPixel.R:X2}。");

            // 轨道空白区必须透明（透出宿主背景），无灰色底
            var trackPixel = PixelAt(pixels, stride, 214, 150);
            Assert.True(
                Near(trackPixel, (hostB, hostG, hostR), 10),
                $"轨道应为透明，实际像素 B={trackPixel.B:X2} G={trackPixel.G:X2} R={trackPixel.R:X2}。");

            SavePng(verticalHost, width, height, $"scrollbar-{(themeFile.Contains("Light") ? "light" : "dark")}-vertical.png");

            // ---- 水平滚动条 ----
            var horizontalHost = BuildHost(theme, controls, vertical: false);
            var horizontalPixels = RenderToBgra(horizontalHost, width, height, out var horizontalStride);

            var hThumbPixel = PixelAt(horizontalPixels, horizontalStride, 40, 164);
            Assert.True(
                Near(hThumbPixel, (thumbB, thumbG, thumbR), 16),
                $"水平滑块颜色异常：B={hThumbPixel.B:X2} G={hThumbPixel.G:X2} R={hThumbPixel.R:X2}。");

            var hTrackPixel = PixelAt(horizontalPixels, horizontalStride, 180, 164);
            Assert.True(
                Near(hTrackPixel, (hostB, hostG, hostR), 10),
                $"水平轨道应为透明，实际像素 B={hTrackPixel.B:X2} G={hTrackPixel.G:X2} R={hTrackPixel.R:X2}。");

            SavePng(horizontalHost, width, height, $"scrollbar-{(themeFile.Contains("Light") ? "light" : "dark")}-horizontal.png");
            return true;
        });

    private static FrameworkElement BuildHost(ResourceDictionary theme, ResourceDictionary controls, bool vertical)
    {
        var host = new Border
        {
            Width = 220,
            Height = 170,
            Background = (Brush)theme["App.Window.Background"],
        };
        host.Resources.MergedDictionaries.Add(theme);
        host.Resources.MergedDictionaries.Add(controls);
        host.Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Visible : ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Visible,
            Content = new Border
            {
                Width = vertical ? double.NaN : 520,
                Height = vertical ? 520 : double.NaN,
                Background = (Brush)theme["App.Surface"],
            },
        };
        return host;
    }

    private static T RunOnSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
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

        return result!;
    }

    private static (ResourceDictionary Theme, ResourceDictionary Controls) LoadThemeDictionaries(string themeFile)
    {
        var theme = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/LlamaLauncher;component/Themes/{themeFile}"),
        };
        var controls = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/LlamaLauncher;component/Themes/Controls.xaml"),
        };
        return (theme, controls);
    }

    private static byte[] RenderToBgra(FrameworkElement element, int width, int height, out int stride)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void SavePng(FrameworkElement element, int width, int height, string fileName)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.Combine(FindRepoRoot(), "artifacts", "scrollbar-preview");
        Directory.CreateDirectory(directory);
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

    private static (byte B, byte G, byte R) PixelAt(byte[] pixels, int stride, int x, int y)
    {
        var offset = y * stride + x * 4;
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }

    private static bool Near((byte B, byte G, byte R) actual, (byte B, byte G, byte R) expected, int tolerance)
        => Math.Abs(actual.B - expected.B) <= tolerance
           && Math.Abs(actual.G - expected.G) <= tolerance
           && Math.Abs(actual.R - expected.R) <= tolerance;
}
