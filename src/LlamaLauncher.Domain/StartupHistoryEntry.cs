namespace LlamaLauncher.Domain;

/// <summary>一次实例启动的历史记录。</summary>
public sealed class StartupHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid InstanceId { get; set; }

    public string? InstanceNameSnapshot { get; set; }

    public Guid? ProfileId { get; set; }

    public string? ModelSnapshotJson { get; set; }

    public string? InstallationSnapshotJson { get; set; }

    /// <summary>脱敏后的命令行预览。</summary>
    public string? CommandLineMasked { get; set; }

    public int? ProcessId { get; set; }

    public DateTimeOffset StartedUtc { get; set; }

    public DateTimeOffset? EndedUtc { get; set; }

    public int? ExitCode { get; set; }

    public InstanceState FinalState { get; set; } = InstanceState.Starting;

    /// <summary>失败阶段（如 ModelValidation、HealthPolling）。</summary>
    public string? FailureStage { get; set; }

    public string? ErrorSummary { get; set; }

    public string? LogFilePath { get; set; }
}
