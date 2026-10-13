using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Instances;
using LlamaLauncher.Application.Services;
using LlamaLauncher.App.Services;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.App.ViewModels;

/// <summary>日志来源选项。</summary>
public sealed record LogSourceOption(string DisplayName, Guid? InstanceId);

/// <summary>日志行。</summary>
public sealed record LogLine(string TimeText, string StreamText, string Text);

/// <summary>日志与诊断页。</summary>
public sealed partial class LogsViewModel : ViewModelBase, IPageViewModel, IDisposable
{
    private const int MaxUiLines = 3000;
    private const int AppLogTailLines = 2000;

    private readonly IInstanceLogStore _logStore;
    private readonly IInstanceManager _instances;
    private readonly IAppPaths _paths;
    private readonly IDiagnosticsService _diagnostics;
    private readonly ISettingsService _settings;
    private readonly ILogger<LogsViewModel> _logger;
    private readonly DispatcherTimer _timer;
    private string _lastAppLogSignature = string.Empty;
    private int _lastInstanceLineCount;

    [ObservableProperty]
    private LogSourceOption? _selectedSource;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private LogLine? _selectedLine;

    public LogsViewModel(
        IInstanceLogStore logStore,
        IInstanceManager instances,
        IAppPaths paths,
        IDiagnosticsService diagnostics,
        ISettingsService settings,
        IDialogService dialogs,
        INotificationService notifications,
        ILogger<LogsViewModel> logger)
    {
        _logStore = logStore;
        _instances = instances;
        _paths = paths;
        _diagnostics = diagnostics;
        _settings = settings;
        _logger = logger;
        Dialogs = dialogs;
        Notifications = notifications;

        _instances.InstanceChanged += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(RefreshSources);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _timer.Tick += (_, _) => RefreshLinesIfNeeded();
        _timer.Start();
        RefreshSources();
    }

    public ObservableCollection<LogSourceOption> Sources { get; } = new();

    public ObservableCollection<LogLine> Lines { get; } = new();

