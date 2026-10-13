using System.Diagnostics;
using LlamaLauncher.Domain;
using LlamaLauncher.IntegrationTests.TestSupport;
using LlamaLauncher.Infrastructure.Probing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Probing;

public sealed class LlamaBinaryProbeServiceTests
{
    private static string ChildExecutablePath => ExternalProcessRunnerTests.ChildExecutablePath;

    private static LlamaBinaryProbeService CreateService(LlamaProbeTimeouts? timeouts = null) => new(
        new ExternalProcessRunner(new FakeTimeProvider(), NullLogger<ExternalProcessRunner>.Instance),
        new FakeTimeProvider(),
        NullLogger<LlamaBinaryProbeService>.Instance,
        timeouts);

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
    public async Task ProbeSuccess_ParsesVersionHelpAndDevices()
    {
        var service = CreateService();

        var capabilities = await service.ProbeAsync(ChildExecutablePath, forceRefresh: true);

        Assert.Equal(ProbeOutcome.Success, capabilities.Outcome);
        Assert.Contains("version: 4321", capabilities.VersionText);
        Assert.True(capabilities.Supports("ctx-size"));
        Assert.True(capabilities.Supports("api-key"));
        Assert.True(capabilities.Supports("flash-attn"));
        Assert.Equal(OptionValueKind.Optional, capabilities.FindOption("flash-attn")!.ValueKind);
        Assert.Contains(capabilities.Devices, d => d.Backend == BackendKind.Cuda && d.Name.Contains("RTX 4090"));
        Assert.Contains(capabilities.Devices, d => d.Backend == BackendKind.Cpu);
        Assert.Empty(capabilities.Warnings);

        // 缓存：同一文件指纹直接命中
        var cached = service.TryGetCached(ChildExecutablePath);
        Assert.NotNull(cached);
        var second = await service.ProbeAsync(ChildExecutablePath);
        Assert.Same(cached, second);
    }

    [Fact]
    public async Task HelpFailure_YieldsPartialFailureWithDiagnosis()
    {
        var service = CreateService();
        LlamaLauncher.Domain.ServerCapabilities capabilities;

        var original = Environment.GetEnvironmentVariable("LLAMALAUNCHER_TEST_HELP_EXIT");
        try
        {
            Environment.SetEnvironmentVariable("LLAMALAUNCHER_TEST_HELP_EXIT", "1");
            capabilities = await service.ProbeAsync(ChildExecutablePath, forceRefresh: true);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LLAMALAUNCHER_TEST_HELP_EXIT", original);
        }

        Assert.Equal(ProbeOutcome.PartialFailure, capabilities.Outcome);
        Assert.Empty(capabilities.Options);
        Assert.Contains(capabilities.Warnings, w => w.Contains("--help 探测失败"));
        var helpProbe = capabilities.ProbeResults.Single(r => r.Command.Contains("--help"));
        Assert.Equal(1, helpProbe.ExitCode);
        Assert.Contains("unknown argument", helpProbe.StandardError);
    }

    [Fact]
    public async Task HelpTimeout_IsKilledAndReported()
    {
        var service = CreateService(new LlamaProbeTimeouts(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(10)));

        var stopwatch = Stopwatch.StartNew();
        var original = Environment.GetEnvironmentVariable("LLAMALAUNCHER_TEST_HELP_SLEEP_MS");
        LlamaLauncher.Domain.ServerCapabilities capabilities;
        try
        {
            Environment.SetEnvironmentVariable("LLAMALAUNCHER_TEST_HELP_SLEEP_MS", "20000");
            capabilities = await service.ProbeAsync(ChildExecutablePath, forceRefresh: true);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LLAMALAUNCHER_TEST_HELP_SLEEP_MS", original);
        }

        stopwatch.Stop();
        Assert.Equal(ProbeOutcome.PartialFailure, capabilities.Outcome);
        var helpProbe = capabilities.ProbeResults.Single(r => r.Command.Contains("--help"));
        Assert.True(helpProbe.TimedOut);
        Assert.Contains(capabilities.Warnings, w => w.Contains("超时"));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"探测超时后未及时返回：{stopwatch.Elapsed}。");
    }

    [Fact]
    public async Task DevicesFailure_IsWarningNotFatal()
    {
        var service = CreateService();
        LlamaLauncher.Domain.ServerCapabilities capabilities;

        var original = Environment.GetEnvironmentVariable("LLAMALAUNCHER_TEST_DEVICES_EXIT");
        try
        {
            Environment.SetEnvironmentVariable("LLAMALAUNCHER_TEST_DEVICES_EXIT", "1");
            capabilities = await service.ProbeAsync(ChildExecutablePath, forceRefresh: true);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LLAMALAUNCHER_TEST_DEVICES_EXIT", original);
        }

        Assert.Equal(ProbeOutcome.Success, capabilities.Outcome);
        Assert.Empty(capabilities.Devices);
        Assert.Contains(capabilities.Warnings, w => w.Contains("--list-devices"));
        Assert.True(capabilities.Supports("model"));
    }

    [Fact]
    public async Task MissingExecutable_YieldsFailedOutcome()
    {
        var service = CreateService();
        var capabilities = await service.ProbeAsync(
            Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".exe"));

        Assert.Equal(ProbeOutcome.Failed, capabilities.Outcome);
        Assert.NotEmpty(capabilities.Warnings);
        Assert.Null(service.TryGetCached(Path.Combine(Path.GetTempPath(), "missing.exe")));
    }

    [Fact]
    public async Task CacheInvalidatesWhenFileChanges()
    {
        using var directory = new TempDirectory();
        var copy = directory.Combine("llama-server-copy.exe");
        File.Copy(ChildExecutablePath, copy);
        var service = CreateService();

        await service.ProbeAsync(copy, forceRefresh: true);
        Assert.NotNull(service.TryGetCached(copy));

        File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddMinutes(5));
        Assert.Null(service.TryGetCached(copy));
    }
}
