namespace LlamaLauncher.Application.Abstractions;

/// <summary>日志来源。</summary>
public enum LogStreamKind
{
    StandardOutput = 0,
    StandardError = 1,
    Application = 2,
}

/// <summary>一行实例日志。</summary>
public sealed record InstanceLogLine(DateTimeOffset TimestampUtc, LogStreamKind Stream, string Text);

/// <summary>日志保留配置。</summary>
public sealed class LogRetentionOptions
{
    /// <summary>单个日志文件上限（字节），超出后轮转。</summary>
    public long MaxFileBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>保留的轮转文件数量（.1 … .N）。</summary>
    public int MaxRotatedFiles { get; set; } = 3;

    /// <summary>内存缓冲每个实例保留的行数（UI 虚拟化读取）。</summary>
    public int MemoryBufferLines { get; set; } = 5000;
}

/// <summary>每实例日志采集：内存环形缓冲 + 滚动文件 + 脱敏。</summary>
public interface IInstanceLogStore
{
    /// <summary>追加一行（内部执行脱敏与分批落盘；不抛异常到调用方）。</summary>
    void Append(Guid instanceId, string instanceName, LogStreamKind stream, string text);

    /// <summary>读取内存缓冲中的最近若干行。</summary>
    IReadOnlyList<InstanceLogLine> GetRecent(Guid instanceId, int maxLines = 500);

    /// <summary>该实例的日志文件路径（无论是否已创建）。</summary>
    string GetLogFilePath(Guid instanceId, string instanceName);

    /// <summary>更新已知秘密值（用于精确脱敏）。</summary>
    void SetKnownSecrets(IEnumerable<string> secrets);

    /// <summary>追加一个已知秘密值（例如当前实例使用的 API Key）。</summary>
    void AddKnownSecret(string secret);

    /// <summary>刷新并关闭全部文件（应用退出前调用）。</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>清理超过保留期限的日志文件（返回删除数量）。</summary>
    int CleanupOldFiles(TimeSpan retention);
}
