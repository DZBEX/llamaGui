using LlamaLauncher.Domain;
using LlamaLauncher.Domain.StateMachine;
using LlamaLauncher.Application.Abstractions;

namespace LlamaLauncher.Application.Instances;

/// <summary>运行时代理：持久化快照（ServerInstance）+ 进程会话与探测上下文。</summary>
public sealed class ManagedInstance
{
    public ManagedInstance(ServerInstance record) => Record = record;

    public ServerInstance Record { get; internal set; }

    public Guid Id => Record.Id;

    public string Name => Record.Name;

    public InstanceState State => Record.State;

    public string LocalBaseUrl => Record.LocalBaseUrl;

    public ServerCapabilities? Capabilities { get; internal set; }

    public LaunchProfile? Profile { get; internal set; }

    /// <summary>脱敏后的启动命令行（日志与 UI 展示）。</summary>
    public string? MaskedCommandLine { get; internal set; }

    public DateTimeOffset? ReadyUtc { get; internal set; }

    public HealthStatusKind Health { get; internal set; } = HealthStatusKind.Unknown;

    public string? HealthDetail { get; internal set; }

    /// <summary>启动失败阶段（ModelValidation / PortValidation / Probe / CommandLine / ProcessStart / Health）。</summary>
    public string? FailureStage { get; internal set; }

    internal IServerProcessSession? Session { get; set; }

    internal CancellationTokenSource? LifetimeCts { get; set; }

    internal bool StopRequested { get; set; }

    internal int StartupTimeoutSeconds { get; set; } = 600;

    internal int StopGraceSeconds { get; set; } = 5;

    internal DateTimeOffset? StartRequestedUtc { get; set; }

    internal TaskCompletionSource<bool>? StartupCompletion { get; set; }

    /// <summary>退出后的收尾任务（状态落库、历史更新）；停止流程会等待它完成。</summary>
    internal Task? ExitHandlingTask { get; set; }

    /// <summary>退出收尾完成的信号（在退出事件处理入口同步创建，避免竞态）。</summary>
    internal TaskCompletionSource<bool>? ExitCompletion { get; set; }

    public bool IsActive => InstanceStateMachine.IsActive(Record.State);
}

/// <summary>启动/重启结果。</summary>
public sealed record StartResult(
    bool Success,
    InstanceState FinalState,
    string? Message,
    IReadOnlyList<Domain.Validation.ValidationIssue> Issues)
{
    public static StartResult Ok(InstanceState state, string? message = null)
        => new(true, state, message, Array.Empty<Domain.Validation.ValidationIssue>());

    public static StartResult Fail(
        InstanceState state,
        string message,
        IReadOnlyList<Domain.Validation.ValidationIssue>? issues = null)
        => new(false, state, message, issues ?? Array.Empty<Domain.Validation.ValidationIssue>());
}

/// <summary>上次运行恢复报告。</summary>
public sealed record RecoveryReport(int MarkedStale, int Adopted, IReadOnlyList<string> Messages);

/// <summary>多实例生命周期管理。</summary>
public interface IInstanceManager
{
    /// <summary>实例集合发生变化（状态/健康/日志文件路径等）。</summary>
    event EventHandler<Guid>? InstanceChanged;

    /// <summary>加载持久化的实例并处理上次运行的残留状态（启动时调用一次）。</summary>
    Task<RecoveryReport> InitializeAsync(CancellationToken cancellationToken = default);

    IReadOnlyList<ManagedInstance> GetInstances();

    ManagedInstance? Get(Guid instanceId);

    /// <summary>创建实例（初始状态 Stopped）。</summary>
    Task<ManagedInstance> CreateAsync(string name, Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 启动实例；waitForReady 为 true 时等待进入 Ready 或失败/终止。
    /// lanAccessConfirmed 表示用户已对局域网监听完成当次风险确认（不持久化）。
    /// </summary>
    Task<StartResult> StartAsync(
        Guid instanceId,
        bool waitForReady = false,
        bool lanAccessConfirmed = false,
        CancellationToken cancellationToken = default);

    Task StopAsync(Guid instanceId, bool force = false, CancellationToken cancellationToken = default);

    Task<StartResult> RestartAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>移除实例记录（仅限已停止/失败状态；不删除历史）。</summary>
    Task RemoveAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>停止全部活动实例（应用退出流程）。</summary>
    Task StopAllAsync(bool force = false, CancellationToken cancellationToken = default);

    /// <summary>获取实例当前使用的 API Key 明文（仅用于本机 API 调用，不写日志）。</summary>
    Task<string?> GetInstanceApiKeyAsync(Guid instanceId, CancellationToken cancellationToken = default);
}
