using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.App.Services;
using LlamaLauncher.Application.Benchmarks;
using LlamaLauncher.Application.Instances;
using LlamaLauncher.Domain;
using LlamaLauncher.Domain.Benchmarks;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.App.ViewModels;

/// <summary>基准测试目标实例选项。</summary>
public sealed class BenchmarkInstanceOption
{
    public BenchmarkInstanceOption(ManagedInstance instance)
    {
        InstanceId = instance.Id;
        DisplayName = instance.Name;
        Endpoint = instance.LocalBaseUrl;
        State = instance.State;
    }

    public Guid InstanceId { get; }

    public string DisplayName { get; }

    public string Endpoint { get; }

    public InstanceState State { get; }

    public bool IsReady => State == InstanceState.Ready;

    public string StateText => Formatting.InstanceStateText(State);

    public string DisplayText => $"{DisplayName}（{StateText}）· {Endpoint}";
}

/// <summary>档位汇总行（界面展示）。</summary>
public sealed class BenchmarkGroupRow
{
    public required string PromptText { get; init; }

    public required string ActualPromptText { get; init; }

    public required string PromptSpeedText { get; init; }

    public required string GenerateSpeedText { get; init; }

    public required string TtftText { get; init; }

    public required string TotalText { get; init; }

    public required string SampleText { get; init; }

    public static BenchmarkGroupRow From(BenchmarkGroupSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new BenchmarkGroupRow
        {
            PromptText = summary.TargetPromptTokens.ToString(CultureInfo.InvariantCulture),
            ActualPromptText = summary.AveragePromptTokens is { } average
                ? average.ToString("0", CultureInfo.InvariantCulture)
                : "—",
            PromptSpeedText = BenchmarkStatistics.FormatMetric(summary.PromptTokensPerSecond, 1),
            GenerateSpeedText = BenchmarkStatistics.FormatMetric(summary.GeneratedTokensPerSecond, 1),
            TtftText = BenchmarkStatistics.FormatMetric(summary.TimeToFirstTokenMs, 0),
            TotalText = BenchmarkStatistics.FormatMetric(summary.TotalMs, 0),
            SampleText = summary.FailureCount == 0
                ? $"{summary.SuccessCount} 成功"
                : $"{summary.SuccessCount} 成功 / {summary.FailureCount} 失败",
        };
    }
}

/// <summary>样本明细行（界面展示）。</summary>
public sealed class BenchmarkSampleRow
{
    public required string KindText { get; init; }

    public required string PromptText { get; init; }

    public required string ActualPromptText { get; init; }

    public required string PromptSpeedText { get; init; }

    public required string GeneratedText { get; init; }

    public required string GenerateSpeedText { get; init; }

    public required string TtftText { get; init; }

    public required string TotalText { get; init; }

