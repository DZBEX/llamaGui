using System.Diagnostics;
using LlamaLauncher.IntegrationTests.TestSupport;
using LlamaLauncher.Infrastructure.Probing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Probing;

public sealed class ExternalProcessRunnerTests
{
    internal static string ChildExecutablePath => Path.Combine(
        AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "LlamaLauncher.TestChildProcess.exe" : "LlamaLauncher.TestChildProcess");

    private static ExternalProcessRunner CreateRunner() =>
        new(new FakeTimeProvider(), NullLogger<ExternalProcessRunner>.Instance);

    [Fact]
    public async Task CapturesStdoutStderrAndExitCode()
    {
        var runner = CreateRunner();
        var result = await runner.RunAsync(
            ChildExecutablePath,
            new[] { "--echo-stdout", "来自标准输出", "--echo-stderr", "来自标准错误", "--exit-when-done", "--exit-code", "3" },
            TimeSpan.FromSeconds(30));

        Assert.False(result.TimedOut);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("来自标准输出", result.StandardOutput);
        Assert.Contains("来自标准错误", result.StandardError);
    }

    [Fact]
    public async Task LargeOutputOnBothStreams_DoesNotDeadlock()
    {
        var runner = CreateRunner();
        var result = await runner.RunAsync(
            ChildExecutablePath,
            new[] { "--stdout", "20000", "--stderr", "20000", "--interval-ms", "0", "--exit-when-done" },
            TimeSpan.FromSeconds(120));

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("OUT 019999", result.StandardOutput);
        Assert.Contains("ERR 019999", result.StandardError);
    }

    [Fact]
    public async Task Timeout_KillsProcessTreeAndReportsTimedOut()
    {
        var runner = CreateRunner();
        var result = await runner.RunAsync(
            ChildExecutablePath,
            new[] { "--hang" },
            TimeSpan.FromMilliseconds(800));

        Assert.True(result.TimedOut);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("超时", result.ErrorMessage);
    }

    [Fact]
    public async Task ExternalCancellation_KillsProcessAndThrows()
    {
        using var directory = new TempDirectory();
        var pidFile = directory.Combine("child.pid");
        var runner = CreateRunner();
        using var cts = new CancellationTokenSource();

        var task = runner.RunAsync(
            ChildExecutablePath,
            new[] { "--hang", "--write-pid", pidFile },
            TimeSpan.FromSeconds(60),
            cts.Token);

        // 等待子进程写出 PID 后再取消
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!File.Exists(pidFile) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(File.Exists(pidFile), "子进程未写出 PID。");
        var pid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        var exited = await WaitForProcessExitAsync(pid, TimeSpan.FromSeconds(10));
        Assert.True(exited, $"取消后子进程 {pid} 仍在运行。");
    }

    [Fact]
    public async Task MissingExecutable_ReturnsFriendlyError()
    {
        var runner = CreateRunner();
        var result = await runner.RunAsync(
            Path.Combine(Path.GetTempPath(), "不存在的程序-" + Guid.NewGuid().ToString("N") + ".exe"),
            Array.Empty<string>(),
            TimeSpan.FromSeconds(5));

        Assert.False(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.NotNull(result.ErrorMessage);
    }

    private static async Task<bool> WaitForProcessExitAsync(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }
}
