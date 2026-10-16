using System.Text.Json.Nodes;
using LlamaLauncher.Domain.Workflows;

namespace LlamaLauncher.Application.Workflows;

/// <summary>节点错误分类（决定重试语义）。</summary>
public enum NodeErrorKind
{
    /// <summary>一般失败（不可重试，除非策略为 Always）。</summary>
    Failed = 0,

    /// <summary>瞬时错误（连接失败、服务暂不可用）。</summary>
    Transient = 1,

    /// <summary>超时。</summary>
    Timeout = 2,

    /// <summary>已取消。</summary>
    Canceled = 3,
}

/// <summary>节点执行错误。</summary>
public sealed record NodeExecutionError(
    NodeErrorKind Kind,
    string Message,
    string? ErrorType = null,
    JsonObject? Context = null);

/// <summary>节点执行结果。</summary>
public sealed class NodeExecutionResult
{
    private NodeExecutionResult()
    {
    }

    /// <summary>是否成功。</summary>
    public bool Success { get; private init; }

    /// <summary>输出端口数据（端口名 → 值）。</summary>
    public JsonObject Outputs { get; private init; } = new();

    /// <summary>成功时触发的分支集合（null = 普通节点，仅激活无分支标识的连线）。</summary>
    public IReadOnlyList<string>? FiredBranches { get; private init; }

    /// <summary>失败信息（Success=false 时非空）。</summary>
    public NodeExecutionError? Error { get; private init; }

    /// <summary>节点诊断信息（如模型请求耗时、Token 用量；用于运行记录）。</summary>
    public JsonObject? Diagnostics { get; private init; }

    /// <summary>业务失败标记（结束节点使用；工作流整体状态仍由引擎判定）。</summary>
    public WorkflowEndStatus? EndStatus { get; private init; }

    /// <summary>业务失败说明。</summary>
    public string? EndMessage { get; private init; }

    /// <summary>构造成功结果。</summary>
    public static NodeExecutionResult Ok(
        JsonObject? outputs = null,
        IReadOnlyList<string>? firedBranches = null,
        JsonObject? diagnostics = null,
        WorkflowEndStatus? endStatus = null,
        string? endMessage = null)
        => new()
        {
            Success = true,
            Outputs = outputs ?? new JsonObject(),
            FiredBranches = firedBranches,
            Diagnostics = diagnostics,
            EndStatus = endStatus,
            EndMessage = endMessage,
        };

    /// <summary>构造失败结果。</summary>
    public static NodeExecutionResult Fail(NodeExecutionError error, JsonObject? diagnostics = null)
        => new() { Success = false, Error = error, Diagnostics = diagnostics };
}

/// <summary>运行期限制（安全边界；由运行请求覆盖工作流默认值）。</summary>
public sealed class WorkflowExecutionLimits
{
    /// <summary>最大节点并发。</summary>
    public int MaxParallelism { get; init; } = 2;

    /// <summary>默认节点超时。</summary>
    public TimeSpan DefaultNodeTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>运行总时长上限。</summary>
    public TimeSpan MaxRunDuration { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>节点输入/输出快照上限（字符）。</summary>
    public int MaxSnapshotChars { get; init; } = WorkflowLimits.MaxSnapshotChars;

    /// <summary>模型响应大小上限（字节）。</summary>
    public int MaxResponseBytes { get; init; } = 1_048_576;

    /// <summary>子工作流最大嵌套深度。</summary>
    public int MaxSubWorkflowDepth { get; init; } = 5;
}

/// <summary>一次运行的请求。</summary>
public sealed class WorkflowRunRequest
{
    /// <summary>要运行的定义（引擎运行其副本，运行中修改不影响本次运行）。</summary>
    public required WorkflowDefinition Definition { get; init; }

    /// <summary>执行环境。</summary>
    public required WorkflowServices Services { get; init; }

    /// <summary>工作流输入。</summary>
    public JsonObject? Inputs { get; init; }

    /// <summary>测试模式。</summary>
    public WorkflowTestMode TestMode { get; init; } = WorkflowTestMode.LocalModel;

    /// <summary>模拟响应（节点 ID → 固定响应；模拟模式下未配置的模型节点会失败并说明）。</summary>
    public IReadOnlyDictionary<string, WorkflowMockResponse>? MockResponses { get; init; }

    /// <summary>默认模型实例 ID（决策节点未绑定时使用）。</summary>
    public string? DefaultInstanceId { get; init; }

    /// <summary>版本快照标签（写入运行记录）。</summary>
    public string? VersionLabel { get; init; }

