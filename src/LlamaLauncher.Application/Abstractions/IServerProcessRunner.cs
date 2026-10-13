namespace LlamaLauncher.Application.Abstractions;

/// <summary>启动 llama-server 进程的规格（argv 传递，不使用 shell）。</summary>
public sealed class ServerProcessStartSpec
{
    public required string ExecutablePath { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public string? WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

/// <summary>进程启动失败。</summary>
public sealed class ProcessStartException : Exception
{
    public ProcessStartException(string userMessage, Exception? inner = null)
        : base(userMessage, inner)
    {
    }
}

/// <summary>一个由启动器启动（或被安全接管）的服务进程会话。</summary>
public interface IServerProcessSession : IAsyncDisposable
{
    int ProcessId { get; }

    DateTimeOffset StartTimeUtc { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    /// <summary>标准输出行（已去除行尾）。</summary>
    event Action<string>? StandardOutputLine;

    /// <summary>标准错误行（已去除行尾）。</summary>
    event Action<string>? StandardErrorLine;

    /// <summary>进程退出（读取循环结束、流已排空后触发一次）。</summary>
    event Action<int?>? Exited;

    Task<int?> WaitForExitAsync(CancellationToken cancellationToken = default);

    /// <summary>终止进程树；进程已退出时静默返回。</summary>
    Task KillAsync(bool entireProcessTree = true);
}

/// <summary>llama-server 进程启动器。</summary>
public interface IServerProcessRunner
{
    /// <summary>启动进程并立即开始异步读取 stdout/stderr。</summary>
    IServerProcessSession Start(ServerProcessStartSpec spec);

    /// <summary>为已验证身份的既有进程创建监控会话（应用崩溃恢复场景）。</summary>
    IServerProcessSession Adopt(int processId);
}
