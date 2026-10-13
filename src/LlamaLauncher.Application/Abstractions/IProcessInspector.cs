namespace LlamaLauncher.Application.Abstractions;

/// <summary>进程身份信息（用于 PID 重用校验与安全接管）。</summary>
public sealed record ProcessIdentity(int Pid, DateTimeOffset? StartTimeUtc, string? ExecutablePath, bool Exists);

/// <summary>进程检查：读取 PID 对应进程的启动时间与可执行文件路径。</summary>
public interface IProcessInspector
{
    ProcessIdentity Inspect(int pid);

    /// <summary>校验进程是否为指定可执行文件启动的实例（PID + 启动时间 + 路径三重验证）。</summary>
    bool Matches(
        int pid,
        string executablePath,
        DateTimeOffset expectedStartTimeUtc,
        TimeSpan startTimeTolerance,
        out ProcessIdentity identity);
}
