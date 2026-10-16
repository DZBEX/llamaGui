using LlamaLauncher.Domain.Benchmarks;
using LlamaLauncher.Infrastructure.Sqlite;
using LlamaLauncher.IntegrationTests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Sqlite;

/// <summary>基准测试运行记录的 SQLite 仓储：往返、摘要列表、更新与删除。</summary>
public sealed class BenchmarkRunStoreTests
{
    [Fact]
    public async Task UpsertAndGet_RoundTripsFullRecord()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var store = new SqliteBenchmarkRunStore(database, NullLogger<SqliteBenchmarkRunStore>.Instance);
        var instanceId = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow.AddMinutes(-5);
        var record = new BenchmarkRunRecord
        {
            InstanceId = instanceId,
            InstanceNameSnapshot = "本机实例",
            ModelNameSnapshot = "qwen2.5-7b-q4_k_m.gguf",
            ProfileNameSnapshot = "默认配置",
            VersionLabel = "b11577",
            AppVersion = "0.9.0",
            StartedUtc = started,
            EndedUtc = started.AddSeconds(42),
            DurationMs = 42_000,
            State = BenchmarkRunState.Succeeded,
            Options = new BenchmarkOptions
            {
                PromptTokens = new List<int> { 512, 2048 },
                GenerateTokens = 64,
                Repetitions = 2,
                WarmupRuns = 1,
                CooldownSeconds = 3,
            },
            Samples = new List<BenchmarkSample>
            {
                new()
                {
                    TargetPromptTokens = 512,
                    Repetition = 1,
                    IsWarmup = false,
                    PromptTokens = 500,
                    PromptMs = 250.5,
                    PromptTokensPerSecond = 1996.0,
                    GeneratedTokens = 64,
                    GeneratedMs = 512.0,
                    GeneratedTokensPerSecond = 125.0,
                    TimeToFirstTokenMs = 260.0,
                    TotalMs = 780.5,
                },
                new()
                {
                    TargetPromptTokens = 2048,
                    Repetition = 0,
                    IsWarmup = true,
                    Error = "预热失败示例",
                },
            },
            ErrorSummary = "含一条失败样本。",
        };

        await store.UpsertAsync(record);
        var loaded = await store.GetAsync(record.RunId);

        Assert.NotNull(loaded);
        Assert.Equal(record.RunId, loaded!.RunId);
        Assert.Equal(instanceId, loaded.InstanceId);
        Assert.Equal("本机实例", loaded.InstanceNameSnapshot);
        Assert.Equal("qwen2.5-7b-q4_k_m.gguf", loaded.ModelNameSnapshot);
        Assert.Equal("默认配置", loaded.ProfileNameSnapshot);
        Assert.Equal("b11577", loaded.VersionLabel);
        Assert.Equal("0.9.0", loaded.AppVersion);
        Assert.Equal(record.State, loaded.State);
        Assert.Equal(record.DurationMs, loaded.DurationMs);
        Assert.Equal(started.ToUniversalTime(), loaded.StartedUtc.ToUniversalTime());
        Assert.Equal(record.Options.PromptTokens, loaded.Options.PromptTokens);
        Assert.Equal(record.Options.GenerateTokens, loaded.Options.GenerateTokens);
        Assert.Equal(record.Options.CooldownSeconds, loaded.Options.CooldownSeconds);
        Assert.Equal(2, loaded.Samples.Count);
        Assert.Equal(1996.0, loaded.Samples[0].PromptTokensPerSecond);
        Assert.Equal(780.5, loaded.Samples[0].TotalMs);
        Assert.True(loaded.Samples[1].IsWarmup);
        Assert.Equal("预热失败示例", loaded.Samples[1].Error);
        Assert.Equal("含一条失败样本。", loaded.ErrorSummary);
    }

    [Fact]
    public async Task List_ReturnsRecentSummariesWithoutSamples()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var store = new SqliteBenchmarkRunStore(database, NullLogger<SqliteBenchmarkRunStore>.Instance);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++)
        {
            await store.UpsertAsync(new BenchmarkRunRecord
            {
                InstanceNameSnapshot = $"实例 {i}",
                StartedUtc = now.AddMinutes(-i),
                State = BenchmarkRunState.Succeeded,
            });
        }

        var summaries = await store.ListAsync(2);

        Assert.Equal(2, summaries.Count);
        Assert.Equal("实例 0", summaries[0].InstanceNameSnapshot);
        Assert.Equal("实例 1", summaries[1].InstanceNameSnapshot);
        Assert.Equal(BenchmarkRunState.Succeeded, summaries[0].State);
    }

    [Fact]
    public async Task Upsert_UpdatesExistingRecordInPlace()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var store = new SqliteBenchmarkRunStore(database, NullLogger<SqliteBenchmarkRunStore>.Instance);
        var record = new BenchmarkRunRecord
        {
            InstanceNameSnapshot = "实例",
            StartedUtc = DateTimeOffset.UtcNow,
            State = BenchmarkRunState.Running,
        };
        await store.UpsertAsync(record);

        record.State = BenchmarkRunState.Succeeded;
        record.Samples.Add(new BenchmarkSample { TargetPromptTokens = 512, Repetition = 1 });
        record.EndedUtc = DateTimeOffset.UtcNow;
        record.DurationMs = 1234;
        await store.UpsertAsync(record);

        var summaries = await store.ListAsync(10);
        Assert.Single(summaries);
        Assert.Equal(BenchmarkRunState.Succeeded, summaries[0].State);
        var loaded = await store.GetAsync(record.RunId);
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Samples);
        Assert.Equal(1234, loaded.DurationMs);
    }

    [Fact]
    public async Task Delete_RemovesRecord()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var store = new SqliteBenchmarkRunStore(database, NullLogger<SqliteBenchmarkRunStore>.Instance);
        var record = new BenchmarkRunRecord
        {
            InstanceNameSnapshot = "实例",
            StartedUtc = DateTimeOffset.UtcNow,
        };
        await store.UpsertAsync(record);

        Assert.True(await store.DeleteAsync(record.RunId));
        Assert.Null(await store.GetAsync(record.RunId));
        Assert.False(await store.DeleteAsync(record.RunId));
        Assert.Empty(await store.ListAsync());
    }
}
