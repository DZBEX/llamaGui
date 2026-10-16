using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Workflows;
using LlamaLauncher.Domain.Workflows;

namespace LlamaLauncher.UnitTests.Workflows;

/// <summary>测试用工作流运行环境（假模型服务、内存存储、可编程交互）。</summary>
public sealed class WorkflowTestEnvironment
{
    public FakeModelResolver Models { get; } = new();

    public FakeDecisionClient Decisions { get; } = new();

    public InMemoryDefinitionStore Definitions { get; } = new();

    public FakeInteractionBroker Interactions { get; } = new();

    public IWorkflowAlgorithmRegistry Algorithms { get; } = new WorkflowAlgorithmRegistry(new IWorkflowAlgorithm[]
    {
        new SampleRankingAlgorithm(),
    });

    public RecordingObserver Observer { get; } = new();

    public WorkflowEngine CreateEngine()
    {
        var executors = WorkflowExecutors.CreateDefault();
        var validator = new WorkflowValidator(executors);
        return new WorkflowEngine(
            executors,
            validator,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowEngine>.Instance);
    }

    public WorkflowServices CreateServices(IWorkflowChildRunner? childRunner = null)
        => new()
        {
            DecisionClient = Decisions,
            ModelResolver = Models,
            DefinitionStore = Definitions,
            Algorithms = Algorithms,
            Interactions = Interactions,
            ChildRunner = childRunner,
        };

    public Task<WorkflowRunResult> RunAsync(
        WorkflowDefinition definition,
        JsonObject? inputs = null,
        WorkflowTestMode mode = WorkflowTestMode.LocalModel,
        IReadOnlyDictionary<string, WorkflowMockResponse>? mocks = null,
        WorkflowExecutionLimits? limits = null,
        bool observe = false)
    {
        var engine = CreateEngine();
        return engine.RunAsync(new WorkflowRunRequest
        {
            Definition = definition,
            Services = CreateServices(),
            Inputs = inputs,
            TestMode = mode,
            MockResponses = mocks,
            Observer = observe ? Observer : null,
            AppVersion = "test",
            Limits = limits ?? new WorkflowExecutionLimits(),
        });
    }
}

/// <summary>假决策客户端：可编程响应序列 + 请求记录。</summary>
public sealed class FakeDecisionClient : IModelDecisionClient
{
    public sealed record Call(DecisionCallRequest Request);

    public ConcurrentQueue<Call> Calls { get; } = new();

    /// <summary>下一个响应的行为（null 表示默认成功响应）。</summary>
    public Func<DecisionCallRequest, CancellationToken, Task<DecisionCallResult>>? Handler { get; set; }

    public DecisionCallResult DefaultResult(DecisionCallRequest request)
    {
        var answers = new JsonObject();
        foreach (var question in request.Questions)
        {
            answers[question.Key] = new JsonObject
            {
                ["type"] = question.Value?["type"]?.GetValue<string>() ?? "choice",
                ["choice"] = question.Key == "route" ? "billing" : "yes",
                ["probabilities"] = new JsonObject { ["billing"] = 0.9, ["other"] = 0.1 },
                ["noul"] = 0.8,
            };
        }

        var response = new JsonObject
        {
            ["model"] = "test-model",
            ["answers"] = answers,
            ["usage"] = new JsonObject { ["input_tokens"] = 42, ["output_tokens"] = 0 },
        };
        return DecisionCallResult.Ok(response, response.ToJsonString(), TimeSpan.FromMilliseconds(5));
    }

    public async Task<DecisionCallResult> CallAsync(DecisionCallRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue(new Call(request));
        if (Handler is { } handler)
        {
            return await handler(request, cancellationToken).ConfigureAwait(false);
        }

        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return DefaultResult(request);
    }
}

/// <summary>假模型解析：可控目标列表与 API Key。</summary>
public sealed class FakeModelResolver : IWorkflowModelResolver
{
    public List<WorkflowModelTarget> Targets { get; } = new()
    {
        new WorkflowModelTarget(
            "inst-1",
            "测试实例",
            "http://127.0.0.1:19001",
            true,
            null,
            new JsonObject { ["modelFile"] = "test.gguf" }),
    };

    public Task<IReadOnlyList<WorkflowModelTarget>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WorkflowModelTarget>>(Targets);

    public Task<WorkflowModelTarget?> ResolveAsync(string? instanceId, CancellationToken cancellationToken = default)
    {
        var target = string.IsNullOrWhiteSpace(instanceId)
            ? Targets.FirstOrDefault(t => t.IsUsable)
            : Targets.FirstOrDefault(t => t.InstanceId == instanceId);
        return Task.FromResult(target);
    }

    public Task<string?> GetApiKeyAsync(string instanceId, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);
}

/// <summary>内存工作流定义存储。</summary>
public sealed class InMemoryDefinitionStore : IWorkflowDefinitionStore
{
    private readonly Dictionary<string, WorkflowDefinition> _store = new(StringComparer.Ordinal);

    public void Add(WorkflowDefinition definition) => _store[definition.Id] = definition.Clone();

    public Task<IReadOnlyList<WorkflowDefinitionSummary>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WorkflowDefinitionSummary>>(_store.Values
            .Select(d => new WorkflowDefinitionSummary(
                d.Id,
                d.Name,
                d.Description,
                d.Nodes.Count,
                d.Edges.Count,
                d.UpdatedAt,
                0))
            .ToList());

    public Task<WorkflowLoadResult> LoadAsync(string workflowId, CancellationToken cancellationToken = default)
        => Task.FromResult(_store.TryGetValue(workflowId, out var definition)
            ? new WorkflowLoadResult(definition.Clone())
            : new WorkflowLoadResult(null, new[]
            {
                new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-IO-001", $"工作流 {workflowId} 不存在。"),
            }));

