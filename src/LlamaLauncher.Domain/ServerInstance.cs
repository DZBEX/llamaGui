namespace LlamaLauncher.Domain;

/// <summary>服务实例（持久化的最近状态快照 + 运行时标识）。</summary>
public sealed class ServerInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public Guid? ProfileId { get; set; }

    public string? ProfileSnapshotJson { get; set; }

    public Guid? ModelId { get; set; }

    public Guid? InstallationId { get; set; }

    public InstanceState State { get; set; } = InstanceState.Stopped;

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; }

    public int? ProcessId { get; set; }

    /// <summary>进程真实启动时间（UTC），用于 PID 重用校验。</summary>
    public DateTimeOffset? ProcessStartTimeUtc { get; set; }

    /// <summary>启动时使用的可执行文件路径快照，用于进程校验。</summary>
    public string? ExecutablePathSnapshot { get; set; }

    /// <summary>运行代次：每次启动自增，防止旧进程退出事件覆盖新状态。</summary>
    public long Generation { get; set; }

    public DateTimeOffset? StartedUtc { get; set; }

    public DateTimeOffset? StoppedUtc { get; set; }

    public int? ExitCode { get; set; }

    public string? LastError { get; set; }

    public string? LogFilePath { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    /// <summary>本机访问地址（0.0.0.0 绑定也通过 127.0.0.1 访问本机服务）。</summary>
    public string LocalBaseUrl =>
        $"http://{(Host is "0.0.0.0" or "::" or "[::]" ? "127.0.0.1" : Host)}:{Port}";
}
