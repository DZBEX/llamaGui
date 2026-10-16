using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Domain.Workflows;
using System.Text.Json.Nodes;

namespace LlamaLauncher.Application.Workflows;

/// <summary>可用于运行时选择的本地模型服务目标（非敏感信息）。</summary>
public sealed record WorkflowModelTarget(
    string InstanceId,
    string InstanceName,
    string BaseUrl,
    bool IsReady,
    string? UnavailableReason,
    JsonObject ContextSnapshot)
{
    /// <summary>是否可用于真实调用。</summary>
    public bool IsUsable => IsReady && string.IsNullOrEmpty(UnavailableReason);

    /// <summary>显示名称（含状态提示）。</summary>
    public string DisplayName => IsUsable
        ? $"{InstanceName}（{BaseUrl}）"
        : $"{InstanceName}（不可用：{UnavailableReason ?? "未就绪"}）";
}

/// <summary>模型服务解析：把决策节点绑定的实例 ID 解析为可调用目标（复用启动器实例管理）。</summary>
public interface IWorkflowModelResolver
{
    /// <summary>列出全部实例目标（含未就绪，便于 UI 展示原因）。</summary>
    Task<IReadOnlyList<WorkflowModelTarget>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>解析目标：<paramref name="instanceId"/> 为空时返回第一个就绪实例；未找到返回 null。</summary>
    Task<WorkflowModelTarget?> ResolveAsync(string? instanceId, CancellationToken cancellationToken = default);

    /// <summary>获取实例 API Key 明文（仅本机调用用，不写日志）。</summary>
    Task<string?> GetApiKeyAsync(string instanceId, CancellationToken cancellationToken = default);
}

/// <summary>算法调用结果状态与数据。</summary>
public sealed record AlgorithmInvocationResult(
    AlgorithmStatus Status,
    JsonNode? Result,
    string? Message,
    TimeSpan Elapsed);

/// <summary>可注册的算法/求解器实现（扩展点；不在核心引擎内依赖具体 OR 库）。</summary>
public interface IWorkflowAlgorithm
{
    /// <summary>稳定键名。</summary>
    string Key { get; }

    /// <summary>显示名称。</summary>
    string DisplayName { get; }

    /// <summary>说明（含“示例”标记，避免被误当作生产求解器）。</summary>
    string Description { get; }

    /// <summary>执行算法（异步、可取消、有超时控制）。</summary>
    Task<AlgorithmInvocationResult> InvokeAsync(JsonNode? input, CancellationToken cancellationToken = default);
}

/// <summary>算法注册表。</summary>
public interface IWorkflowAlgorithmRegistry
{
    /// <summary>列出已注册算法。</summary>
    IReadOnlyList<IWorkflowAlgorithm> List();

    /// <summary>按键查找。</summary>
    IWorkflowAlgorithm? Find(string? key);
}

/// <summary>人工确认会话（由 UI 实现；引擎不引用 WPF）。</summary>
public sealed record WorkflowUserPrompt(
    string RunId,
    string NodeId,
    string Title,
    string Message,
    IReadOnlyList<ManualField> Fields,
    string ApproveLabel,
    string RejectLabel);

/// <summary>人工决定。</summary>
public sealed record WorkflowUserDecision(ManualDecisionKind Kind, JsonObject Fields)
{
    /// <summary>批准。</summary>
    public static WorkflowUserDecision Approved(JsonObject? fields = null)
        => new(ManualDecisionKind.Approved, fields ?? new JsonObject());

    /// <summary>拒绝。</summary>
    public static WorkflowUserDecision Rejected(JsonObject? fields = null)
        => new(ManualDecisionKind.Rejected, fields ?? new JsonObject());
}

/// <summary>人工确认交互通道；未提供实现时人工确认节点会失败并说明原因。</summary>
public interface IWorkflowInteractionBroker
{
    /// <summary>请求用户决定（可取消；取消时抛出 OperationCanceledException）。</summary>
    Task<WorkflowUserDecision> RequestAsync(WorkflowUserPrompt prompt, CancellationToken cancellationToken = default);
}

/// <summary>默认交互通道：明确失败而不是假装成功（用于批量测试等无 UI 场景）。</summary>
public sealed class UnavailableInteractionBroker : IWorkflowInteractionBroker
{
    public Task<WorkflowUserDecision> RequestAsync(
        WorkflowUserPrompt prompt,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "工作流包含人工确认节点，但当前运行环境未配置交互通道（请在实验室界面中运行，或移除人工确认节点）。");
}

/// <summary>子工作流执行器接口（由 <see cref="WorkflowEngine"/> 自身实现并注入）。</summary>
public interface IWorkflowChildRunner
{
    /// <summary>执行子工作流（共享取消；带递归深度保护）。</summary>
    Task<WorkflowRunResult> RunChildAsync(
        WorkflowDefinition definition,
        JsonObject inputs,
        WorkflowTestMode testMode,
        IReadOnlyDictionary<string, WorkflowMockResponse>? mockResponses,
        string? defaultInstanceId,
        int timeoutSeconds,
        CancellationToken cancellationToken);
}

/// <summary>执行环境：节点执行器需要的全部外部服务（由调用方组装，便于测试替换）。</summary>
public sealed class WorkflowServices
{
    /// <summary>决策模型客户端。</summary>
    public required IModelDecisionClient DecisionClient { get; init; }

    /// <summary>模型服务解析。</summary>
    public required IWorkflowModelResolver ModelResolver { get; init; }

    /// <summary>工作流定义存储（子工作流加载）。</summary>
    public required IWorkflowDefinitionStore DefinitionStore { get; init; }

    /// <summary>算法注册表。</summary>
    public required IWorkflowAlgorithmRegistry Algorithms { get; init; }

    /// <summary>人工确认通道（可空 → 使用不可用实现）。</summary>
    public IWorkflowInteractionBroker? Interactions { get; init; }

    /// <summary>子工作流执行器（运行开始时由引擎注入）。</summary>
    public IWorkflowChildRunner? ChildRunner { get; set; }
}
