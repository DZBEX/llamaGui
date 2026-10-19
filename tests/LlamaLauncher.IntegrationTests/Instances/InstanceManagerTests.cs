using System.Diagnostics;
using LlamaLauncher.Domain;
using LlamaLauncher.IntegrationTests.TestSupport;
using LlamaLauncher.Infrastructure.Logging;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Instances;

public sealed class InstanceManagerTests
{
    private static async Task<LaunchProfile> CreateProfileAsync(
        IntegrationFixture fixture,
        int port,
        Guid installationId,
        string modelPath,
        Action<ServerLaunchOptions>? configure = null)
    {
        var now = fixture.TimeProvider.GetUtcNow();
        var profile = new LaunchProfile
        {
            Name = "测试配置-" + Guid.NewGuid().ToString("N")[..6],
            InstallationId = installationId,
            Options = new ServerLaunchOptions
            {
                ModelPath = modelPath,
                Host = "127.0.0.1",
                Port = port,
                ContextSize = 512,
                StartupTimeoutSeconds = 30,
                StopGraceSeconds = 1,
            },
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        configure?.Invoke(profile.Options);
        await fixture.Profiles.UpsertAsync(profile);
        return profile;
    }

    private static void WithEnvironmentVariable(string name, string? value, Action action)
    {
        var original = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, original);
        }
    }

    [Fact]
    public async Task StartBecomesReadyThenStopsCleanly()
    {
        using var fixture = new IntegrationFixture();
        var modelPath = fixture.CreateModelFile();
        var installation = await fixture.InstallationService.RegisterExternalAsync(IntegrationFixture.ChildExecutablePath);
        var port = IntegrationFixture.GetFreePort();
        var profile = await CreateProfileAsync(fixture, port, installation.Id, modelPath);
        var instance = await fixture.Manager.CreateAsync("实例 A", profile.Id);

        try
        {
            fixture.SetEnvironment("LLAMALAUNCHER_TEST_ENABLE_HTTP", "1");
            fixture.SetEnvironment("LLAMALAUNCHER_TEST_HEALTH_READY_AFTER_MS", "300");

            var result = await fixture.Manager.StartAsync(instance.Id, waitForReady: true);

            Assert.True(result.Success, result.Message);
            Assert.Equal(InstanceState.Ready, instance.State);
            Assert.Equal(HealthStatusKind.Ready, instance.Health);
            Assert.NotNull(instance.Record.ProcessId);
            Assert.True(instance.Record.ProcessId > 0);
            Assert.NotNull(instance.MaskedCommandLine);
            Assert.Contains("--port", instance.MaskedCommandLine);

            var logs = fixture.LogStore.GetRecent(instance.Id, 200);
            Assert.Contains(logs, l => l.Text.Contains("启动命令（脱敏）"));
            Assert.Contains(logs, l => l.Text.Contains("服务已就绪"));

            var history = await fixture.History.QueryAsync(new() { InstanceId = instance.Id });
            var entry = Assert.Single(history);
            Assert.NotNull(entry.CommandLineMasked);
            Assert.Equal(instance.Record.ProcessId, entry.ProcessId);

            var pid = instance.Record.ProcessId!.Value;
            await fixture.Manager.StopAsync(instance.Id);

            Assert.True(
                await WaitForStateAsync(instance, InstanceState.Stopped, TimeSpan.FromSeconds(10)),
                $"停止后实例状态为 {instance.State}。");
            Assert.NotNull(instance.Record.StoppedUtc);

            var exited = await WaitForProcessExitAsync(pid, TimeSpan.FromSeconds(15));
            Assert.True(exited, $"停止后进程 {pid} 仍在运行。");

            var historyAfter = await fixture.History.QueryAsync(new() { InstanceId = instance.Id });
            Assert.True(
                historyAfter[0].FinalState == InstanceState.Stopped,
                $"历史最终状态应为 Stopped，实际 {historyAfter[0].FinalState}；" +
                $"EndedUtc={historyAfter[0].EndedUtc}，ExitCode={historyAfter[0].ExitCode}，" +
                $"Error={historyAfter[0].ErrorSummary}，Stage={historyAfter[0].FailureStage}；" +
                $"管理日志：{string.Join(" / ", fixture.ManagerLogger.Messages.TakeLast(12))}");
            Assert.NotNull(historyAfter[0].EndedUtc);
        }
        finally
        {
            fixture.SetEnvironment("LLAMALAUNCHER_TEST_ENABLE_HTTP", null);
            fixture.SetEnvironment("LLAMALAUNCHER_TEST_HEALTH_READY_AFTER_MS", null);
        }
    }

    [Fact]
    public async Task PortConflict_FailsBeforeStartWithClearMessage()
    {
        using var fixture = new IntegrationFixture();
        var modelPath = fixture.CreateModelFile();
        var installation = await fixture.InstallationService.RegisterExternalAsync(IntegrationFixture.ChildExecutablePath);
        var port = IntegrationFixture.GetFreePort();
        var profile = await CreateProfileAsync(fixture, port, installation.Id, modelPath);
        var instance = await fixture.Manager.CreateAsync("端口冲突实例", profile.Id);

        using var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        blocker.Start();

        var result = await fixture.Manager.StartAsync(instance.Id, waitForReady: true);

        Assert.False(result.Success);
        Assert.Equal(InstanceState.Failed, instance.State);
        Assert.Contains("端口", result.Message);
        Assert.Null(instance.Record.ProcessId);

        var history = await fixture.History.QueryAsync(new() { InstanceId = instance.Id });
        Assert.Equal(InstanceState.Failed, history[0].FinalState);
        Assert.Equal("Validation", history[0].FailureStage);
    }

    [Fact]
    public async Task TwoInstances_StartAndStopIndependently()
    {
        using var fixture = new IntegrationFixture();
        var modelPath = fixture.CreateModelFile();
        var installation = await fixture.InstallationService.RegisterExternalAsync(IntegrationFixture.ChildExecutablePath);
        var profileA = await CreateProfileAsync(fixture, IntegrationFixture.GetFreePort(), installation.Id, modelPath);
        var profileB = await CreateProfileAsync(fixture, IntegrationFixture.GetFreePort(), installation.Id, modelPath);
        var instanceA = await fixture.Manager.CreateAsync("实例 A", profileA.Id);
        var instanceB = await fixture.Manager.CreateAsync("实例 B", profileB.Id);

        try
        {
            fixture.SetEnvironment("LLAMALAUNCHER_TEST_ENABLE_HTTP", "1");

            var resultA = await fixture.Manager.StartAsync(instanceA.Id, waitForReady: true);
            var resultB = await fixture.Manager.StartAsync(instanceB.Id, waitForReady: true);
            Assert.True(resultA.Success, resultA.Message);
            Assert.True(resultB.Success, resultB.Message);
            Assert.Equal(InstanceState.Ready, instanceA.State);
            Assert.Equal(InstanceState.Ready, instanceB.State);
            Assert.NotEqual(instanceA.Record.ProcessId, instanceB.Record.ProcessId);

            await fixture.Manager.StopAsync(instanceA.Id);

            Assert.True(
                await WaitForStateAsync(instanceA, InstanceState.Stopped, TimeSpan.FromSeconds(10)),
                $"实例 A 停止后状态为 {instanceA.State}。");
            Assert.Equal(InstanceState.Ready, instanceB.State);
            Assert.NotNull(instanceB.Record.ProcessId);
        }
        finally
        {
            fixture.SetEnvironment("LLAMALAUNCHER_TEST_ENABLE_HTTP", null);
        }
    }

    [Fact]
    public async Task Start_FallsBackToAvailableInstallation_WhenProfilePathIsFromAnotherComputer()
    {
        using var fixture = new IntegrationFixture();
        var modelPath = fixture.CreateModelFile();
        var available = await fixture.InstallationService.RegisterExternalAsync(
            IntegrationFixture.ChildExecutablePath);

        var now = fixture.TimeProvider.GetUtcNow();
        var stale = new LlamaCppInstallation
        {
            Id = Guid.NewGuid(),
            VersionLabel = "b11703",
            Directory = @"D:\OtherComputer\llama.cpp\b11703",
            ExecutablePath = @"D:\OtherComputer\llama.cpp\b11703\llama-server.exe",
            SourceKind = InstallationSourceKind.ExternalPath,
            Status = InstallationStatus.Missing,
            InstalledUtc = now,
        };
        await fixture.InstallationsRepo.UpsertAsync(stale);
        var profile = await CreateProfileAsync(
            fixture,
            IntegrationFixture.GetFreePort(),
            stale.Id,
            modelPath);
        var instance = await fixture.Manager.CreateAsync("跨电脑实例", profile.Id);

        try
        {
            fixture.SetEnvironment("LLAMALAUNCHER_TEST_ENABLE_HTTP", "1");

            var result = await fixture.Manager.StartAsync(instance.Id, waitForReady: true);

            Assert.True(result.Success, result.Message);
            Assert.Equal(InstanceState.Ready, instance.State);
            var repaired = await fixture.Profiles.GetByIdAsync(profile.Id);
            Assert.Equal(available.Id, repaired!.InstallationId);
            Assert.Equal(available.ExecutablePath, repaired.InstallationSnapshot?.ExecutablePath);
            Assert.Contains(
                fixture.LogStore.GetRecent(instance.Id, 200),
                line => line.Text.Contains("原 llama.cpp 程序路径不可用"));
        }
        finally
        {
            fixture.SetEnvironment("LLAMALAUNCHER_TEST_ENABLE_HTTP", null);
        }
    }

    [Fact]
    public async Task UnexpectedExit_IsDetectedAndRecorded()
    {
        using var fixture = new IntegrationFixture();
        var modelPath = fixture.CreateModelFile();
        var installation = await fixture.InstallationService.RegisterExternalAsync(IntegrationFixture.ChildExecutablePath);
        var profile = await CreateProfileAsync(
            fixture,
            IntegrationFixture.GetFreePort(),
            installation.Id,
            modelPath,
            options => { });
        profile.ExtraArguments = "--exit-after-ms 1500";
        await fixture.Profiles.UpsertAsync(profile);
        var instance = await fixture.Manager.CreateAsync("异常退出实例", profile.Id);

        try
        {
            fixture.SetEnvironment("LLAMALAUNCHER_TEST_ENABLE_HTTP", "1");
            var result = await fixture.Manager.StartAsync(instance.Id, waitForReady: true);
            Assert.True(result.Success, result.Message);

            var exited = await WaitForStateAsync(instance, InstanceState.ExitedUnexpectedly, TimeSpan.FromSeconds(15));
            Assert.True(exited, $"实例未进入 ExitedUnexpectedly 状态，当前：{instance.State}");
            Assert.Contains("意外退出", instance.Record.LastError);

            var history = await fixture.History.QueryAsync(new() { InstanceId = instance.Id });
            Assert.Equal(InstanceState.ExitedUnexpectedly, history[0].FinalState);
            Assert.NotNull(history[0].EndedUtc);
        }
        finally
        {
            fixture.SetEnvironment("LLAMALAUNCHER_TEST_ENABLE_HTTP", null);
        }
    }

    [Fact]
    public async Task ApiKeyIsMaskedInLogsAndHistory()
    {
        using var fixture = new IntegrationFixture();
        var modelPath = fixture.CreateModelFile();
        var installation = await fixture.InstallationService.RegisterExternalAsync(IntegrationFixture.ChildExecutablePath);
        const string secret = "test-secret-key-abcdef";
        await fixture.Secrets.SetAsync("llamalauncher.apiKey", secret);

        var profile = await CreateProfileAsync(
            fixture,
            IntegrationFixture.GetFreePort(),
            installation.Id,
            modelPath,
            options =>
            {
                options.ApiKeyEnabled = true;
                options.ApiKeySecretName = "llamalauncher.apiKey";
            });
        // 子进程回显全部参数，验证日志侧仍然脱敏
        profile.ExtraArguments = "--echo-args";
        await fixture.Profiles.UpsertAsync(profile);
        var instance = await fixture.Manager.CreateAsync("脱敏实例", profile.Id);

        try
        {
            fixture.SetEnvironment("LLAMALAUNCHER_TEST_ENABLE_HTTP", "1");
            var result = await fixture.Manager.StartAsync(instance.Id, waitForReady: true);
            Assert.True(result.Success, result.Message);
            await Task.Delay(300); // 等待回显日志写入

            var logs = fixture.LogStore.GetRecent(instance.Id, 500);
            Assert.DoesNotContain(logs, l => l.Text.Contains(secret));
            Assert.Contains(logs, l => l.Text.Contains("--api-key ******"));

            var history = await fixture.History.QueryAsync(new() { InstanceId = instance.Id });
            Assert.DoesNotContain(secret, history[0].CommandLineMasked);
        }
        finally
        {
            fixture.SetEnvironment("LLAMALAUNCHER_TEST_ENABLE_HTTP", null);
        }
    }

    [Fact]
    public async Task Recovery_MarksStaleInstances()
    {
        using var fixture = new IntegrationFixture();
        var now = fixture.TimeProvider.GetUtcNow();
        var stale = new ServerInstance
        {
            Id = Guid.NewGuid(),
            Name = "残留实例",
            State = InstanceState.Ready,
            Host = "127.0.0.1",
            Port = 65500,
            ProcessId = 999999, // 不存在的 PID
            ProcessStartTimeUtc = now.AddHours(-1),
            ExecutablePathSnapshot = IntegrationFixture.ChildExecutablePath,
            CreatedUtc = now.AddHours(-2),
            UpdatedUtc = now.AddHours(-1),
        };
        await fixture.InstancesRepo.UpsertAsync(stale);

        var report = await fixture.Manager.InitializeAsync();

        Assert.Equal(1, report.MarkedStale);
        Assert.Equal(0, report.Adopted);
        var reloaded = await fixture.InstancesRepo.GetByIdAsync(stale.Id);
        Assert.Equal(InstanceState.ExitedUnexpectedly, reloaded!.State);
        Assert.NotNull(reloaded.LastError);
    }

    [Theory]
    [InlineData(InstanceState.LoadingModel)]
    [InlineData(InstanceState.Ready)]
    public async Task Recovery_AdoptsVerifiedRunningProcess(InstanceState persistedState)
    {
        using var fixture = new IntegrationFixture();
        var port = IntegrationFixture.GetFreePort();
        var original = Environment.GetEnvironmentVariable("LLAMALAUNCHER_TEST_ENABLE_HTTP");
        Process? child = null;
        try
        {
            Environment.SetEnvironmentVariable("LLAMALAUNCHER_TEST_ENABLE_HTTP", "1");
            var startInfo = new ProcessStartInfo
            {
                FileName = IntegrationFixture.ChildExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(port.ToString());
            startInfo.ArgumentList.Add("--host");
            startInfo.ArgumentList.Add("127.0.0.1");
            child = Process.Start(startInfo)!;
            var startTime = new DateTimeOffset(child.StartTime.ToUniversalTime());

            // 等待进程身份信息（启动时间/路径）可读，避免刚启动瞬间读取失败造成的抖动
            var identityDeadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < identityDeadline)
            {
                var identity = fixture.Inspector.Inspect(child.Id);
                if (identity.Exists && identity.ExecutablePath is not null && identity.StartTimeUtc is not null)
                {
                    break;
                }

                await Task.Delay(100);
            }

            var now = fixture.TimeProvider.GetUtcNow();
            var record = new ServerInstance
            {
                Id = Guid.NewGuid(),
                Name = "被接管实例",
                State = persistedState,
                Host = "127.0.0.1",
                Port = port,
                ProcessId = child.Id,
                ProcessStartTimeUtc = startTime,
                ExecutablePathSnapshot = IntegrationFixture.ChildExecutablePath,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            await fixture.InstancesRepo.UpsertAsync(record);

            var report = await fixture.Manager.InitializeAsync();

            Assert.Equal(1, report.Adopted);
            var instance = fixture.Manager.Get(record.Id);
            Assert.NotNull(instance);
            var ready = await WaitForStateAsync(instance!, InstanceState.Ready, TimeSpan.FromSeconds(15));
            Assert.True(ready, $"接管的实例未就绪，当前：{instance!.State}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LLAMALAUNCHER_TEST_ENABLE_HTTP", original);
            if (child is not null)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception)
                {
                }

                child.Dispose();
            }
        }
    }

    private static async Task<bool> WaitForStateAsync(
        LlamaLauncher.Application.Instances.ManagedInstance instance,
        InstanceState state,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (instance.State == state)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return instance.State == state;
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

            await Task.Delay(150);
        }

        return false;
    }
}
