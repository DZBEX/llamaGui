using System.Text.Json.Nodes;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Domain.Workflows;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Application.Workflows;

/// <summary>启动一次运行的参数（由 UI / 批量测试组装）。</summary>
public sealed class WorkflowLaunchOptions
{
    /// <summary>要运行的定义。</summary>
    public required WorkflowDefinition Definition { get; init; }

    /// <summary>工作流输入。</summary>
    public JsonObject? Inputs { get; init; }

    /// <summary>测试模式（默认取定义默认值）。</summary>
    public WorkflowTestMode? TestMode { get; init; }

    /// <summary>模拟响应。</summary>
    public IReadOnlyDictionary<string, WorkflowMockResponse>? MockResponses { get; init; }

    /// <summary>默认模型实例。</summary>
    public string? DefaultInstanceId { get; init; }

    /// <summary>版本快照标签。</summary>
    public string? VersionLabel { get; init; }

    /// <summary>观察者。</summary>
    public IWorkflowRunObserver? Observer { get; init; }

    /// <summary>以单步模式启动。</summary>
    public bool StartInStepMode { get; init; }

    /// <summary>从指定节点启动（可选）。</summary>
    public string? StartAtNodeId { get; init; }

    /// <summary>起始快照 JSON（{"variables":{},"upstream":{}}）。</summary>
    public string? StartSnapshotJson { get; init; }

    /// <summary>预执行节点（中间启动时跳过）。</summary>
    public IReadOnlyList<string>? PreExecutedNodeIds { get; init; }

    /// <summary>是否把运行记录写入历史（默认 true）。</summary>
    public bool PersistRunRecord { get; init; } = true;
}

/// <summary>
/// 运行协调：组装服务、启动引擎、落运行历史；对 UI 与批量测试提供统一入口。
/// 引擎与 UI 解耦：本类不引用 WPF。
/// </summary>
public sealed class WorkflowRunnerService
{
    private readonly IWorkflowDefinitionStore _definitions;
    private readonly IWorkflowRunStore _runs;
    private readonly IModelDecisionClient _decisionClient;
    private readonly IWorkflowModelResolver _models;
    private readonly IWorkflowAlgorithmRegistry _algorithms;
    private readonly WorkflowEngine _engine;
    private readonly ILogger<WorkflowRunnerService> _logger;

    public WorkflowRunnerService(
        IWorkflowDefinitionStore definitions,
        IWorkflowRunStore runs,
        IModelDecisionClient decisionClient,
        IWorkflowModelResolver models,
        IWorkflowAlgorithmRegistry algorithms,
        WorkflowEngine engine,
        ILogger<WorkflowRunnerService> logger)
    {
        _definitions = definitions;
        _runs = runs;
        _decisionClient = decisionClient;
        _models = models;
        _algorithms = algorithms;
        _engine = engine;
        _logger = logger;
    }

    /// <summary>共享校验器。</summary>
    public WorkflowValidator Validator => _engine.Validator;

    /// <summary>算法注册表。</summary>
    public IWorkflowAlgorithmRegistry Algorithms => _algorithms;

    /// <summary>可用模型目标列表。</summary>
    public Task<IReadOnlyList<WorkflowModelTarget>> GetModelTargetsAsync(CancellationToken cancellationToken = default)
        => _models.ListAsync(cancellationToken);

