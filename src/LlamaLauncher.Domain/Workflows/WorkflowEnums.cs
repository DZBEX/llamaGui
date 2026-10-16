namespace LlamaLauncher.Domain.Workflows;

/// <summary>工作流运行状态（统一状态机，禁止在 UI/引擎中用字符串判断状态）。</summary>
public enum WorkflowRunState
{
    /// <summary>已创建，尚未校验。</summary>
    Created = 0,

    /// <summary>正在校验工作流定义。</summary>
    Validating = 1,

    /// <summary>校验通过，可以运行。</summary>
    Ready = 2,

    /// <summary>正在运行。</summary>
    Running = 3,

    /// <summary>暂停请求已发出，等待当前节点结束。</summary>
    Pausing = 4,

    /// <summary>已暂停（节点边界处）。</summary>
    Paused = 5,

    /// <summary>暂停在人工确认节点，等待用户操作。</summary>
    WaitingForUser = 6,

    /// <summary>运行成功。</summary>
    Succeeded = 7,

    /// <summary>运行失败。</summary>
    Failed = 8,

    /// <summary>取消请求已发出，等待活动节点结束。</summary>
    Canceling = 9,

    /// <summary>已取消。</summary>
    Canceled = 10,

    /// <summary>超时终止。</summary>
    TimedOut = 11,
}

/// <summary>暂停原因（用于 UI 提示与继续行为）。</summary>
public enum WorkflowPauseReason
{
    /// <summary>用户主动暂停。</summary>
    UserRequest = 0,

    /// <summary>命中节点断点。</summary>
    Breakpoint = 1,

    /// <summary>单步执行：上一个节点完成后暂停。</summary>
    StepCompleted = 2,
}

/// <summary>节点运行状态。</summary>
public enum NodeRunState
{
    /// <summary>等待执行。</summary>
    Pending = 0,

    /// <summary>正在执行。</summary>
    Running = 1,

    /// <summary>执行成功。</summary>
    Succeeded = 2,

    /// <summary>执行失败。</summary>
    Failed = 3,

    /// <summary>已跳过（分支未选中或上游跳过）。</summary>
    Skipped = 4,

    /// <summary>已取消。</summary>
    Canceled = 5,

    /// <summary>在断点处暂停（尚未开始执行）。</summary>
    PausedAtBreakpoint = 6,

    /// <summary>等待人工确认。</summary>
    WaitingForUser = 7,

    /// <summary>执行超时。</summary>
    TimedOut = 8,
}

/// <summary>节点失败策略。</summary>
public enum NodeFailurePolicy
{
    /// <summary>终止整个工作流（默认）。</summary>
    Terminate = 0,

    /// <summary>允许继续执行后续节点（错误被记录）。</summary>
    Continue = 1,

    /// <summary>沿错误出口（error 端口）继续执行。</summary>
    ErrorBranch = 2,
}

/// <summary>重试条件。</summary>
public enum RetryCondition
{
    /// <summary>不重试（MaxAttempts 仅表示尝试一次）。</summary>
    Never = 0,

    /// <summary>仅超时类错误重试。</summary>
    OnTimeout = 1,

    /// <summary>仅瞬时错误（连接失败/超时/服务暂不可用）重试。</summary>
    OnTransient = 2,

    /// <summary>任何失败都重试。</summary>
    Always = 3,
}

/// <summary>工作流测试模式（必须与真实调用隔离）。</summary>
public enum WorkflowTestMode
{
    /// <summary>真实调用本机 llama-server。</summary>
    LocalModel = 0,

    /// <summary>模拟模式：使用配置的固定响应，默认不调用模型或外部系统。</summary>
    Simulated = 1,

    /// <summary>外部扩展模式（仅在对应扩展显式配置并启用时使用）。</summary>
    ExternalExtension = 2,
}

/// <summary>工作流变量/端口数据类型。</summary>
public enum WorkflowValueType
{
    /// <summary>任意类型。</summary>
    Any = 0,

    /// <summary>字符串。</summary>
    String = 1,

    /// <summary>数值。</summary>
    Number = 2,

    /// <summary>布尔。</summary>
    Boolean = 3,

    /// <summary>JSON 对象。</summary>
    Object = 4,

    /// <summary>JSON 数组。</summary>
    Array = 5,
}

/// <summary>校验问题严重级别。</summary>
public enum WorkflowIssueSeverity
{
    /// <summary>错误：阻止运行/保存。</summary>
    Error = 0,

    /// <summary>警告：允许运行但需要提示。</summary>
    Warning = 1,

    /// <summary>信息。</summary>
    Info = 2,
}

/// <summary>运行日志级别。</summary>
public enum WorkflowLogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4,
}

/// <summary>结束节点的结束状态。</summary>
public enum WorkflowEndStatus
{
    /// <summary>成功结束。</summary>
    Succeeded = 0,

    /// <summary>业务失败结束（流程正常执行但业务结果失败）。</summary>
    BusinessFailure = 1,
}

/// <summary>人工确认结果。</summary>
public enum ManualDecisionKind
{
    /// <summary>批准。</summary>
    Approved = 0,

    /// <summary>拒绝。</summary>
    Rejected = 1,
}

/// <summary>算法节点结果状态。</summary>
public enum AlgorithmStatus
{
    /// <summary>求解成功。</summary>
    Success = 0,

    /// <summary>无解。</summary>
    NoSolution = 1,

    /// <summary>输入无效。</summary>
    InvalidInput = 2,

    /// <summary>求解超时。</summary>
    Timeout = 3,

    /// <summary>已取消。</summary>
    Canceled = 4,

    /// <summary>求解器错误。</summary>
    Error = 5,
}

/// <summary>规则失败时的处理方式。</summary>
public enum RuleFailureAction
{
    /// <summary>硬错误：停止流程。</summary>
    Stop = 0,

    /// <summary>沿 fail 分支继续。</summary>
    Branch = 1,

    /// <summary>记录错误但继续执行 out 出口。</summary>
    Continue = 2,
}

/// <summary>并行节点的汇合策略。</summary>
public enum ParallelJoinMode
{
    /// <summary>等待全部完成；任一失败按该分支策略处理。</summary>
    AllComplete = 0,

    /// <summary>等待全部完成并收集所有错误（不中断其他分支）。</summary>
    CollectErrors = 1,

    /// <summary>任一失败立即请求取消其他分支。</summary>
    FailFast = 2,
}

/// <summary>循环节点模式。</summary>
public enum LoopMode
{
    /// <summary>对数组逐项调用子工作流。</summary>
    SubWorkflow = 0,

    /// <summary>对数组逐项调用决策模型节点配置。</summary>
    DecisionModel = 1,
}

/// <summary>异常处理节点动作。</summary>
public enum ExceptionAction
{
    /// <summary>记录错误后继续（沿 out 出口传递错误上下文）。</summary>
    Capture = 0,

    /// <summary>重新抛出（终止或按上游策略处理）。</summary>
    Rethrow = 1,
}
