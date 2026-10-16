using System.Text.Json.Nodes;
using LlamaLauncher.Domain.Workflows;

namespace LlamaLauncher.Application.Workflows;

/// <summary>节点执行上下文（引擎组装；执行器只读取，不直接接触 UI 或引擎内部状态）。</summary>
public sealed class NodeExecutionContext
{
    /// <summary>节点定义。</summary>
    public required WorkflowNode Node { get; init; }

    /// <summary>运行 ID。</summary>
    public required string RunId { get; init; }

    /// <summary>测试模式。</summary>
    public required WorkflowTestMode TestMode { get; init; }

    /// <summary>按输入映射组装出的节点输入（目标字段 → 值）。</summary>
    public required JsonObject Inputs { get; init; }

    /// <summary>完整表达式作用域（input/vars/up/meta/error）。</summary>
    public required JsonObject Scope { get; init; }

    /// <summary>工作流输入根（input.*）。</summary>
    public required JsonObject WorkflowInputs { get; init; }

    /// <summary>运行变量快照（vars.*）。</summary>
    public required JsonObject Variables { get; init; }

    /// <summary>上游输出（up.*）。</summary>
    public required JsonObject Upstream { get; init; }

    /// <summary>运行元数据（meta.*）。</summary>
    public required JsonObject Meta { get; init; }

    /// <summary>错误上下文（error.*；仅在错误分支路径上非空）。</summary>
    public JsonObject? ErrorContext { get; init; }

    /// <summary>执行环境服务。</summary>
    public required WorkflowServices Services { get; init; }

    /// <summary>运行限制。</summary>
    public required WorkflowExecutionLimits Limits { get; init; }

    /// <summary>模拟模式响应（仅当运行处于模拟模式且配置了该节点时非空）。</summary>
    public WorkflowMockResponse? Mock { get; init; }

    /// <summary>当前尝试序号（1 起）。</summary>
    public required int Attempt { get; init; }

    /// <summary>写运行日志（level/message/exceptionText）。</summary>
    public required Action<WorkflowLogLevel, string, string?> Log { get; init; }

    /// <summary>取消令牌（运行取消 + 节点超时联动）。</summary>
    public required CancellationToken CancellationToken { get; init; }
}

/// <summary>节点执行器统一接口（与 UI 解耦；每种节点一个实现，可独立测试）。</summary>
public interface INodeExecutor
{
    /// <summary>节点类型 ID（与 <see cref="NodeTypeCatalog"/> 一致）。</summary>
    string TypeId { get; }

    /// <summary>设计时配置校验（返回错误/警告；空表示通过）。</summary>
    IReadOnlyList<WorkflowIssue> ValidateConfig(WorkflowNode node, WorkflowValidationContext context);

    /// <summary>执行节点。</summary>
    Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext context);
}

/// <summary>校验上下文（设计器校验与运行前校验共用）。</summary>
public sealed class WorkflowValidationContext
{
    /// <summary>被校验的定义。</summary>
    public WorkflowDefinition Definition { get; init; } = new();

    /// <summary>子工作流存在性检查（可空：为 null 时跳过存在性检查）。</summary>
    public Func<string, bool>? WorkflowExists { get; init; }

    /// <summary>子工作流加载（用于递归引用检测；可空时跳过）。</summary>
    public Func<string, WorkflowDefinition?>? WorkflowLoader { get; init; }

    /// <summary>可用模型目标（可空：为 null 时跳过实例绑定检查）。</summary>
    public IReadOnlyList<WorkflowModelTarget>? Models { get; init; }

    /// <summary>已注册算法键（可空：为 null 时跳过求解器检查）。</summary>
    public IReadOnlyList<string>? AlgorithmKeys { get; init; }

    /// <summary>是否用于运行前校验（更严格：例如要求模型可用）。</summary>
    public bool ForRun { get; init; }
}
