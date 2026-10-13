using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.IntegrationTests.TestSupport;
using LlamaLauncher.Infrastructure.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Infrastructure;

public sealed class InstanceLogStoreTests
{
    [Fact]
    public void AppendAndGetRecent_KeepsOrderAndCaps()
    {
        using var directory = new TempDirectory();
        var store = new InstanceLogStore(directory.Combine("logs"), new FakeTimeProvider(), NullLogger<InstanceLogStore>.Instance)
        {
            Options = new LogRetentionOptions { MemoryBufferLines = 10 },
        };
        var id = Guid.NewGuid();
        for (var i = 0; i < 25; i++)
        {
            store.Append(id, "测试实例", LogStreamKind.StandardOutput, "行 " + i);
        }

        var recent = store.GetRecent(id, 100);

        Assert.Equal(10, recent.Count);
        Assert.Equal("行 24", recent[^1].Text);
        Assert.Equal("行 15", recent[0].Text);
    }

    [Fact]
    public async Task Append_WritesMaskedLinesToFile()
    {
        using var directory = new TempDirectory();
        var store = new InstanceLogStore(directory.Combine("logs"), new FakeTimeProvider(), NullLogger<InstanceLogStore>.Instance);
        var id = Guid.NewGuid();

        store.AddKnownSecret("top-secret-value");
        store.Append(id, "实例", LogStreamKind.StandardOutput, "token=top-secret-value end");
        store.Append(id, "实例", LogStreamKind.StandardError, "--api-key abcdef123456");
        await store.FlushAsync();

        var path = store.GetLogFilePath(id, "实例");
        Assert.True(File.Exists(path));
        var content = await ReadAllTextSharedAsync(path);
        Assert.DoesNotContain("top-secret-value", content);
        Assert.DoesNotContain("abcdef123456", content);
        Assert.Contains("******", content);
    }

    private static async Task<string> ReadAllTextSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task FileRotation_KeepsBoundedFiles()
    {
        using var directory = new TempDirectory();
        var store = new InstanceLogStore(directory.Combine("logs"), new FakeTimeProvider(), NullLogger<InstanceLogStore>.Instance)
        {
            Options = new LogRetentionOptions { MaxFileBytes = 2048, MaxRotatedFiles = 2, MemoryBufferLines = 1000 },
        };
        var id = Guid.NewGuid();

        for (var i = 0; i < 500; i++)
        {
            store.Append(id, "轮转", LogStreamKind.StandardOutput, $"这是一行比较长的日志内容用于触发文件轮转 {i:D4} " + new string('x', 40));
        }

        await store.FlushAsync();

        var basePath = store.GetLogFilePath(id, "轮转");
        Assert.True(File.Exists(basePath));
        Assert.True(File.Exists(basePath + ".1"), "应存在第 1 个轮转文件。");
        Assert.False(File.Exists(basePath + ".3"), "轮转文件数量应受上限约束。");
        Assert.True(new FileInfo(basePath).Length <= 4096, "当前日志文件应小于两份上限。");
    }

    [Fact]
    public void CleanupOldFiles_RemovesExpiredFiles()
    {
        using var directory = new TempDirectory();
        var logsDir = directory.Combine("logs");
        var store = new InstanceLogStore(logsDir, new FakeTimeProvider(), NullLogger<InstanceLogStore>.Instance);
        var oldFile = Path.Combine(logsDir, "old.log");
        File.WriteAllText(oldFile, "old");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-30));

        var removed = store.CleanupOldFiles(TimeSpan.FromDays(7));

        Assert.Equal(1, removed);
        Assert.False(File.Exists(oldFile));
    }
}
