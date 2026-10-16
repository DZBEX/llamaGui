using LlamaLauncher.Application.Updates;
using Xunit;

namespace LlamaLauncher.UnitTests.Updates;

/// <summary>启动自动检查的节流规则（让设置中的“检查间隔”真正生效）。</summary>
public sealed class UpdateCheckScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NeverChecked_ShouldCheck()
        => Assert.True(UpdateCheckSchedule.ShouldCheck(null, 24, Now));

    [Fact]
    public void WithinInterval_ShouldNotCheck()
    {
        var last = Now - TimeSpan.FromHours(1);
        Assert.False(UpdateCheckSchedule.ShouldCheck(last.ToString("O"), 24, Now));
    }

    [Fact]
    public void AfterInterval_ShouldCheck()
    {
        var last = Now - TimeSpan.FromHours(25);
        Assert.True(UpdateCheckSchedule.ShouldCheck(last.ToString("O"), 24, Now));
    }

    [Fact]
    public void InvalidValue_ShouldCheck()
        => Assert.True(UpdateCheckSchedule.ShouldCheck("not-a-date", 24, Now));

    [Fact]
    public void NonPositiveInterval_DisablesThrottle()
        => Assert.True(UpdateCheckSchedule.ShouldCheck((Now - TimeSpan.FromMinutes(1)).ToString("O"), 0, Now));

    [Fact]
    public void FutureTimestamp_ShouldNotCheck()
        => Assert.False(UpdateCheckSchedule.ShouldCheck((Now + TimeSpan.FromHours(3)).ToString("O"), 24, Now));

    [Theory]
    [InlineData("2026-10-17T07:59:00.0000000+00:00")]
    [InlineData("2026-10-17T15:59:00.0000000+08:00")]
    public void RoundtripTimestamp_ParsesAndCompares(string value)
        => Assert.False(UpdateCheckSchedule.ShouldCheck(value, 24, Now));
}
