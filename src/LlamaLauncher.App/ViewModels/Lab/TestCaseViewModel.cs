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

/// <summary>模拟响应编辑行。</summary>
public sealed partial class MockRow : ObservableObject
{
    public MockRow()
    {
    }

    public MockRow(string nodeId, WorkflowMockResponse response)
    {
        _nodeId = nodeId;
        _outputJson = response.Output is null ? "null" : WorkflowJson.Serialize(response.Output);
        _branchId = response.BranchId ?? string.Empty;
        _errorMessage = response.ErrorMessage ?? string.Empty;
    }

    [ObservableProperty]
    private string _nodeId = string.Empty;

    [ObservableProperty]
    private string _outputJson = "null";

    [ObservableProperty]
    private string _branchId = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;
}

/// <summary>断言编辑行。</summary>
public sealed partial class AssertionRow : ObservableObject
{
    public AssertionRow(WorkflowAssertion? assertion = null)
    {
        _id = assertion?.Id is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N")[..8];
        if (assertion is null)
        {
            return;
        }

        _name = assertion.Name;
        _target = assertion.Target;
        _operator = assertion.Operator;
        _expectedJson = assertion.ExpectedJson ?? string.Empty;
        _toleranceText = assertion.Tolerance?.ToString() ?? string.Empty;
        _severity = assertion.Severity;
    }

    public string Id => _id;

    private readonly string _id;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _target = "output.route";

    [ObservableProperty]
    private AssertionOperator _operator = AssertionOperator.Equals;

    [ObservableProperty]
    private string _expectedJson = string.Empty;

    [ObservableProperty]
    private string _toleranceText = string.Empty;

    [ObservableProperty]
    private WorkflowIssueSeverity _severity = WorkflowIssueSeverity.Error;

    public WorkflowAssertion ToModel() => new()
    {
        Id = Id,
        Name = Name,
        Target = Target.Trim(),
        Operator = Operator,
        ExpectedJson = string.IsNullOrWhiteSpace(ExpectedJson) ? null : ExpectedJson.Trim(),
        Tolerance = double.TryParse(ToleranceText, out var tolerance) ? tolerance : null,
        Severity = Severity,
    };
}

/// <summary>
/// 测试用例管理：新建/编辑/复制/删除、绑定工作流与版本、输入、模拟响应、断言，
/// 导入导出、单场景运行与断言结果展示。
/// </summary>
public sealed partial class TestCaseViewModel : ViewModelBase, IPageViewModel
{
    private readonly IWorkflowTestCaseStore _store;
    private readonly IWorkflowDefinitionStore _definitions;
    private readonly WorkflowRunnerService _runner;
    private readonly ILogger<TestCaseViewModel> _logger;

    public TestCaseViewModel(
        IWorkflowTestCaseStore store,
        IWorkflowDefinitionStore definitions,
        WorkflowRunnerService runner,
        INotificationService notifications,
        IDialogService dialogs,
        IClipboardService clipboard,
        ILogger<TestCaseViewModel> logger)
    {
        _store = store;
        _definitions = definitions;
        _runner = runner;
        _logger = logger;
        Notifications = notifications;
        Dialogs = dialogs;
        Clipboard = clipboard;
    }

    private IClipboardService Clipboard { get; }

    public ObservableCollection<WorkflowTestCase> Cases { get; } = new();

    public ObservableCollection<WorkflowDefinitionSummary> WorkflowChoices { get; } = new();

    public ObservableCollection<MockRow> Mocks { get; } = new();

    public ObservableCollection<AssertionRow> Assertions { get; } = new();

    public ObservableCollection<AssertionOutcome> RunOutcomes { get; } = new();

    public IReadOnlyList<WorkflowTestMode> ModeOptions { get; } = new[]
    {
        WorkflowTestMode.Simulated,
        WorkflowTestMode.LocalModel,
    };

    public IReadOnlyList<AssertionOperator> OperatorOptions { get; } = Enum.GetValues<AssertionOperator>();

    public IReadOnlyList<WorkflowIssueSeverity> SeverityOptions { get; } = new[]
    {
        WorkflowIssueSeverity.Error,
        WorkflowIssueSeverity.Warning,
    };

    [ObservableProperty]
    private WorkflowTestCase? _selectedCase;

    [ObservableProperty]
    private string _nameText = string.Empty;

    [ObservableProperty]
    private string _descriptionText = string.Empty;

    [ObservableProperty]
    private string _tagsText = string.Empty;

    [ObservableProperty]
    private WorkflowDefinitionSummary? _selectedWorkflow;

    [ObservableProperty]
    private string _versionLabelText = string.Empty;

    [ObservableProperty]
    private WorkflowTestMode _mode = WorkflowTestMode.Simulated;