    /// <summary>状态观察者。</summary>
    public IWorkflowRunObserver? Observer { get; init; }

    /// <summary>应用版本（写入运行记录）。</summary>
    public string AppVersion { get; init; } = string.Empty;

    /// <summary>是否以单步模式启动（在第一个节点前暂停）。</summary>
    public bool StartInStepMode { get; init; }

    /// <summary>限流与超时设置。</summary>
    public WorkflowExecutionLimits Limits { get; init; } = new();

    /// <summary>从指定节点启动（可空；需要提供快照，见 <see cref="StartSnapshotJson"/>）。</summary>
    public string? StartAtNodeId { get; init; }

    /// <summary>从中间节点启动的上下文快照（{"variables":{},"upstream":{}}）。</summary>
    public string? StartSnapshotJson { get; init; }

    /// <summary>已执行的节点（从中间启动时标记为已执行，跳过重跑）。</summary>
    public IReadOnlyList<string>? PreExecutedNodeIds { get; init; }
}

/// <summary>运行状态观察者（引擎→UI 单向通知；回调在后台线程执行，UI 实现负责调度到 UI 线程）。</summary>
public interface IWorkflowRunObserver
{
    /// <summary>运行状态变化。</summary>
    void OnRunStateChanged(WorkflowRunState state, WorkflowPauseReason? pauseReason, string? message);

    /// <summary>节点状态变化（给出最新记录快照）。</summary>
    void OnNodeStateChanged(NodeRunRecord record);

    /// <summary>日志。</summary>
    void OnLog(WorkflowLogEntry entry);

    /// <summary>等待人工确认（UI 可提示）。</summary>
    void OnWaitingForUser(WorkflowUserPrompt prompt);
}

/// <summary>空观察者（测试与无 UI 场景）。</summary>
public sealed class NullWorkflowRunObserver : IWorkflowRunObserver
{
    public static readonly NullWorkflowRunObserver Instance = new();

    public void OnRunStateChanged(WorkflowRunState state, WorkflowPauseReason? pauseReason, string? message)
    {
    }

    public void OnNodeStateChanged(NodeRunRecord record)
    {
    }

    public void OnLog(WorkflowLogEntry entry)
    {
    }

    public void OnWaitingForUser(WorkflowUserPrompt prompt)
    {
    }
}

/// <summary>运行结果（引擎返回；含完整运行记录）。</summary>
public sealed class WorkflowRunResult
{
    /// <summary>运行记录。</summary>
    public required WorkflowRunRecord Record { get; init; }

    /// <summary>最终状态（由引擎判定，不依赖节点界面状态）。</summary>
    public WorkflowRunState State => Record.State;

    /// <summary>输出。</summary>
    public JsonObject? Output { get; init; }
}

/// <summary>运行句柄：控制与结果。</summary>
public sealed class WorkflowRunHandle
{
    private readonly Action _pause;
    private readonly Action _resume;
    private readonly Action _step;
    private readonly Action _cancel;

    internal WorkflowRunHandle(
        string runId,
        Task<WorkflowRunResult> completion,
        Action pause,
        Action resume,
        Action step,
        Action cancel,
        Func<WorkflowRunState> stateAccessor,
        Func<WorkflowPauseReason?> pauseReasonAccessor)
    {
        RunId = runId;
        Completion = completion;
        _pause = pause;
        _resume = resume;
        _step = step;
        _cancel = cancel;
        StateAccessor = stateAccessor;
        PauseReasonAccessor = pauseReasonAccessor;
    }

    /// <summary>运行 ID。</summary>
    public string RunId { get; }

    /// <summary>完成任务。</summary>
    public Task<WorkflowRunResult> Completion { get; }

    /// <summary>当前状态。</summary>
    public WorkflowRunState State => StateAccessor();

    /// <summary>当前暂停原因（未暂停为 null）。</summary>
    public WorkflowPauseReason? PauseReason => PauseReasonAccessor();

    private Func<WorkflowRunState> StateAccessor { get; }

    private Func<WorkflowPauseReason?> PauseReasonAccessor { get; }

    /// <summary>请求暂停（在节点边界生效）。</summary>
    public void Pause() => _pause();

    /// <summary>继续执行。</summary>
    public void Resume() => _resume();

    /// <summary>单步：继续执行并在此节点后再次暂停。</summary>
    public void Step() => _step();

    /// <summary>取消运行（传播到所有活动节点）。</summary>
    public void Cancel() => _cancel();
}
