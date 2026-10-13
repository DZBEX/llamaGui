using LlamaLauncher.App.ViewModels;
using LlamaLauncher.Domain;
using Xunit;

namespace LlamaLauncher.UiTests;

public sealed class FormattingTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1610612736, "1.5 GB")]
    public void Bytes_Formats(long bytes, string expected)
        => Assert.Equal(expected, Formatting.Bytes(bytes));

    [Fact]
    public void Bytes_NullIsDash() => Assert.Equal("—", Formatting.Bytes(null));

    [Theory]
    [InlineData(InstanceState.Ready, "运行中")]
    [InlineData(InstanceState.Failed, "失败")]
    [InlineData(InstanceState.ExitedUnexpectedly, "异常退出")]
    public void InstanceStateText_Maps(InstanceState state, string expected)
        => Assert.Equal(expected, Formatting.InstanceStateText(state));

    [Fact]
    public void Duration_FormatsHumanReadable()
    {
        Assert.Equal("30 秒", Formatting.Duration(TimeSpan.FromSeconds(30)));
        Assert.Equal("2 分 5 秒", Formatting.Duration(TimeSpan.FromSeconds(125)));
        Assert.Equal("1 小时 1 分", Formatting.Duration(TimeSpan.FromMinutes(61)));
        Assert.Equal("—", Formatting.Duration(null));
    }

    [Fact]
    public void UpdateStageText_Maps()
    {
        Assert.Equal("已回滚", Formatting.UpdateStageText(UpdateStage.RolledBack));
        Assert.Equal("能力验证中", Formatting.UpdateStageText(UpdateStage.Probing));
    }

    [Fact]
    public void MessageTime_ChoosesPrecisionByAge()
    {
        // 用本机时区构造，保证 ToLocalTime 为恒等变换，测试结果与运行环境时区无关。
        static DateTimeOffset Local(int year, int month, int day, int hour, int minute)
            => new(year, month, day, hour, minute, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(year, month, day, hour, minute, 0)));

        var now = Local(2026, 10, 13, 12, 0);
        Assert.Equal("09:05", Formatting.MessageTime(Local(2026, 10, 13, 9, 5), now));
        Assert.Equal("10-01 08:30", Formatting.MessageTime(Local(2026, 10, 1, 8, 30), now));
        Assert.Equal("2025-12-31 23:00", Formatting.MessageTime(Local(2025, 12, 31, 23, 0), now));
        Assert.Equal(string.Empty, Formatting.MessageTime(null, now));
    }
}
