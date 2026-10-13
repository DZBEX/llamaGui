using System.Globalization;
using System.Windows;
using LlamaLauncher.App.Converters;
using LlamaLauncher.App.ViewModels;
using Xunit;

namespace LlamaLauncher.UiTests;

public sealed class ConverterTests
{
    private static string RunOnStaThread(Func<string> action)
    {
        string result = string.Empty;
        var thread = new Thread(() => result = action());
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    [Fact]
    public void InverseBoolToVisibility_Works()
    {
        var converter = new InverseBoolToVisibilityConverter();
        Assert.Equal(Visibility.Collapsed, converter.Convert(true, typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Visible, converter.Convert(false, typeof(Visibility), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void NullOrEmptyToVisibility_Works()
    {
        var converter = new NullOrEmptyToVisibilityConverter();
        Assert.Equal(Visibility.Collapsed, converter.Convert(null, typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, converter.Convert(string.Empty, typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Visible, converter.Convert("x", typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Visible, converter.Convert(null, typeof(Visibility), "invert", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void NullToBoolConverter_Works()
    {
        var converter = new NullToBoolConverter();
        Assert.Equal(true, converter.Convert(null, typeof(bool), null, CultureInfo.InvariantCulture));
        Assert.Equal(false, converter.Convert(0.5, typeof(bool), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void SeverityToBrush_MapsWithoutApplication()
    {
        // 无 Application 时应返回回退色而不是抛异常
        var converter = new SeverityToBrushConverter();
        var brush = converter.Convert(
            new ValidationIssueRow("错误", "x"),
            typeof(System.Windows.Media.Brush),
            null,
            CultureInfo.InvariantCulture);
        Assert.IsAssignableFrom<System.Windows.Media.Brush>(brush);
    }
}
