using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.Application.Workflows;
using LlamaLauncher.Application.Workflows.Testing;
using LlamaLauncher.App.Services;
using LlamaLauncher.Domain.Workflows;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.App.ViewModels.Lab;

/// <summary>批量运行中的用例行。</summary>
public sealed partial class BatchCaseRow : ObservableObject
{
    public required WorkflowTestCase TestCase { get; init; }

    [ObservableProperty]
    private bool _selected = true;

    [ObservableProperty]
    private string _statusText = "待运行";

    public string Name => TestCase.Name;

    public string WorkflowName { get; set; } = string.Empty;

    public string ModeText => TestCase.Mode == WorkflowTestMode.Simulated ? "模拟" : "本地模型";
}

/// <summary>批量运行结果行。</summary>
public sealed partial class BatchResultRow : ObservableObject
{
    public required string CaseName { get; init; }

    [ObservableProperty]
    private string _stateText = "—";

    [ObservableProperty]
    private string _durationText = "—";

    [ObservableProperty]
    private int _failedAssertions;

    [ObservableProperty]
    private string? _runId;

    [ObservableProperty]
    private string? _message;

    public WorkflowRunResult? Result { get; set; }
}

/// <summary>
/// 批量测试：选择用例批量执行、受控并发（默认 1）、实时统计、暂停/继续/取消、
/// 失败原因查看与导出结果。
/// </summary>
public sealed partial class BatchTestViewModel : ViewModelBase, IPageViewModel
{
    private readonly IWorkflowTestCaseStore _store;
    private readonly IWorkflowDefinitionStore _definitions;
    private readonly WorkflowRunnerService _runner;
    private readonly ILogger<BatchTestViewModel> _logger;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _pauseGate;

    public BatchTestViewModel(
        IWorkflowTestCaseStore store,
        IWorkflowDefinitionStore definitions,
        WorkflowRunnerService runner,
        INotificationService notifications,
        IDialogService dialogs,
        ILogger<BatchTestViewModel> logger)
    {
        _store = store;
        _definitions = definitions;
        _runner = runner;
        _logger = logger;
        Notifications = notifications;
        Dialogs = dialogs;
    }

    public ObservableCollection<BatchCaseRow> Cases { get; } = new();

    public ObservableCollection<BatchResultRow> Results { get; } = new();

    [ObservableProperty]
    private int _concurrency = 1;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _completedCount;

    [ObservableProperty]
    private int _succeededCount;

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private int _skippedCount;

    [ObservableProperty]
    private string _statisticsText = "尚未运行。";

    [ObservableProperty]
    private BatchResultRow? _selectedResult;

    [ObservableProperty]
    private string? _resultDetailText;