    public Task SaveAsync(
        WorkflowDefinition definition,
        string? snapshotLabel = null,
        string? snapshotNote = null,
        CancellationToken cancellationToken = default)
    {
        _store[definition.Id] = definition.Clone();
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string workflowId, CancellationToken cancellationToken = default)
        => Task.FromResult(_store.Remove(workflowId));

    public Task<IReadOnlyList<WorkflowVersionSnapshot>> ListSnapshotsAsync(
        string workflowId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WorkflowVersionSnapshot>>(Array.Empty<WorkflowVersionSnapshot>());

    public Task<WorkflowVersionSnapshot?> LoadSnapshotAsync(
        string workflowId,
        string snapshotId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<WorkflowVersionSnapshot?>(null);

    public Task<WorkflowLoadResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
        => Task.FromResult(new WorkflowLoadResult(null, new[]
        {
            new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-IO-001", "测试环境不支持从文件导入。"),
        }));

    public Task ExportAsync(
        WorkflowDefinition definition,
        string destinationPath,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>假交互通道：自动给出决定（默认批准）。</summary>
public sealed class FakeInteractionBroker : IWorkflowInteractionBroker
{
    public ConcurrentQueue<WorkflowUserPrompt> Prompts { get; } = new();

    public ManualDecisionKind Decision { get; set; } = ManualDecisionKind.Approved;

    public JsonObject Fields { get; } = new();

    public Task<WorkflowUserDecision> RequestAsync(
        WorkflowUserPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        Prompts.Enqueue(prompt);
        return Task.FromResult(new WorkflowUserDecision(Decision, Fields));
    }
}

/// <summary>记录运行事件的观察者。</summary>
public sealed class RecordingObserver : IWorkflowRunObserver
{
    private readonly object _sync = new();

    public List<(WorkflowRunState State, WorkflowPauseReason? Reason, string? Message)> RunStates { get; } = new();

    public List<NodeRunRecord> NodeUpdates { get; } = new();

    public List<WorkflowLogEntry> Logs { get; } = new();

    public List<WorkflowUserPrompt> Prompts { get; } = new();

    public void OnRunStateChanged(WorkflowRunState state, WorkflowPauseReason? pauseReason, string? message)
    {
        lock (_sync)
        {
            RunStates.Add((state, pauseReason, message));
        }
    }

    public void OnNodeStateChanged(NodeRunRecord record)
    {
        lock (_sync)
        {
            NodeUpdates.Add(record);
        }
    }

    public void OnLog(WorkflowLogEntry entry)
    {
        lock (_sync)
        {
            Logs.Add(entry);
        }
    }

    public void OnWaitingForUser(WorkflowUserPrompt prompt)
    {
        lock (_sync)
        {
            Prompts.Add(prompt);
        }
    }
}

/// <summary>测试工作流构造帮助。</summary>
public static class WorkflowBuilder
{
    public static WorkflowDefinition Create(string name = "测试工作流")
        => new() { Id = "wf-" + Guid.NewGuid().ToString("N")[..8], Name = name };

    public static WorkflowNode AddNode(
        this WorkflowDefinition definition,
        string typeId,
        string? id = null,
        string? name = null,
        double x = 0,
        double y = 0)
    {
        var nodeId = id ?? $"n{definition.Nodes.Count + 1}";
        var node = NodeTypeCatalog.CreateNode(typeId, nodeId, x, y, name);
        definition.Nodes.Add(node);
        return node;
    }

    public static WorkflowEdge Connect(
        this WorkflowDefinition definition,
        WorkflowNode source,
        WorkflowNode target,
        string? branchId = null,
        string sourcePort = NodeTypeCatalog.DefaultOutputPort,
        string targetPort = NodeTypeCatalog.DefaultInputPort)
    {
        var edge = new WorkflowEdge
        {
            Id = $"e{definition.Edges.Count + 1}",
            SourceNodeId = source.Id,
            SourcePort = sourcePort,
            TargetNodeId = target.Id,
            TargetPort = targetPort,
            BranchId = branchId,
        };
        definition.Edges.Add(edge);
        return edge;
    }

    /// <summary>设置节点配置（类型化对象 → JsonObject）。</summary>
    public static WorkflowNode WithConfig(this WorkflowNode node, object config)
    {
        node.Config = WorkflowJson.ToConfigObject(config);
        return node;
    }

    /// <summary>数据处理节点：Assign（表达式 → 目标字段）。</summary>
    public static WorkflowNode WithAssign(this WorkflowNode node, string target, string source)
        => node.WithConfig(new TransformNodeConfig
        {
            Operations =
            {
                new TransformOperation
                {
                    Id = "op1",
                    Kind = TransformOperationKind.Assign,
                    Target = target,
                    Source = source,
                },
            },
        });

    /// <summary>决策节点：单问题（choice）。</summary>
    public static WorkflowNode WithDecision(
        this WorkflowNode node,
        string questionId = "route",
        string stateTemplate = "{{ input.message }}",
        string? instanceId = null)
        => node.WithConfig(new DecisionNodeConfig
        {
            InstanceId = instanceId,
            StateTemplate = stateTemplate,
            Questions =
            {
                new DecisionQuestion
                {
                    Id = questionId,
                    Type = "choice",
                    Instructions = "Which team?",
                    Options =
                    {
                        new DecisionOption { Key = "billing" },
                        new DecisionOption { Key = "other" },
                    },
                },
            },
        });
}