    [ObservableProperty]
    private string _inputsJson = "{}";

    [ObservableProperty]
    private string _ignoreFieldsText = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _runResultText = "尚未运行。";

    [ObservableProperty]
    private string _editorStatusText = "选择左侧用例或新建一个用例。";

    public Task OnActivatedAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var cases = await _store.ListAsync().ConfigureAwait(true);
            var selectedId = SelectedCase?.Id;
            Cases.Clear();
            foreach (var testCase in cases)
            {
                Cases.Add(testCase);
            }

            SelectedCase = Cases.FirstOrDefault(c => c.Id == selectedId) ?? Cases.FirstOrDefault();

            var workflows = await _definitions.ListAsync().ConfigureAwait(true);
            WorkflowChoices.Clear();
            foreach (var workflow in workflows)
            {
                WorkflowChoices.Add(workflow);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "刷新测试用例失败");
            Notifications?.Notify("刷新失败：" + ex.Message);
        }
    }

    partial void OnSelectedCaseChanged(WorkflowTestCase? value) => LoadCaseIntoEditor(value);

    private void LoadCaseIntoEditor(WorkflowTestCase? testCase)
    {
        RunOutcomes.Clear();
        if (testCase is null)
        {
            NameText = string.Empty;
            DescriptionText = string.Empty;
            TagsText = string.Empty;
            SelectedWorkflow = WorkflowChoices.FirstOrDefault();
            VersionLabelText = string.Empty;
            Mode = WorkflowTestMode.Simulated;
            InputsJson = "{}";
            IgnoreFieldsText = string.Empty;
            Mocks.Clear();
            Assertions.Clear();
            EditorStatusText = "选择左侧用例或新建一个用例。";
            RunResultText = "尚未运行。";
            return;
        }

        NameText = testCase.Name;
        DescriptionText = testCase.Description;
        TagsText = string.Join(", ", testCase.Tags);
        SelectedWorkflow = WorkflowChoices.FirstOrDefault(w => w.WorkflowId == testCase.WorkflowId);
        VersionLabelText = testCase.WorkflowVersionLabel ?? string.Empty;
        Mode = testCase.Mode;
        InputsJson = testCase.Inputs is null ? "{}" : WorkflowJson.Serialize(testCase.Inputs);
        IgnoreFieldsText = string.Join(", ", testCase.IgnoreFields);
        Mocks.Clear();
        foreach (var pair in testCase.MockResponses)
        {
            Mocks.Add(new MockRow(pair.Key, pair.Value));
        }

        Assertions.Clear();
        foreach (var assertion in testCase.Assertions)
        {
            Assertions.Add(new AssertionRow(assertion));
        }

        EditorStatusText = $"用例 {testCase.Id} · 更新于 {testCase.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
    }

    [RelayCommand]
    private void NewCase()
    {
        SelectedCase = null;
        NameText = "新用例 " + DateTime.Now.ToString("HH:mm:ss");
        DescriptionText = string.Empty;
        TagsText = string.Empty;
        SelectedWorkflow = WorkflowChoices.FirstOrDefault();
        Mode = WorkflowTestMode.Simulated;
        InputsJson = "{}";
        EditorStatusText = "新用例（尚未保存）。";
    }

    [RelayCommand]
    private async Task SaveCaseAsync()
    {
        if (string.IsNullOrWhiteSpace(NameText))
        {
            await (Dialogs?.ShowWarningAsync("无法保存", "用例名称不能为空。") ?? Task.CompletedTask);
            return;
        }

        if (SelectedWorkflow is not { } workflow)
        {
            await (Dialogs?.ShowWarningAsync("无法保存", "请选择绑定的工作流。") ?? Task.CompletedTask);
            return;
        }

        var inputs = WorkflowJson.TryParse(InputsJson, out var inputsError) as JsonObject;
        if (inputs is null)
        {
            await (Dialogs?.ShowErrorAsync("输入无效", $"输入必须是 JSON 对象：{inputsError}") ?? Task.CompletedTask);
            return;
        }

        var mocks = new Dictionary<string, WorkflowMockResponse>();
        foreach (var row in Mocks.Where(m => !string.IsNullOrWhiteSpace(m.NodeId)))
        {
            var output = WorkflowJson.TryParse(row.OutputJson, out var mockError);
            if (output is null && !string.IsNullOrWhiteSpace(row.OutputJson) && row.OutputJson.Trim() != "null")
            {
                await (Dialogs?.ShowErrorAsync("模拟响应无效", $"节点 {row.NodeId} 的输出不是有效 JSON：{mockError}") ?? Task.CompletedTask);
                return;
            }

            mocks[row.NodeId.Trim()] = new WorkflowMockResponse
            {
                Output = output,
                BranchId = string.IsNullOrWhiteSpace(row.BranchId) ? null : row.BranchId.Trim(),
                ErrorMessage = string.IsNullOrWhiteSpace(row.ErrorMessage) ? null : row.ErrorMessage,
            };
        }

        var testCase = SelectedCase ?? new WorkflowTestCase { Id = Guid.NewGuid().ToString("N")[..12] };
        testCase.Name = NameText.Trim();
        testCase.Description = DescriptionText ?? string.Empty;
        testCase.Tags = (TagsText ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        testCase.WorkflowId = workflow.WorkflowId;
        testCase.WorkflowVersionLabel = string.IsNullOrWhiteSpace(VersionLabelText) ? null : VersionLabelText.Trim();
        testCase.Mode = Mode;
        testCase.Inputs = inputs;
        testCase.MockResponses = mocks;
        testCase.IgnoreFields = (IgnoreFieldsText ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        testCase.Assertions = Assertions.Select(a => a.ToModel()).ToList();

        try
        {
            await _store.SaveAsync(testCase).ConfigureAwait(true);
            Notifications?.Notify($"已保存用例“{testCase.Name}”。");
            await RefreshAsync().ConfigureAwait(true);
            SelectedCase = Cases.FirstOrDefault(c => c.Id == testCase.Id);
        }
        catch (Exception ex)
        {
            await (Dialogs?.ShowErrorAsync("保存失败", ex.Message) ?? Task.CompletedTask);
        }
    }

    [RelayCommand]
    private async Task DeleteCaseAsync()
    {
        if (SelectedCase is not { } testCase)
        {
            return;
        }

        var confirmed = await (Dialogs?.ConfirmAsync(new ConfirmDialogOptions
        {
            Title = "删除测试用例",
            Message = $"确定删除用例“{testCase.Name}”？",
            PrimaryButtonText = "删除",
            IsDestructive = true,
        }) ?? Task.FromResult(false)).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        await _store.DeleteAsync(testCase.Id).ConfigureAwait(true);
        Notifications?.Notify("用例已删除。");
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DuplicateCaseAsync()
    {
        if (SelectedCase is not { } testCase)
        {
            return;
        }

        var copy = WorkflowJson.Deserialize<WorkflowTestCase>(WorkflowJson.Serialize(testCase));
        if (copy is null)
        {
            return;
        }

        copy.Id = Guid.NewGuid().ToString("N")[..12];
        copy.Name += " 副本";
        copy.CreatedUtc = default;
        copy.UpdatedUtc = default;
        await _store.SaveAsync(copy).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        SelectedCase = Cases.FirstOrDefault(c => c.Id == copy.Id);
        Notifications?.Notify("已复制用例。");
    }

    [RelayCommand]
    private async Task ImportCaseAsync()
    {
        var path = Dialogs?.PickOpenFile("导入测试用例", "测试用例 (*.testcase.json)|*.testcase.json|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*");
        if (path is null)
        {
            return;
        }

        var result = await _store.ImportAsync(path).ConfigureAwait(true);
        if (result.TestCase is null)
        {
            await (Dialogs?.ShowErrorAsync("导入失败", string.Join("\n", result.Issues.Select(i => i.Message))) ?? Task.CompletedTask);
            return;
        }

        if (result.HasErrors)
        {
            await (Dialogs?.ShowWarningAsync("导入存在问题", string.Join("\n", result.Issues.Select(i => i.Message))) ?? Task.CompletedTask);
        }

        await _store.SaveAsync(result.TestCase).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        SelectedCase = Cases.FirstOrDefault(c => c.Id == result.TestCase!.Id);
        Notifications?.Notify("用例已导入（未自动运行）。");
    }

    [RelayCommand]
    private async Task ExportCaseAsync()
    {
        if (SelectedCase is not { } testCase)
        {
            return;
        }

        var path = Dialogs?.PickSaveFile(
            "导出测试用例",
            "测试用例 (*.testcase.json)|*.testcase.json",
            testCase.Id + ".testcase.json");
        if (path is null)
        {
            return;
        }

        await _store.ExportAsync(testCase, path).ConfigureAwait(true);
        Notifications?.Notify($"已导出：{path}");
    }

    [RelayCommand]
    private void AddMock()
    {
        Mocks.Add(new MockRow { NodeId = SuggestDecisionNodeId() });
    }

    [RelayCommand]
    private void RemoveMock(MockRow? row)
    {
        if (row is not null)
        {
            Mocks.Remove(row);
        }
    }

    private string SuggestDecisionNodeId()
    {
        if (SelectedWorkflow is null)
        {
            return string.Empty;
        }

        var loaded = _definitions.LoadAsync(SelectedWorkflow.WorkflowId).GetAwaiter().GetResult();
        return loaded.Definition?.Nodes
            .FirstOrDefault(n => n.TypeId == NodeTypeCatalog.Decision)?.Id ?? string.Empty;
    }

    [RelayCommand]
    private void AddAssertion()
    {
        Assertions.Add(new AssertionRow());
    }

    [RelayCommand]
    private void RemoveAssertion(AssertionRow? row)
    {
        if (row is not null)
        {
            Assertions.Remove(row);
        }
    }

    [RelayCommand]
    private void GenerateInputsTemplate()
    {
        if (SelectedWorkflow is null)
        {
            return;
        }

        var loaded = _definitions.LoadAsync(SelectedWorkflow.WorkflowId).GetAwaiter().GetResult();
        if (loaded.Definition is null)
        {
            return;
        }

        var template = new JsonObject();
        foreach (var variable in loaded.Definition.Inputs)
        {
            template[variable.Name] = variable.ExampleJson is not null
                ? WorkflowJson.TryParse(variable.ExampleJson, out _)
                : variable.DefaultValueJson is not null
                    ? WorkflowJson.TryParse(variable.DefaultValueJson, out _)
                    : null;
        }

        InputsJson = WorkflowJson.Serialize(template);
        Notifications?.Notify("已生成输入模板（请补充示例值）。");
    }

    [RelayCommand]
    private async Task RunCaseAsync()
    {
        if (SelectedWorkflow is not { } workflow)
        {
            Notifications?.Notify("请选择绑定的工作流。");
            return;
        }

        var inputs = WorkflowJson.TryParse(InputsJson, out var inputsError) as JsonObject;
        if (inputs is null)
        {
            await (Dialogs?.ShowErrorAsync("输入无效", $"输入必须是 JSON 对象：{inputsError}") ?? Task.CompletedTask);
            return;
        }

        var loaded = await _definitions.LoadAsync(workflow.WorkflowId).ConfigureAwait(true);
        if (loaded.Definition is null)
        {
            await (Dialogs?.ShowErrorAsync("无法运行", $"工作流加载失败：{string.Join("；", loaded.Issues.Select(i => i.Message))}") ?? Task.CompletedTask);
            return;
        }

        var definition = loaded.Definition;
        if (!string.IsNullOrWhiteSpace(VersionLabelText))
        {
            var snapshot = await _definitions.LoadSnapshotAsync(workflow.WorkflowId, VersionLabelText.Trim()).ConfigureAwait(true);
            if (snapshot?.Definition is not null)
            {
                definition = snapshot.Definition;
            }
            else
            {
                Notifications?.Notify($"未找到快照“{VersionLabelText}”，将使用当前版本运行。");
            }
        }

        var mocks = new Dictionary<string, WorkflowMockResponse>();
        foreach (var row in Mocks.Where(m => !string.IsNullOrWhiteSpace(m.NodeId)))
        {
            mocks[row.NodeId.Trim()] = new WorkflowMockResponse
            {
                Output = WorkflowJson.TryParse(row.OutputJson, out _),
                BranchId = string.IsNullOrWhiteSpace(row.BranchId) ? null : row.BranchId.Trim(),
                ErrorMessage = string.IsNullOrWhiteSpace(row.ErrorMessage) ? null : row.ErrorMessage,
            };
        }

        IsRunning = true;
        RunOutcomes.Clear();
        RunResultText = "运行中…";
        try
        {
            var result = await _runner.RunAsync(new WorkflowLaunchOptions
            {
                Definition = definition,
                Inputs = inputs,
                TestMode = Mode,
                MockResponses = mocks,
                VersionLabel = string.IsNullOrWhiteSpace(VersionLabelText) ? null : VersionLabelText.Trim(),
                PersistRunRecord = true,
            }).ConfigureAwait(true);

            var report = WorkflowAssertionEvaluator.Evaluate(
                new WorkflowTestCase
                {
                    Id = SelectedCase?.Id ?? "adhoc",
                    Name = NameText,
                    Assertions = Assertions.Select(a => a.ToModel()).ToList(),
                },
                result);
            foreach (var outcome in report.Outcomes)
            {
                RunOutcomes.Add(outcome);
            }

            RunResultText =
                $"运行 {result.State} · 耗时 {result.Record.DurationMs:0} ms · {report.Summary}" +
                (result.Record.ErrorSummary is null ? string.Empty : $"\n{result.Record.ErrorSummary}");
            RunResultText = RunResultText.Replace("\n", Environment.NewLine);
            Notifications?.Notify($"用例运行完成：{result.State}（{report.Summary}）。");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "用例运行失败");
            RunResultText = "运行失败：" + ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }
}