    public Task OnActivatedAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var cases = await _store.ListAsync().ConfigureAwait(true);
            var workflows = await _definitions.ListAsync().ConfigureAwait(true);
            Cases.Clear();
            foreach (var testCase in cases)
            {
                Cases.Add(new BatchCaseRow
                {
                    TestCase = testCase,
                    WorkflowName = workflows.FirstOrDefault(w => w.WorkflowId == testCase.WorkflowId)?.Name ?? "（工作流缺失）",
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "刷新批量测试用例失败");
            Notifications?.Notify("刷新失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var row in Cases)
        {
            row.Selected = true;
        }
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var row in Cases)
        {
            row.Selected = false;
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }

        var selected = Cases.Where(c => c.Selected).ToList();
        if (selected.Count == 0)
        {
            Notifications?.Notify("请先选择要运行的用例。");
            return;
        }

        IsRunning = true;
        IsPaused = false;
        Results.Clear();
        TotalCount = selected.Count;
        CompletedCount = SucceededCount = FailedCount = SkippedCount = 0;
        _cts = new CancellationTokenSource();
        _pauseGate = null;
        UpdateStatistics();

        try
        {
            var workflows = await _definitions.ListAsync().ConfigureAwait(true);
            var definitions = new Dictionary<string, WorkflowDefinition>(StringComparer.Ordinal);
            foreach (var workflow in workflows)
            {
                var loaded = await _definitions.LoadAsync(workflow.WorkflowId).ConfigureAwait(true);
                if (loaded.Definition is not null)
                {
                    definitions[workflow.WorkflowId] = loaded.Definition;
                }
            }

            var concurrency = Math.Clamp(Concurrency, 1, 4);
            using var semaphore = new SemaphoreSlim(concurrency);
            var tasks = selected.Select(row => RunCaseAsync(row, definitions, semaphore, _cts.Token)).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            foreach (var row in Cases.Where(c => c.Selected && c.StatusText == "待运行"))
            {
                row.StatusText = "已取消";
                SkippedCount++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "批量测试失败");
            Notifications?.Notify("批量测试失败：" + ex.Message);
        }
        finally
        {
            IsRunning = false;
            IsPaused = false;
            _cts?.Dispose();
            _cts = null;
            UpdateStatistics();
        }
    }

    private async Task RunCaseAsync(
        BatchCaseRow row,
        IReadOnlyDictionary<string, WorkflowDefinition> definitions,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            Post(() => row.StatusText = "运行中");
            var testCase = row.TestCase;
            if (!definitions.TryGetValue(testCase.WorkflowId, out var definition))
            {
                CompleteCase(row, null, "绑定的工作流不存在。", failedAssertions: 0);
                return;
            }

            var mocks = testCase.MockResponses;
            var result = await _runner.RunAsync(new WorkflowLaunchOptions
            {
                Definition = definition,
                Inputs = testCase.Inputs ?? new JsonObject(),
                TestMode = testCase.Mode,
                MockResponses = mocks.Count == 0 ? null : mocks,
                VersionLabel = testCase.WorkflowVersionLabel,
                PersistRunRecord = true,
            }).ConfigureAwait(false);

            var report = WorkflowAssertionEvaluator.Evaluate(testCase, result);
            var message = result.Record.ErrorSummary
                ?? (report.Passed ? null : string.Join("；", report.Outcomes.Where(o => !o.Passed).Select(o => o.Message)));
            CompleteCase(row, result, message, report.FailedCount);
        }
        catch (OperationCanceledException)
        {
            Post(() =>
            {
                row.StatusText = "已取消";
                SkippedCount++;
                UpdateStatistics();
            });
        }
        catch (Exception ex)
        {
            CompleteCase(row, null, "运行异常：" + ex.Message, failedAssertions: 0);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private void CompleteCase(BatchCaseRow row, WorkflowRunResult? result, string? message, int failedAssertions)
    {
        Post(() =>
        {
            var succeeded = result is not null
                && result.State == WorkflowRunState.Succeeded
                && failedAssertions == 0;
            row.StatusText = succeeded ? "通过" : "失败";
            if (succeeded)
            {
                SucceededCount++;
            }
            else
            {
                FailedCount++;
            }

            CompletedCount++;
            Results.Add(new BatchResultRow
            {
                CaseName = row.TestCase.Name,
                StateText = succeeded ? "通过" : result?.State.ToString() ?? "错误",
                DurationText = result?.Record.DurationMs is { } duration ? $"{duration:0} ms" : "—",
                FailedAssertions = failedAssertions,
                RunId = result?.Record.RunId,
                Message = message,
                Result = result,
            });
            UpdateStatistics();
        });
    }

    private async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        Task? wait;
        lock (this)
        {
            wait = IsPaused ? (_pauseGate ??= CreateGate()).Task : null;
        }

        if (wait is not null)
        {
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource<bool> CreateGate()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [RelayCommand]
    private void Pause()
    {
        if (!IsRunning)
        {
            return;
        }

        IsPaused = true;
        StatisticsText = "已暂停（当前运行中的用例完成后不再启动新用例）。";
    }

    [RelayCommand]
    private void Resume()
    {
        TaskCompletionSource<bool>? gate;
        lock (this)
        {
            IsPaused = false;
            gate = _pauseGate;
            _pauseGate = null;
        }

        gate?.TrySetResult(true);
        UpdateStatistics();
    }

    [RelayCommand]
    private void CancelRun()
    {
        _cts?.Cancel();
        lock (this)
        {
            var gate = _pauseGate;
            _pauseGate = null;
            gate?.TrySetResult(true);
        }
    }

    partial void OnSelectedResultChanged(BatchResultRow? value)
    {
        if (value?.Result is not { } result)
        {
            ResultDetailText = null;
            return;
        }

        var lines = new List<string>
        {
            $"RunId：{result.Record.RunId}",
            $"状态：{result.Record.State} · 耗时：{result.Record.DurationMs:0} ms · 模式：{result.Record.TestMode}",
            $"开始：{result.Record.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
        };
        if (result.Record.ErrorSummary is { } error)
        {
            lines.Add("错误：" + error);
        }

        lines.Add("输出：" + (result.Output is null ? "（无）" : WorkflowJson.Compact(result.Output)));
        ResultDetailText = string.Join(Environment.NewLine, lines);
    }

    [RelayCommand]
    private async Task ExportResultsAsync()
    {
        if (Results.Count == 0)
        {
            Notifications?.Notify("还没有结果可导出。");
            return;
        }

        var path = Dialogs?.PickSaveFile(
            "导出批量测试结果",
            "JSON 文件 (*.json)|*.json|CSV 文件 (*.csv)|*.csv",
            $"batch-results-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        if (path is null)
        {
            return;
        }

        try
        {
            if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                var csv = new System.Text.StringBuilder("用例,状态,耗时(ms),失败断言,RunId,说明\n");
                foreach (var row in Results)
                {
                    csv.AppendLine(string.Join(",",
                        Csv(row.CaseName),
                        Csv(row.StateText),
                        Csv(row.DurationText),
                        row.FailedAssertions,
                        Csv(row.RunId ?? string.Empty),
                        Csv(row.Message ?? string.Empty)));
                }

                await File.WriteAllTextAsync(path, csv.ToString()).ConfigureAwait(true);
            }
            else
            {
                var root = new JsonObject
                {
                    ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["total"] = TotalCount,
                    ["succeeded"] = SucceededCount,
                    ["failed"] = FailedCount,
                    ["results"] = new JsonArray(Results.Select(r => (JsonNode)new JsonObject
                    {
                        ["case"] = r.CaseName,
                        ["state"] = r.StateText,
                        ["durationMs"] = r.Result?.Record.DurationMs,
                        ["failedAssertions"] = r.FailedAssertions,
                        ["runId"] = r.RunId,
                        ["message"] = r.Message,
                    }).ToArray()),
                };
                await File.WriteAllTextAsync(path, WorkflowJson.Serialize(root)).ConfigureAwait(true);
            }

            Notifications?.Notify($"已导出：{path}");
        }
        catch (Exception ex)
        {
            await (Dialogs?.ShowErrorAsync("导出失败", ex.Message) ?? Task.CompletedTask);
        }
    }

    private static string Csv(string value)
        => value.Contains(',') || value.Contains('"')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    private void UpdateStatistics()
    {
        if (IsPaused)
        {
            return;
        }

        var average = Results.Count == 0
            ? "—"
            : $"{(Results.Where(r => r.Result?.Record.DurationMs is not null).Select(r => r.Result!.Record.DurationMs!.Value).DefaultIfEmpty(0).Average()):0} ms";
        StatisticsText =
            $"完成 {CompletedCount}/{TotalCount} · 通过 {SucceededCount} · 失败 {FailedCount} · 取消 {SkippedCount} · 平均 {average}";
    }

    private static void Post(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
