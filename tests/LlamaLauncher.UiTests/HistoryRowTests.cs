using LlamaLauncher.App.ViewModels;
using LlamaLauncher.Domain;
using Xunit;

namespace LlamaLauncher.UiTests;

/// <summary>启动历史行的状态显示（运行中的记录不应显示为“启动中”）。</summary>
public sealed class HistoryRowTests
{
    [Fact]
    public void RunningEntry_ShowsRunning()
    {
        var entry = new StartupHistoryEntry
        {
            InstanceId = Guid.NewGuid(),
            StartedUtc = DateTimeOffset.UtcNow,
            FinalState = InstanceState.Starting,
        };

        var row = new HistoryRow(entry);

        Assert.Equal("运行中", row.FinalStateText);
        Assert.Equal("—", row.EndedText);
    }

    [Fact]
    public void EndedEntry_ShowsFinalState()
    {
        var entry = new StartupHistoryEntry
        {
            InstanceId = Guid.NewGuid(),
            StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            EndedUtc = DateTimeOffset.UtcNow,
            FinalState = InstanceState.Stopped,
            ExitCode = -1,
        };

        var row = new HistoryRow(entry);

        Assert.Equal("已停止", row.FinalStateText);
        Assert.Equal("-1", row.ExitCodeText);
    }
}
