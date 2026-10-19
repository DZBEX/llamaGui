using LlamaLauncher.App.ViewModels.Lab;
using LlamaLauncher.App.Services;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Workflows;
using LlamaLauncher.Domain.Workflows;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.UiTests;

/// <summary>
/// 测试用例页关键路径的 UI 逻辑测试（内存存储 + 真实工作流引擎）：
/// 回归「启动/刷新后用列表中的用例运行时报“请选择绑定的工作流”」——
/// 刷新时必须先填充工作流选项，再选中用例，用例绑定的工作流才能恢复到编辑器。
/// </summary>
public sealed class TestCaseViewModelTests
{
    [Fact]
    public async Task 刷新后恢复用例绑定的工作流并可运行通过断言()
    {
        var definitions = new InMemoryDefinitionStore();
        definitions.Add(CreateEchoWorkflow());
        var store = new InMemoryTestCaseStore();
        store.Add(new WorkflowTestCase
        {
            Id = "tc-echo",
            Name = "回显用例",
            WorkflowId = "wf-echo",
            Mode = WorkflowTestMode.Simulated,
            Inputs = new System.Text.Json.Nodes.JsonObject { ["message"] = "hello" },
            Assertions =
            {
                new WorkflowAssertion
                {
                    Id = "a1",
                    Name = "回显一致",
                    Target = "output.echo",
                    Operator = AssertionOperator.Equals,
                    ExpectedJson = "\"hello\"",
                },
            },
        });

        var notifications = new RecordingNotificationService();
        var viewModel = CreateViewModel(store, definitions, notifications);

        await viewModel.RefreshCommand.ExecuteAsync(null);

        // 回归点：用例加载后必须已绑定其工作流（修复前为 null，导致运行被拒绝）
        Assert.NotNull(viewModel.SelectedWorkflow);
        Assert.Equal("wf-echo", viewModel.SelectedWorkflow!.WorkflowId);

        await viewModel.RunCaseCommand.ExecuteAsync(null);

        Assert.Contains("Succeeded", viewModel.RunResultText);
        Assert.Contains("断言全部通过", viewModel.RunResultText);
        Assert.NotEmpty(viewModel.RunOutcomes);
        Assert.All(viewModel.RunOutcomes, o => Assert.True(o.Passed, $"{o.Name}：{o.Message}"));
    }

    [Fact]
    public async Task 绑定的工作流不存在时运行给出明确提示()
    {
        var definitions = new InMemoryDefinitionStore();
        var store = new InMemoryTestCaseStore();
        store.Add(new WorkflowTestCase
        {
            Id = "tc-missing",
            Name = "缺失绑定",
            WorkflowId = "wf-not-exists",
            Mode = WorkflowTestMode.Simulated,
        });

        var notifications = new RecordingNotificationService();
        var viewModel = CreateViewModel(store, definitions, notifications);

        await viewModel.RefreshCommand.ExecuteAsync(null);
        await viewModel.RunCaseCommand.ExecuteAsync(null);

        Assert.Null(viewModel.SelectedWorkflow);
        Assert.Contains(notifications.Messages, m => m.Contains("请选择绑定的工作流"));
    }

    private static WorkflowDefinition CreateEchoWorkflow()
    {
        var workflow = new WorkflowDefinition
        {
            Id = "wf-echo",
            Name = "回显工作流",
            Inputs = { new WorkflowVariable { Name = "message", Required = true, Type = WorkflowValueType.String } },
        };
        workflow.Nodes.Add(new WorkflowNode { Id = "s1", TypeId = NodeTypeCatalog.Start, Name = "开始", X = 60, Y = 120 });
        workflow.Nodes.Add(new WorkflowNode
        {
            Id = "t1",
            TypeId = NodeTypeCatalog.Transform,
            Name = "回显",
            X = 320,
            Y = 120,
            Config = WorkflowJson.ToConfigObject(new TransformNodeConfig
            {
                BaseExpression = "up.s1.out",
                Operations =
                {
                    new TransformOperation { Id = "o1", Kind = TransformOperationKind.Assign, Target = "echo", Source = "input.message" },
                },
            }),
        });
        workflow.Nodes.Add(new WorkflowNode
        {
            Id = "e1",
            TypeId = NodeTypeCatalog.End,
            Name = "结束",
            X = 580,
            Y = 120,
            Config = WorkflowJson.ToConfigObject(new EndNodeConfig
            {
                OutputMappings = { new WorkflowDataMapping { Target = "echo", Source = "up.t1.out.echo" } },
            }),
        });
        workflow.Edges.Add(new WorkflowEdge { Id = "x1", SourceNodeId = "s1", TargetNodeId = "t1" });
        workflow.Edges.Add(new WorkflowEdge { Id = "x2", SourceNodeId = "t1", TargetNodeId = "e1" });
        return workflow;
    }

    private static TestCaseViewModel CreateViewModel(
        IWorkflowTestCaseStore store,
        IWorkflowDefinitionStore definitions,
        INotificationService notifications)
    {
        var executors = WorkflowExecutors.CreateDefault();
        var engine = new WorkflowEngine(executors, new WorkflowValidator(executors), NullLogger<WorkflowEngine>.Instance);
        var runner = new WorkflowRunnerService(
            definitions,
            new InMemoryRunStore(),
            new UnusedDecisionClient(),
            new StubModelResolver(),
            new WorkflowAlgorithmRegistry(new IWorkflowAlgorithm[] { new SampleRankingAlgorithm() }),
            engine,
            NullLogger<WorkflowRunnerService>.Instance);

        return new TestCaseViewModel(
            store,
            definitions,
            runner,
            notifications,
            new FakeDialogService(),
            new FakeClipboardService(),
            NullLogger<TestCaseViewModel>.Instance);
    }

