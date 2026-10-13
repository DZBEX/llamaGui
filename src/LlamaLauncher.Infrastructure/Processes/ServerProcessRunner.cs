using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using LlamaLauncher.Application.Abstractions;

namespace LlamaLauncher.Infrastructure.Processes;

/// <summary>启动/接管 llama-server 进程。</summary>
public sealed class ServerProcessRunner : IServerProcessRunner
{
    public IServerProcessSession Start(ServerProcessStartSpec spec)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDirectory is { Length: > 0 } dir
                ? dir
                : Path.GetDirectoryName(spec.ExecutablePath) ?? Environment.CurrentDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (spec.Environment is not null)
        {
            foreach (var (name, value) in spec.Environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        Process process;
        try
        {
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                throw new ProcessStartException("进程无法启动（Start 返回 false）。");
            }
        }
        catch (Win32Exception ex)
        {
            throw new ProcessStartException(
                $"无法启动进程：{ex.Message}（可能是缺少依赖 DLL、权限不足或文件不是有效的可执行程序）。",
                ex);
        }
        catch (ProcessStartException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProcessStartException($"启动进程失败：{ex.Message}", ex);
        }

        return new SpawnedServerProcess(process, adopted: false);
    }

    public IServerProcessSession Adopt(int processId)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException ex)
        {
            throw new ProcessStartException($"进程 {processId} 不存在。", ex);
        }

        return new SpawnedServerProcess(process, adopted: true);
    }
}

/// <summary>真实进程会话：异步行读取、退出事件与进程树终止。</summary>
public sealed class SpawnedServerProcess : IServerProcessSession
{
    private readonly Process _process;
    private readonly bool _adopted;
    private readonly TaskCompletionSource<int?> _exitTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Task? _stdoutPump;
    private readonly Task? _stderrPump;
    private readonly Task _exitWatcher;
    private int _disposed;

    internal SpawnedServerProcess(Process process, bool adopted)
    {
        _process = process;
        _adopted = adopted;
        ProcessId = SafeProcessId(process);
        StartTimeUtc = SafeStartTime(process) ?? DateTimeOffset.MinValue;

        if (!adopted)
        {
            _stdoutPump = PumpAsync(process.StandardOutput, line => StandardOutputLine?.Invoke(line));
            _stderrPump = PumpAsync(process.StandardError, line => StandardErrorLine?.Invoke(line));
        }

        _exitWatcher = WatchExitAsync();
    }

    public int ProcessId { get; }

    public DateTimeOffset StartTimeUtc { get; }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            try
            {
                return _process.HasExited ? _process.ExitCode : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public event Action<string>? StandardOutputLine;

    public event Action<string>? StandardErrorLine;

    public event Action<int?>? Exited;

    public Task<int?> WaitForExitAsync(CancellationToken cancellationToken = default)
        => _exitTcs.Task.WaitAsync(cancellationToken);

    public Task KillAsync(bool entireProcessTree = true)
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree);
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已退出
        }
        catch (Win32Exception)
        {
            // 无权限或已退出：交由退出监视器给出结果
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            await _exitWatcher.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 忽略：进程可能仍在运行（接管场景仅做监视）
        }

        try
        {
            _pumpCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _process.Dispose();
        }
        catch (Exception)
        {
        }

        _pumpCts.Dispose();
    }

    private async Task WatchExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 进程句柄失效时视为已退出
        }

        if (_stdoutPump is not null)
        {
            await SafeAwaitAsync(_stdoutPump).ConfigureAwait(false);
        }

        if (_stderrPump is not null)
        {
            await SafeAwaitAsync(_stderrPump).ConfigureAwait(false);
        }

        try
        {
            _pumpCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        var exitCode = ExitCode;
        _exitTcs.TrySetResult(exitCode);
        try
        {
            Exited?.Invoke(exitCode);
        }
        catch (Exception)
        {
            // 订阅方异常不得影响进程监视
        }
    }

    private async Task PumpAsync(StreamReader reader, Action<string> handler)
    {
        var token = _pumpCts.Token;
        try
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }

                if (line is null)
                {
                    break;
                }

                try
                {
                    handler(line);
                }
                catch (Exception)
                {
                    // 消费方（日志采集）异常不得中断读取循环
                }
            }
        }
        finally
        {
            // 确保退出监视器不会因读取循环提前结束而挂起
        }
    }

    private static async Task SafeAwaitAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static int SafeProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static DateTimeOffset? SafeStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch (Exception)
        {
            return null;
        }
    }
}
