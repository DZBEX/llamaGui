using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.CommandLine;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.Probing;

/// <summary>
/// 短生命周期进程执行器：使用 ArgumentList 逐个传参（不经过 cmd/PowerShell），
/// 异步读取 stdout/stderr，超时后终止进程树。用于版本/帮助/设备等探测调用。
/// </summary>
public sealed class ExternalProcessRunner : IProbeProcessRunner
{
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ExternalProcessRunner> _logger;

    public ExternalProcessRunner(TimeProvider timeProvider, ILogger<ExternalProcessRunner> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<ProcessRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var started = _timeProvider.GetUtcNow();
        var stopwatch = Stopwatch.StartNew();
        var command = WindowsCommandLine.BuildPreview(executablePath, arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) is { Length: > 0 } dir
                ? dir
                : Environment.CurrentDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process = null;
        try
        {
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return ErrorResult(command, started, stopwatch.ElapsedMilliseconds, "进程无法启动。");
            }
        }
        catch (OperationCanceledException)
        {
            process?.Dispose();
            throw;
        }
        catch (Win32Exception ex)
        {
            process?.Dispose();
            return ErrorResult(
                command,
                started,
                stopwatch.ElapsedMilliseconds,
                $"无法启动进程：{ex.Message}（可能不是可执行文件、缺少权限或缺少依赖 DLL）。");
        }
        catch (Exception ex)
        {
            process?.Dispose();
            return ErrorResult(command, started, stopwatch.ElapsedMilliseconds, $"启动进程失败：{ex.Message}");
        }

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var timedOut = false;

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                TryKillTree(process);
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "等待被终止进程退出时出错：{Command}", command);
                }
            }
            catch (OperationCanceledException)
            {
                TryKillTree(process);
                throw;
            }

            var stdout = await SafeReadAsync(stdoutTask).ConfigureAwait(false);
            var stderr = await SafeReadAsync(stderrTask).ConfigureAwait(false);
            stopwatch.Stop();

            var result = new ProcessRunResult
            {
                Command = command,
                ExitCode = timedOut ? null : SafeExitCode(process),
                StandardOutput = stdout,
                StandardError = stderr,
                TimedOut = timedOut,
                DurationMs = stopwatch.ElapsedMilliseconds,
                StartedUtc = started,
                ErrorMessage = timedOut ? $"探测命令超时（{timeout.TotalSeconds:0} 秒）。" : null,
            };
            return result;
        }
    }

    private static int? SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "终止探测进程失败");
        }
    }

    private static ProcessRunResult ErrorResult(string command, DateTimeOffset started, long elapsedMs, string message)
        => new()
        {
            Command = command,
            ExitCode = null,
            TimedOut = false,
            DurationMs = elapsedMs,
            StartedUtc = started,
            ErrorMessage = message,
        };
}