    public string? ErrorText { get; init; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public static BenchmarkSampleRow From(BenchmarkSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return new BenchmarkSampleRow
        {
            KindText = sample.IsWarmup ? "预热" : $"第 {sample.Repetition} 次",
            PromptText = sample.TargetPromptTokens.ToString(CultureInfo.InvariantCulture),
            ActualPromptText = Format(sample.PromptTokens),
            PromptSpeedText = Format(sample.PromptTokensPerSecond),
            GeneratedText = Format(sample.GeneratedTokens),
            GenerateSpeedText = Format(sample.GeneratedTokensPerSecond),
            TtftText = Format(sample.TimeToFirstTokenMs),
            TotalText = Format(sample.TotalMs),
            ErrorText = sample.Error,
        };
    }

    private static string Format(double? value) => value is null
        ? "—"
        : value.Value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Format(int? value) => value is null
        ? "—"
        : value.Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 性能基准页：选择运行中的实例，通过 /completion 测量提示处理/生成速度与延迟，
/// 展示实时进度、档位统计与样本明细，并管理历史记录（查看/删除/复制报告/导出 JSON）。
/// </summary>
public sealed partial class BenchmarkViewModel : ViewModelBase, IPageViewModel, IDisposable
{
    private const int HistoryLimit = 100;

    private readonly BenchmarkRunnerService _runner;
    private readonly IBenchmarkRunStore _store;
    private readonly IInstanceManager _instances;
    private readonly IClipboardService _clipboard;
    private readonly ILogger<BenchmarkViewModel> _logger;
    private CancellationTokenSource? _cts;
    private BenchmarkRunRecord? _currentRecord;
    private BenchmarkRunRecord? _historyRecord;

    public BenchmarkViewModel(
        BenchmarkRunnerService runner,
        IBenchmarkRunStore store,
        IInstanceManager instances,
        IClipboardService clipboard,
        INotificationService notifications,
        IDialogService dialogs,
        ILogger<BenchmarkViewModel> logger)
    {
        _runner = runner;
        _store = store;
        _instances = instances;
        _clipboard = clipboard;
        _logger = logger;
        Notifications = notifications;
        Dialogs = dialogs;
        _instances.InstanceChanged += OnInstanceChanged;
    }

    public ObservableCollection<BenchmarkInstanceOption> Instances { get; } = new();

    public ObservableCollection<BenchmarkGroupRow> CurrentGroups { get; } = new();

    public ObservableCollection<BenchmarkSampleRow> CurrentSamples { get; } = new();

    public ObservableCollection<BenchmarkRunSummary> History { get; } = new();

    public ObservableCollection<BenchmarkGroupRow> HistoryGroups { get; } = new();

    public ObservableCollection<BenchmarkSampleRow> HistorySamples { get; } = new();

    [ObservableProperty]
    private BenchmarkInstanceOption? _selectedInstance;

    [ObservableProperty]
    private string _promptTokensText = "512, 2048";

    [ObservableProperty]
    private int _generateTokens = 128;

    [ObservableProperty]
    private int _repetitions = 3;

    [ObservableProperty]
    private int _warmupRuns = 1;

    [ObservableProperty]
    private int _cooldownSeconds;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "选择“运行中”的实例并配置参数后开始测试。";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _currentResultText = "尚未运行。";

    [ObservableProperty]
    private BenchmarkRunSummary? _selectedHistory;

    [ObservableProperty]
    private string _historyDetailText = "选择一条历史记录查看详情。";

    public Task OnActivatedAsync() => RefreshAsync();

    partial void OnIsRunningChanged(bool value)
    {
        RunBenchmarkCommand.NotifyCanExecuteChanged();
        CancelBenchmarkCommand.NotifyCanExecuteChanged();
        DeleteHistoryCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedHistoryChanged(BenchmarkRunSummary? value)
    {
        DeleteHistoryCommand.NotifyCanExecuteChanged();
        _ = LoadHistoryDetailAsync(value);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        RefreshInstances();
        try
        {
            await RefreshHistoryCoreAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载基准测试历史失败。");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunBenchmarkAsync()
    {
        if (SelectedInstance is not { IsReady: true } instance)
        {
            Notifications?.Notify("请选择一个状态为“运行中”的实例。");
            return;
        }

        var (options, issues) = BuildOptions();
        if (options is null)
        {
            if (Dialogs is not null)
            {
                await Dialogs.ShowWarningAsync("无法开始基准测试", string.Join("\n", issues))
                    .ConfigureAwait(true);
            }

            Notifications?.Notify("基准配置无效：" + issues.FirstOrDefault());
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsRunning = true;
        CurrentGroups.Clear();
        CurrentSamples.Clear();
        _currentRecord = null;
        CurrentResultText = "运行中…";
        ProgressValue = 0;
        ProgressText = string.Empty;
        StatusText = "准备中…";

        try
        {
            var record = await _runner
                .RunAsync(instance.InstanceId, options, OnProgress, _cts.Token)
                .ConfigureAwait(true);
            _currentRecord = record;
            ApplyRecord(record, CurrentGroups, CurrentSamples);
            CurrentResultText = DescribeRecord(record, includeConfig: true);
            StatusText = record.State switch
            {
                BenchmarkRunState.Succeeded when record.ErrorSummary is null => "测试完成，结果已保存。",
                BenchmarkRunState.Succeeded => "测试完成（存在失败样本或提前中止，详见结果说明）。",
                BenchmarkRunState.Failed => "测试失败：" + (record.ErrorSummary ?? "未知原因。"),
                BenchmarkRunState.Canceled => "已取消（保留已完成的样本）。",
                _ => "已结束。",
            };
            Notifications?.Notify(
                record.State == BenchmarkRunState.Succeeded
                    ? "基准测试完成，结果已保存到历史记录。"
                    : "基准测试" + BenchmarkReportFormatter.StateText(record.State) + "。");
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消。";
        }
        catch (InvalidOperationException ex)
        {
            StatusText = "无法开始：" + ex.Message;
            Notifications?.Notify("无法开始基准测试：" + ex.Message);
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            try
            {
                await RefreshHistoryCoreAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "刷新基准测试历史失败。");
            }
        }
    }

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelBenchmark()
    {
        try
        {
            _cts?.Cancel();
            StatusText = "正在取消…";
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool CanCancel() => IsRunning;

    [RelayCommand]
    private void ApplyPreset(string? key)
    {
        switch (key)
        {
            case "quick":
                PromptTokensText = "512";
                GenerateTokens = 128;
                Repetitions = 3;
                WarmupRuns = 1;
                CooldownSeconds = 0;
                break;

            case "standard":
                PromptTokensText = "512, 2048";
                GenerateTokens = 128;
                Repetitions = 3;
                WarmupRuns = 1;
                CooldownSeconds = 0;
                break;

            case "long":
                PromptTokensText = "2048, 4096";
                GenerateTokens = 256;
                Repetitions = 1;
                WarmupRuns = 1;
                CooldownSeconds = 5;
                break;

            default:
                return;
        }

        Notifications?.Notify("已应用预设参数。");
    }

    [RelayCommand(CanExecute = nameof(CanDeleteHistory))]
    private async Task DeleteHistoryAsync()
    {
        var summary = SelectedHistory;
        if (summary is null || Dialogs is null)
        {
            return;
        }

        var confirmed = await Dialogs.ConfirmAsync(new ConfirmDialogOptions
        {
            Title = "删除基准记录",
            Message = $"确定删除 {Formatting.LocalTime(summary.StartedUtc)} 的基准测试记录吗？此操作不可撤销。",
            IsDestructive = true,
        }).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _store.DeleteAsync(summary.RunId).ConfigureAwait(true);
            Notifications?.Notify("基准测试记录已删除。");
            await RefreshHistoryCoreAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除基准记录失败（RunId={RunId}）。", summary.RunId);
            Notifications?.Notify("删除失败：" + ex.Message);
        }
    }

    private bool CanDeleteHistory() => !IsRunning && SelectedHistory is not null;

    [RelayCommand]
    private async Task CopyReportAsync(string? which)
    {
        var record = ResolveRecord(which);
        if (record is null)
        {
            Notifications?.Notify("没有可复制的基准记录。");
            return;
        }

        var text = BenchmarkReportFormatter.BuildMarkdown(record);
        var (success, error) = await _clipboard.SetTextAsync(text).ConfigureAwait(true);
        Notifications?.Notify(success
            ? "基准报告已复制到剪贴板。"
            : "复制失败：" + (error ?? "未知错误"));
    }

    [RelayCommand]
    private async Task ExportReportAsync(string? which)
    {
        var record = ResolveRecord(which);
        if (record is null)
        {
            Notifications?.Notify("没有可导出的基准记录。");
            return;
        }

        var defaultName = $"benchmark-{record.StartedUtc.ToLocalTime():yyyyMMdd-HHmmss}.json";
        var path = Dialogs?.PickSaveFile("导出基准测试报告", "JSON 文件 (*.json)|*.json", defaultName);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, BenchmarkReportFormatter.BuildJson(record)).ConfigureAwait(true);
            Notifications?.Notify("报告已导出：" + path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "导出基准报告失败。");
            Notifications?.Notify("导出失败：" + ex.Message);
            if (Dialogs is not null)
            {
                await Dialogs.ShowErrorAsync("导出失败", ex.Message).ConfigureAwait(true);
            }
        }
    }

    /// <summary>复制/导出目标记录（"history" 为历史详情，其余为本次结果）。</summary>
    public BenchmarkRunRecord? ResolveRecord(string? which)
        => string.Equals(which, "history", StringComparison.OrdinalIgnoreCase)
            ? _historyRecord
            : _currentRecord;

    /// <summary>构造并校验运行配置（供命令与测试使用）。</summary>
    public (BenchmarkOptions? Options, IReadOnlyList<string> Issues) BuildOptions()
    {
        var issues = new List<string>();
        var lengths = new List<int>();
        foreach (var part in (PromptTokensText ?? string.Empty).Split(
            new[] { ',', '，', ';', '；', '|', ' ', '\t' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                if (!lengths.Contains(value))
                {
                    lengths.Add(value);
                }
            }
            else
            {
                issues.Add($"无法解析提示长度“{part}”，请输入以逗号分隔的整数（token 数）。");
            }
        }

        lengths.Sort();
        var options = new BenchmarkOptions
        {
            PromptTokens = lengths,
            GenerateTokens = GenerateTokens,
            Repetitions = Repetitions,
            WarmupRuns = WarmupRuns,
            CooldownSeconds = CooldownSeconds,
        };
        issues.AddRange(BenchmarkLimits.Validate(options));
        return issues.Count == 0 ? (options, issues) : (null, issues);
    }

    public void Dispose()
    {
        _instances.InstanceChanged -= OnInstanceChanged;
        _cts?.Dispose();
        _cts = null;
    }

    private void OnProgress(BenchmarkProgress progress)
    {
        RunOnUi(() =>
        {
            StatusText = progress.StageText;
            ProgressValue = progress.TotalSamples > 0
                ? (double)progress.CompletedSamples / progress.TotalSamples
                : 0;
            ProgressText = $"已完成 {progress.CompletedSamples}/{progress.TotalSamples} 个样本";
            ApplyRecord(progress.Run, CurrentGroups, CurrentSamples);
        });
    }

    private void OnInstanceChanged(object? sender, Guid instanceId)
        => RunOnUi(RefreshInstances);

    private void RefreshInstances()
    {
        var previousId = SelectedInstance?.InstanceId;
        Instances.Clear();
        foreach (var instance in _instances.GetInstances())
        {
            Instances.Add(new BenchmarkInstanceOption(instance));
        }

        SelectedInstance = Instances.FirstOrDefault(i => i.InstanceId == previousId)
            ?? Instances.FirstOrDefault(i => i.IsReady)
            ?? Instances.FirstOrDefault();
    }

    private async Task RefreshHistoryCoreAsync()
    {
        var previousId = SelectedHistory?.RunId;
        var items = await _store.ListAsync(HistoryLimit).ConfigureAwait(true);
        History.Clear();
        foreach (var item in items)
        {
            History.Add(item);
        }

        SelectedHistory = History.FirstOrDefault(h => h.RunId == previousId)
            ?? History.FirstOrDefault();
    }

    private async Task LoadHistoryDetailAsync(BenchmarkRunSummary? summary)
    {
        if (summary is null)
        {
            _historyRecord = null;
            HistoryGroups.Clear();
            HistorySamples.Clear();
            HistoryDetailText = "选择一条历史记录查看详情。";
            return;
        }

        try
        {
            var record = await _store.GetAsync(summary.RunId).ConfigureAwait(true);
            if (record is null || SelectedHistory?.RunId != summary.RunId)
            {
                return;
            }

            _historyRecord = record;
            ApplyRecord(record, HistoryGroups, HistorySamples);
            HistoryDetailText = DescribeRecord(record, includeConfig: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载基准历史详情失败（RunId={RunId}）。", summary.RunId);
        }
    }

    private static void ApplyRecord(
        BenchmarkRunRecord record,
        ObservableCollection<BenchmarkGroupRow> groups,
        ObservableCollection<BenchmarkSampleRow> samples)
    {
        groups.Clear();
        foreach (var summary in BenchmarkStatistics.Aggregate(record))
        {
            groups.Add(BenchmarkGroupRow.From(summary));
        }

        samples.Clear();
        foreach (var sample in record.Samples)
        {
            samples.Add(BenchmarkSampleRow.From(sample));
        }
    }

    private static string DescribeRecord(BenchmarkRunRecord record, bool includeConfig)
    {
        var builder = new StringBuilder();
        builder.Append($"{Formatting.LocalTime(record.StartedUtc)} · 实例 {record.InstanceNameSnapshot}");
        if (!string.IsNullOrEmpty(record.ModelNameSnapshot))
        {
            builder.Append($" · 模型 {record.ModelNameSnapshot}");
        }

        builder.Append($" · {BenchmarkReportFormatter.StateText(record.State)}");
        if (record.DurationMs is { } duration)
        {
            builder.Append($" · 总耗时 {Formatting.Duration(TimeSpan.FromMilliseconds(duration))}");
        }

        if (!string.IsNullOrEmpty(record.VersionLabel))
        {
            builder.Append($" · llama.cpp {record.VersionLabel}");
        }

        if (includeConfig)
        {
            builder.Append(
                $" · 提示 {string.Join(" / ", record.Options.PromptTokens)} · " +
                $"生成 {record.Options.GenerateTokens} · " +
                $"重复 {record.Options.Repetitions} · " +
                $"预热 {record.Options.WarmupRuns}");
        }

        if (!string.IsNullOrEmpty(record.ErrorSummary))
        {
            builder.AppendLine().Append(record.ErrorSummary);
        }

        return builder.ToString();
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        // 无应用、已在 UI 线程、或 Dispatcher 所属线程已退出（测试进程中的残留 Application）时直接执行。
        if (dispatcher is null || dispatcher.CheckAccess() || !dispatcher.Thread.IsAlive)
        {
            action();
        }
        else
        {
            _ = dispatcher.BeginInvoke(action);
        }
    }
}