    public Task OnActivatedAsync()
    {
        RefreshSources();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void ClearView()
    {
        Lines.Clear();
        Notifications?.Notify("已清空界面显示（日志文件不受影响）。");
    }

    /// <summary>跳转到指定实例的日志来源。</summary>
    public void SelectInstance(Guid instanceId)
    {
        RefreshSources();
        var target = Sources.FirstOrDefault(s => s.InstanceId == instanceId);
        if (target is not null)
        {
            SelectedSource = target;
        }
    }

    [RelayCommand]
    private Task CopySelectedAsync()
    {
        if (SelectedLine is not { } line)
        {
            return Task.CompletedTask;
        }

        try
        {
            System.Windows.Clipboard.SetText($"{line.TimeText} {line.StreamText} {line.Text}");
            Notifications?.Notify("已复制选中行。");
        }
        catch (Exception ex)
        {
            Notifications?.Notify("复制失败：" + ex.Message);
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task CopyAllAsync()
    {
        if (Lines.Count == 0)
        {
            return Task.CompletedTask;
        }

        try
        {
            var text = string.Join(Environment.NewLine, Lines.Select(l => $"{l.TimeText} {l.StreamText} {l.Text}"));
            System.Windows.Clipboard.SetText(text);
            Notifications?.Notify($"已复制 {Lines.Count} 行。");
        }
        catch (Exception ex)
        {
            Notifications?.Notify("复制失败：" + ex.Message);
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", _paths.LogsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notifications?.Notify("打开日志目录失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        var path = Dialogs!.PickSaveFile(
            "导出诊断包",
            "Zip 压缩包|*.zip",
            $"llamalauncher-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (path is null)
        {
            return;
        }

        var (confirmed, includePaths) = await Dialogs.ConfirmWithCheckboxAsync(
            new ConfirmDialogOptions
            {
                Title = "导出诊断包",
                Message = "诊断包包含应用日志、实例日志、设置、安装与实例信息。\n" +
                          "API Key、令牌等敏感值会被脱敏；默认还会把用户目录替换为占位符。",
                PrimaryButtonText = "导出",
                SecondaryButtonText = "取消",
            },
            "保留完整本地路径（不替换为占位符）",
            defaultChecked: false);
        if (!confirmed)
        {
            return;
        }

        await RunBusyAsync("正在导出诊断包…", async () =>
        {
            var result = await _diagnostics.ExportBundleAsync(path, includePaths);
            await Dialogs!.ShowInfoAsync(
                result.Success ? "导出完成" : "导出失败",
                result.Success ? "诊断包已保存到：\n" + result.FilePath : result.Message);
        });
    }

    [RelayCommand]
    private void CleanupOldLogs()
    {
        try
        {
            var settings = _settings.LoadAsync().GetAwaiter().GetResult();
            var removed = _logStore.CleanupOldFiles(TimeSpan.FromDays(settings.LogRetentionDays));
            Notifications?.Notify($"已清理 {removed} 个超过 {settings.LogRetentionDays} 天的日志文件。");
        }
        catch (Exception ex)
        {
            Notifications?.Notify("清理旧日志失败：" + ex.Message);
        }
    }

    private void RefreshSources()
    {
        var selectedKey = SelectedSource?.InstanceId;
        Sources.Clear();
        Sources.Add(new LogSourceOption("应用日志（启动器）", null));
        foreach (var instance in _instances.GetInstances())
        {
            Sources.Add(new LogSourceOption($"实例：{instance.Name}", instance.Id));
        }

        SelectedSource = Sources.FirstOrDefault(s => s.InstanceId == selectedKey)
            ?? Sources.FirstOrDefault();
        _lastAppLogSignature = string.Empty;
        _lastInstanceLineCount = -1;
        RefreshLinesIfNeeded(force: true);
    }

    partial void OnSelectedSourceChanged(LogSourceOption? value)
    {
        Lines.Clear();
        _lastAppLogSignature = string.Empty;
        _lastInstanceLineCount = -1;
        RefreshLinesIfNeeded(force: true);
    }

    private void RefreshLinesIfNeeded(bool force = false)
    {
        if (IsPaused && !force)
        {
            return;
        }

        if (SelectedSource is null)
        {
            return;
        }

        if (SelectedSource.InstanceId is { } instanceId)
        {
            var recent = _logStore.GetRecent(instanceId, AppLogTailLines);
            if (!force && recent.Count == _lastInstanceLineCount)
            {
                return;
            }

            _lastInstanceLineCount = recent.Count;
            RebuildLines(recent.Select(l => new LogLine(
                l.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff"),
                l.Stream switch
                {
                    LogStreamKind.StandardOutput => "[stdout]",
                    LogStreamKind.StandardError => "[stderr]",
                    _ => "[app]",
                },
                l.Text)));
        }
        else
        {
            var path = FindLatestAppLog();
            if (path is null)
            {
                if (force)
                {
                    RebuildLines(Array.Empty<LogLine>());
                }

                return;
            }

            string content;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                content = reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "读取应用日志失败");
                return;
            }

            if (!force && content == _lastAppLogSignature)
            {
                return;
            }

            _lastAppLogSignature = content;
            var lines = content
                .Split('\n')
                .Where(l => l.Length > 0)
                .TakeLast(AppLogTailLines)
                .Select(l => new LogLine(string.Empty, string.Empty, l.TrimEnd('\r')));
            RebuildLines(lines);
        }
    }

    private void RebuildLines(IEnumerable<LogLine> source)
    {
        var filter = SearchText?.Trim();
        var filtered = string.IsNullOrEmpty(filter)
            ? source
            : source.Where(l => l.Text.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var list = filtered.TakeLast(MaxUiLines).ToList();
        Lines.Clear();
        foreach (var line in list)
        {
            Lines.Add(line);
        }
    }

    partial void OnSearchTextChanged(string value) => RefreshLinesIfNeeded(force: true);

    private string? FindLatestAppLog()
    {
        try
        {
            if (!Directory.Exists(_paths.LogsDirectory))
            {
                return null;
            }

            return Directory
                .EnumerateFiles(_paths.LogsDirectory, "app-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose() => _timer.Stop();
}