    /// <summary>构建带外部上下文的校验（子流程存在性、循环引用、模型与算法可用性）。</summary>
    public async Task<WorkflowValidationReport> ValidateAsync(
        WorkflowDefinition definition,
        bool forRun,
        CancellationToken cancellationToken = default)
    {
        var summaries = await _definitions.ListAsync(cancellationToken).ConfigureAwait(false);
        var loaded = new Dictionary<string, WorkflowDefinition>(StringComparer.Ordinal);
        foreach (var summary in summaries)
        {
            var result = await _definitions.LoadAsync(summary.WorkflowId, cancellationToken).ConfigureAwait(false);
            if (result.Definition is not null)
            {
                loaded[summary.WorkflowId] = result.Definition;
            }
        }

        IReadOnlyList<WorkflowModelTarget>? models = null;
        try
        {
            models = await _models.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取模型服务列表失败，将跳过实例绑定校验。");
        }

        var context = new WorkflowValidationContext
        {
            Definition = definition,
            WorkflowExists = id => string.Equals(id, definition.Id, StringComparison.Ordinal) || loaded.ContainsKey(id),
            WorkflowLoader = id => loaded.GetValueOrDefault(id),
            Models = models,
            AlgorithmKeys = _algorithms.List().Select(a => a.Key).ToList(),
            ForRun = forRun,
        };
        return _engine.Validator.Validate(definition, context);
    }

    /// <summary>启动运行（可选写入历史）。</summary>
    public WorkflowRunHandle Start(WorkflowLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var limits = new WorkflowExecutionLimits
        {
            MaxParallelism = Math.Clamp(options.Definition.Defaults.MaxNodeParallelism, 1, 8),
            DefaultNodeTimeout = TimeSpan.FromSeconds(Math.Clamp(options.Definition.Defaults.DefaultNodeTimeoutSeconds, 1, 86_400)),
            MaxRunDuration = TimeSpan.FromSeconds(Math.Clamp(options.Definition.Defaults.MaxRunSeconds, 1, 86_400)),
            MaxSnapshotChars = Math.Clamp(options.Definition.Defaults.MaxNodeSnapshotChars, 1_024, 8 * 1024 * 1024),
            MaxResponseBytes = Math.Clamp(options.Definition.Defaults.MaxResponseBytes, 1_024, 64 * 1024 * 1024),
        };

        var services = new WorkflowServices
        {
            DecisionClient = _decisionClient,
            ModelResolver = _models,
            DefinitionStore = _definitions,
            Algorithms = _algorithms,
            Interactions = _interactionBroker,
            ChildRunner = null,
        };

        var request = new WorkflowRunRequest
        {
            Definition = options.Definition,
            Services = services,
            Inputs = options.Inputs,
            TestMode = options.TestMode ?? options.Definition.Defaults.DefaultTestMode,
            MockResponses = options.MockResponses,
            DefaultInstanceId = options.DefaultInstanceId,
            VersionLabel = options.VersionLabel,
            Observer = options.Observer ?? NullWorkflowRunObserver.Instance,
            AppVersion = ResolveAppVersion(),
            StartInStepMode = options.StartInStepMode,
            StartAtNodeId = options.StartAtNodeId,
            StartSnapshotJson = options.StartSnapshotJson,
            PreExecutedNodeIds = options.PreExecutedNodeIds,
            Limits = limits,
        };

        var handle = _engine.Start(request);
        if (options.PersistRunRecord)
        {
            _ = PersistAsync(handle);
        }

        return handle;
    }

    /// <summary>运行并等待结果。</summary>
    public async Task<WorkflowRunResult> RunAsync(WorkflowLaunchOptions options, CancellationToken cancellationToken = default)
    {
        var handle = Start(options);
        return await handle.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>人工确认交互通道（UI 注入）。</summary>
    public IWorkflowInteractionBroker? InteractionBroker
    {
        get => _interactionBroker;
        set => _interactionBroker = value;
    }

    private IWorkflowInteractionBroker? _interactionBroker;

    private async Task PersistAsync(WorkflowRunHandle handle)
    {
        try
        {
            var result = await handle.Completion.ConfigureAwait(false);
            await _runs.AppendAsync(result.Record).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入运行历史失败（RunId={RunId}）。", handle.RunId);
        }
    }

    private static string ResolveAppVersion()
    {
        var version = typeof(WorkflowRunnerService).Assembly.GetName().Version;
        return version is null ? "unknown" : version.ToString(3);
    }
}
