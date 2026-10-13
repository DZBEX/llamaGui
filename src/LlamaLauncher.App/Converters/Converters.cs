using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using LlamaLauncher.App.ViewModels;
using LlamaLauncher.Domain;

namespace LlamaLauncher.App.Converters;

/// <summary>按页面 ViewModel 类型选择视图模板。</summary>
public sealed class PageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Dashboard { get; set; }

    public DataTemplate? Instances { get; set; }

    public DataTemplate? Models { get; set; }

    public DataTemplate? Profiles { get; set; }

    public DataTemplate? Chat { get; set; }

    public DataTemplate? Hardware { get; set; }

    public DataTemplate? Updates { get; set; }

    public DataTemplate? Logs { get; set; }

    public DataTemplate? History { get; set; }

    public DataTemplate? Settings { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) => item switch
    {
        DashboardViewModel => Dashboard,
        InstancesViewModel => Instances,
        ModelsViewModel => Models,
        ProfilesViewModel => Profiles,
        ChatViewModel => Chat,
        HardwareViewModel => Hardware,
        UpdatesViewModel => Updates,
        LogsViewModel => Logs,
        HistoryViewModel => History,
        SettingsViewModel => Settings,
        _ => null,
    };
}

/// <summary>true → Collapsed，false → Visible。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>null/空字符串 → Collapsed（parameter="invert" 时相反）。</summary>
public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEmpty = value is null || (value is string s && s.Length == 0);
        var invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
        if (invert)
        {
            isEmpty = !isEmpty;
        }

        return isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>InstanceState → 状态色。</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            InstanceState.Ready => "App.State.Ready",
            InstanceState.Starting or InstanceState.LoadingModel or InstanceState.Stopping => "App.State.Loading",
            InstanceState.Failed or InstanceState.ExitedUnexpectedly => "App.State.Error",
            _ => "App.State.Stopped",
        };
        return System.Windows.Application.Current?.TryFindResource(key) as Brush
            ?? new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>MessageStatus → 状态色（流式=琥珀、中断=灰、失败=红）。</summary>
public sealed class MessageStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            MessageStatus.Streaming => "App.State.Loading",
            MessageStatus.Interrupted => "App.State.Stopped",
            MessageStatus.Failed => "App.State.Error",
            _ => "App.State.Ready",
        };
        return System.Windows.Application.Current?.TryFindResource(key) as Brush
            ?? new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>校验问题行 → 严重级别色。</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ValidationIssueRow { IsBlocking: true } => "App.State.Error",
            ValidationIssueRow => "App.State.Loading",
            _ => "App.State.Stopped",
        };
        return System.Windows.Application.Current?.TryFindResource(key) as Brush
            ?? new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>布尔取反。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>null/false → true（用于“不确定进度 = 总量未知”）。</summary>
public sealed class NullToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null or false;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
