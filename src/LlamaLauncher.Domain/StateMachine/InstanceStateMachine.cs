namespace LlamaLauncher.Domain.StateMachine;

using LlamaLauncher.Domain;

/// <summary>非法的实例状态迁移。</summary>
public sealed class InvalidStateTransitionException : InvalidOperationException
{
    public InvalidStateTransitionException(InstanceState from, InstanceState to)
        : base($"不允许的实例状态迁移：{from} -> {to}。")
    {
        From = from;
        To = to;
    }

    public InstanceState From { get; }

    public InstanceState To { get; }
}

/// <summary>实例状态机：集中管理合法迁移，禁止各层自行拼装状态。</summary>
public static class InstanceStateMachine
{
    private static readonly HashSet<(InstanceState From, InstanceState To)> AllowedTransitions = new()
    {
        (InstanceState.Unknown, InstanceState.Stopped),
        (InstanceState.Unknown, InstanceState.Failed),
        (InstanceState.Stopped, InstanceState.Starting),
        (InstanceState.Stopped, InstanceState.Failed),
        (InstanceState.Starting, InstanceState.LoadingModel),
        (InstanceState.Starting, InstanceState.Ready),
        (InstanceState.Starting, InstanceState.Failed),
        (InstanceState.Starting, InstanceState.Stopping),
        (InstanceState.Starting, InstanceState.ExitedUnexpectedly),
        (InstanceState.LoadingModel, InstanceState.Ready),
        (InstanceState.LoadingModel, InstanceState.Failed),
        (InstanceState.LoadingModel, InstanceState.Stopping),
        (InstanceState.LoadingModel, InstanceState.ExitedUnexpectedly),
        (InstanceState.Ready, InstanceState.Stopping),
        (InstanceState.Ready, InstanceState.Failed),
        (InstanceState.Ready, InstanceState.ExitedUnexpectedly),
        // 应用重启后接管仍运行的实例：对已就绪实例重新进入“加载/校验”，由健康检查确认最终状态。
        (InstanceState.Ready, InstanceState.LoadingModel),
        (InstanceState.Stopping, InstanceState.Stopped),
        (InstanceState.Stopping, InstanceState.Failed),
        (InstanceState.Stopping, InstanceState.ExitedUnexpectedly),
        (InstanceState.Failed, InstanceState.Starting),
        (InstanceState.Failed, InstanceState.Stopped),
        (InstanceState.ExitedUnexpectedly, InstanceState.Starting),
        (InstanceState.ExitedUnexpectedly, InstanceState.Stopped),
    };

    /// <summary>判断迁移是否合法（同状态视为幂等，允许）。</summary>
    public static bool CanTransition(InstanceState from, InstanceState to)
        => from == to || AllowedTransitions.Contains((from, to));

    /// <summary>校验迁移，非法时抛出 <see cref="InvalidStateTransitionException"/>。</summary>
    public static void EnsureTransition(InstanceState from, InstanceState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidStateTransitionException(from, to);
        }
    }

    /// <summary>状态是否为终止态（无运行中的进程）。</summary>
    public static bool IsTerminal(InstanceState state) =>
        state is InstanceState.Stopped or InstanceState.Failed or InstanceState.ExitedUnexpectedly;

    /// <summary>是否处于（进程应存在或正在启动）的活跃状态。</summary>
    public static bool IsActive(InstanceState state) =>
        state is InstanceState.Starting or InstanceState.LoadingModel or InstanceState.Ready or InstanceState.Stopping;
}
