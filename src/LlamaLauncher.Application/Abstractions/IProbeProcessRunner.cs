namespace LlamaLauncher.Application.Abstractions;

/// <summary>一次外部进程执行的原始结果。</summary>
public sealed class ProcessRunResult
{
    public string Command { get; init; } = string.Empty;

    public int? ExitCode { get; init; }

    public string StandardOutput { get; init; } = string.Empty;

    public string StandardError { get; init; } = string.Empty;

    public bool TimedOut { get; init; }

    public long DurationMs { get; init; }

    public string? ErrorMessage { get; init; }

    public DateTimeOffset StartedUtc { get; init; }

    public bool Succeeded => !TimedOut && ErrorMessage is null && ExitCode == 0;
}

/// <summary>短生命周期进程执行器（版本/帮助/设备探测等），带独立超时与取消。</summary>
public interface IProbeProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
