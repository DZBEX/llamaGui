using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace LlamaLauncher.UiTests;

/// <summary>
/// ComboBox 深色主题渲染回归测试：
/// 用真实的主题资源字典渲染控件，并做像素级断言（背景色正确、文字可见、选中项高亮可见），
/// 同时把渲染结果保存到 artifacts/combo-preview/ 供人工查看。
/// </summary>
public sealed class ComboBoxRenderingTests
{
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

    private static (ResourceDictionary Theme, ResourceDictionary Controls) LoadThemeDictionaries()
    {
        var theme = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/LlamaLauncher;component/Themes/Theme.Dark.xaml"),
        };
        var controls = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/LlamaLauncher;component/Themes/Controls.xaml"),
        };
        return (theme, controls);
    }

    private static byte[] RenderToBgra(FrameworkElement element, int width, int height, out int stride)
    {
        element.Width = width;
        element.Height = height;
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
        element.Width = width;
        element.Height = height;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.Combine(FindRepoRoot(), "artifacts", "combo-preview");
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

    private static ((byte B, byte G, byte R) Color, int Count) MostCommonColor(byte[] pixels, int stride, int width, int height)
    {
        var counts = new Dictionary<(byte, byte, byte), int>();
        for (var y = 2; y < height - 2; y += 2)
        {
            for (var x = 2; x < width - 2; x += 2)
            {
                var color = PixelAt(pixels, stride, x, y);
                counts[color] = counts.TryGetValue(color, out var count) ? count + 1 : 1;
            }
        }

        var top = counts.OrderByDescending(kv => kv.Value).First();
        return (top.Key, top.Value);
    }

    private static int CountPixels(byte[] pixels, int stride, int width, int height, Func<(byte B, byte G, byte R), bool> predicate)
    {
        var count = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (predicate(PixelAt(pixels, stride, x, y)))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static bool Near((byte B, byte G, byte R) actual, (byte B, byte G, byte R) expected, int tolerance = 10)
        => Math.Abs(actual.B - expected.B) <= tolerance
           && Math.Abs(actual.G - expected.G) <= tolerance
           && Math.Abs(actual.R - expected.R) <= tolerance;

    [Fact]
    public void ClosedComboBox_UsesThemeColorsAndShowsText()
        => RunOnSta(() =>
        {
            var (theme, controls) = LoadThemeDictionaries();
            var combo = new ComboBox
            {
                Style = (Style)controls[typeof(ComboBox)],
                ItemsSource = new[] { "稳定通道（Stable）", "预发布通道（Prerelease）", "第三项" },
                SelectedIndex = 0,
            };
            combo.Resources.MergedDictionaries.Add(theme);

            const int width = 260;
            const int height = 34;
            var pixels = RenderToBgra(combo, width, height, out var stride);

            // 主要背景色应为深色主题的 Surface.Alt（#2B2F33）而不是系统浅色
            var (topColor, _) = MostCommonColor(pixels, stride, width, height);
            Assert.True(
                Near(topColor, (0x33, 0x2F, 0x2B)),
                $"ComboBox 背景色异常：B={topColor.B:X2} G={topColor.G:X2} R={topColor.R:X2}（期望接近 #2B2F33）");

            // 内容区必须存在明显的浅色像素（文字/箭头），否则说明文字与背景混为一体
            var lightPixels = CountPixels(pixels, stride, 200, height, c => 0.114 * c.B + 0.587 * c.G + 0.299 * c.R > 140);
            Assert.True(lightPixels > 30, $"未检测到足够的浅色文字像素（{lightPixels}），文字可能不可见。");

            SavePng(combo, width, height, "closed-combo.png");
            return true;
        });

    [Fact]
    public void DropDownItems_UseDarkPopupAndVisibleHighlight()
        => RunOnSta(() =>
        {
            var (theme, controls) = LoadThemeDictionaries();

            // 弹出层无法用 RenderTargetBitmap 直接捕获（独立窗口），
            // 这里按模板中的实际结构（Popup 内容 = Surface 背景 + ComboBoxItem）重建同一视觉树做像素校验。
            var normalItem = new ComboBoxItem
            {
                Style = (Style)controls[typeof(ComboBoxItem)],
                Content = "稳定通道（Stable）",
            };
            // 悬停状态（IsHighlighted）由输入系统驱动、无法在测试中直接设置，
            // 这里用与模板触发器中相同的悬停底色重建同样的视觉，用于验证颜色可读性。
            var hoverRow = new Border
            {
                Background = (Brush)controls["App.Surface.Alt"],
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(3, 1, 3, 1),
                Child = new ComboBoxItem
                {
                    Style = (Style)controls[typeof(ComboBoxItem)],
                    Content = "悬停样式项（Surface.Alt 底）",
                },
            };
            var selectedItem = new ComboBoxItem
            {
                Style = (Style)controls[typeof(ComboBoxItem)],
                Content = "选中项（应显示为强调色底白字）",
                IsSelected = true,
            };

            var panel = new StackPanel();
            panel.Children.Add(normalItem);
            panel.Children.Add(hoverRow);
            panel.Children.Add(selectedItem);
            var popupLook = new Border
            {
                Background = (Brush)theme["App.Surface"],
                BorderBrush = (Brush)theme["App.Border"],
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4),
                Child = panel,
            };
            popupLook.Resources.MergedDictionaries.Add(theme);
            popupLook.Resources.MergedDictionaries.Add(controls);

            const int width = 300;
            const int height = 130;
            var pixels = RenderToBgra(popupLook, width, height, out var stride);

            // 弹层底色必须是深色 Surface（#232629）
            var (topColor, _) = MostCommonColor(pixels, stride, width, height);
            Assert.True(
                Near(topColor, (0x29, 0x26, 0x23)),
                $"弹层背景色异常：B={topColor.B:X2} G={topColor.G:X2} R={topColor.R:X2}（期望接近 #232629）");

            // 选中项应为强调色背景（#4C8DFF）
            var accentPixels = CountPixels(pixels, stride, width, height, c => Near(c, (0xFF, 0x8D, 0x4C), 24));
            Assert.True(accentPixels > 400, $"未检测到选中项强调色背景（{accentPixels} 像素）。");

            // 文字必须清晰可见（浅色像素）
            var textPixels = CountPixels(pixels, stride, width, height, c => 0.114 * c.B + 0.587 * c.G + 0.299 * c.R > 150);
            Assert.True(textPixels > 60, $"未检测到足够的浅色文字像素（{textPixels}）。");

            SavePng(popupLook, width, height, "dropdown-items.png");
            return true;
        });

    [Fact]
    public void LightTheme_ComboBoxAlsoRendersReadable()
        => RunOnSta(() =>
        {
            var theme = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/LlamaLauncher;component/Themes/Theme.Light.xaml"),
            };
            var controls = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/LlamaLauncher;component/Themes/Controls.xaml"),
            };
            var combo = new ComboBox
            {
                Style = (Style)controls[typeof(ComboBox)],
                ItemsSource = new[] { "浅色主题项一", "浅色主题项二" },
                SelectedIndex = 0,
            };
            combo.Resources.MergedDictionaries.Add(theme);

            const int width = 220;
            const int height = 34;
            var pixels = RenderToBgra(combo, width, height, out var stride);

            // 浅色主题：主背景接近白色，文字为深色像素
            var (topColor, _) = MostCommonColor(pixels, stride, width, height);
            Assert.True(
                Near(topColor, (0xF2, 0xEE, 0xEC), 12),
                $"浅色主题背景色异常：B={topColor.B:X2} G={topColor.G:X2} R={topColor.R:X2}");
            var darkPixels = CountPixels(pixels, stride, 180, height, c => 0.114 * c.B + 0.587 * c.G + 0.299 * c.R < 90);
            Assert.True(darkPixels > 30, $"浅色主题下未检测到深色文字像素（{darkPixels}）。");

            SavePng(combo, width, height, "closed-combo-light.png");
            return true;
        });
}