    private sealed class InMemoryDefinitionStore : IWorkflowDefinitionStore
    {
        private readonly Dictionary<string, WorkflowDefinition> _definitions = new(StringComparer.Ordinal);

        public void Add(WorkflowDefinition definition) => _definitions[definition.Id] = definition;

        public Task<IReadOnlyList<WorkflowDefinitionSummary>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkflowDefinitionSummary>>(_definitions.Values
                .Select(d => new WorkflowDefinitionSummary(d.Id, d.Name, d.Description, d.Nodes.Count, d.Edges.Count, d.UpdatedAt, 0))
                .ToList());

        public Task<WorkflowLoadResult> LoadAsync(string workflowId, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkflowLoadResult(_definitions.GetValueOrDefault(workflowId)));

        public Task SaveAsync(WorkflowDefinition definition, string? snapshotLabel = null, string? snapshotNote = null, CancellationToken cancellationToken = default)
        {
            Add(definition);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string workflowId, CancellationToken cancellationToken = default)
            => Task.FromResult(_definitions.Remove(workflowId));

        public Task<IReadOnlyList<WorkflowVersionSnapshot>> ListSnapshotsAsync(string workflowId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkflowVersionSnapshot>>(Array.Empty<WorkflowVersionSnapshot>());

        public Task<WorkflowVersionSnapshot?> LoadSnapshotAsync(string workflowId, string snapshotId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkflowVersionSnapshot?>(null);

        public Task<WorkflowLoadResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ExportAsync(WorkflowDefinition definition, string destinationPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class InMemoryTestCaseStore : IWorkflowTestCaseStore
    {
        private readonly List<WorkflowTestCase> _cases = new();

        public void Add(WorkflowTestCase testCase) => _cases.Add(testCase);

        public Task<IReadOnlyList<WorkflowTestCase>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkflowTestCase>>(_cases.ToList());

        public Task<WorkflowTestCase?> LoadAsync(string testCaseId, CancellationToken cancellationToken = default)
            => Task.FromResult(_cases.FirstOrDefault(c => c.Id == testCaseId));

        public Task SaveAsync(WorkflowTestCase testCase, CancellationToken cancellationToken = default)
        {
            _cases.RemoveAll(c => c.Id == testCase.Id);
            _cases.Add(testCase);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string testCaseId, CancellationToken cancellationToken = default)
            => Task.FromResult(_cases.RemoveAll(c => c.Id == testCaseId) > 0);

        public Task<WorkflowTestCaseLoadResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ExportAsync(WorkflowTestCase testCase, string destinationPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class InMemoryRunStore : IWorkflowRunStore
    {
        public Task AppendAsync(WorkflowRunRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<WorkflowRunSummary>> ListAsync(WorkflowRunQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkflowRunSummary>>(Array.Empty<WorkflowRunSummary>());

        public Task<WorkflowRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkflowRunRecord?>(null);

        public Task<bool> DeleteAsync(string runId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<int> PruneAsync(int keepLatest, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class UnusedDecisionClient : IModelDecisionClient
    {
        public Task<DecisionCallResult> CallAsync(DecisionCallRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("模拟模式不应调用真实模型。");
    }

    private sealed class StubModelResolver : IWorkflowModelResolver
    {
        private static readonly WorkflowModelTarget Target = new(
            "inst-stub",
            "桩实例",
            "http://127.0.0.1:18081",
            true,
            null,
            new System.Text.Json.Nodes.JsonObject());

        public Task<IReadOnlyList<WorkflowModelTarget>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkflowModelTarget>>(new[] { Target });

        public Task<WorkflowModelTarget?> ResolveAsync(string? instanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkflowModelTarget?>(Target);

        public Task<string?> GetApiKeyAsync(string instanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class FakeDialogService : LlamaLauncher.App.Services.IDialogService
    {
        public Task<bool> ConfirmAsync(LlamaLauncher.App.Services.ConfirmDialogOptions options) => Task.FromResult(true);

        public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;

        public Task ShowWarningAsync(string title, string message) => Task.CompletedTask;

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public Task<string?> PromptAsync(string title, string message, string initialValue = "")
            => Task.FromResult<string?>(null);

        public Task<string?> PromptWithConfirmationAsync(string title, string message, string requiredConfirmationKeyword)
            => Task.FromResult<string?>(null);

        public Task<(bool Confirmed, bool CheckboxChecked)> ConfirmWithCheckboxAsync(
            LlamaLauncher.App.Services.ConfirmDialogOptions options,
            string checkboxText,
            bool defaultChecked = false)
            => Task.FromResult((false, false));

        public string? PickOpenFile(string title, string filter, string? initialDirectory = null) => null;

        public string? PickFolder(string title, string? initialDirectory = null) => null;

        public string? PickSaveFile(string title, string filter, string? defaultFileName = null, string? initialDirectory = null) => null;
    }

    private sealed class FakeClipboardService : LlamaLauncher.App.Services.IClipboardService
    {
        public Task<(bool Success, string? Error)> SetTextAsync(string text) => Task.FromResult((true, (string?)null));
    }

    private sealed class RecordingNotificationService : LlamaLauncher.App.Services.INotificationService
    {
        public List<string> Messages { get; } = new();

        public event Action<string?>? StatusChanged;

        public void SetStatus(string message) => Messages.Add(message);

        public void ClearStatus() => StatusChanged?.Invoke(null);

        public void Notify(string message) => Messages.Add(message);
    }
}
