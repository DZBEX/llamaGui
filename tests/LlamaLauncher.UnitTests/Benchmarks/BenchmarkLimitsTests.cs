using LlamaLauncher.Domain.Benchmarks;
using Xunit;

namespace LlamaLauncher.UnitTests.Benchmarks;

public sealed class BenchmarkLimitsTests
{
    [Fact]
    public void Validate_AcceptsDefaultAndBoundaryOptions()
    {
        Assert.Empty(BenchmarkLimits.Validate(new BenchmarkOptions()));

        var boundary = new BenchmarkOptions
        {
            PromptTokens = new List<int> { BenchmarkLimits.MinPromptTokens, BenchmarkLimits.MaxPromptTokens },
            GenerateTokens = BenchmarkLimits.MaxGenerateTokens,
            Repetitions = BenchmarkLimits.MaxRepetitions,
            WarmupRuns = BenchmarkLimits.MaxWarmupRuns,
            CooldownSeconds = BenchmarkLimits.MaxCooldownSeconds,
        };
        Assert.Empty(BenchmarkLimits.Validate(boundary));
    }

    [Fact]
    public void Validate_RejectsEmptyPromptLengths()
    {
        var issues = BenchmarkLimits.Validate(new BenchmarkOptions { PromptTokens = new List<int>() });

        Assert.Contains(issues, i => i.Contains("至少需要一个提示长度档位"));
    }

    [Fact]
    public void Validate_RejectsTooManyDuplicatesAndOutOfRangeLengths()
    {
        var tooMany = new BenchmarkOptions
        {
            PromptTokens = new List<int> { 16, 32, 64, 128, 256, 512, 1024 },
        };
        Assert.Contains(BenchmarkLimits.Validate(tooMany), i => i.Contains("最多 6 个"));

        var duplicates = new BenchmarkOptions { PromptTokens = new List<int> { 512, 512 } };
        Assert.Contains(BenchmarkLimits.Validate(duplicates), i => i.Contains("重复"));

        var outOfRange = new BenchmarkOptions { PromptTokens = new List<int> { 1, 999_999 } };
        var issues = BenchmarkLimits.Validate(outOfRange);
        Assert.Contains(issues, i => i.Contains("提示长度 1 "));
        Assert.Contains(issues, i => i.Contains("提示长度 999999 "));
    }

    [Fact]
    public void Validate_RejectsOutOfRangeNumericOptions()
    {
        var options = new BenchmarkOptions
        {
            GenerateTokens = 0,
            Repetitions = 0,
            WarmupRuns = -1,
            CooldownSeconds = BenchmarkLimits.MaxCooldownSeconds + 1,
        };

        var issues = BenchmarkLimits.Validate(options);

        Assert.Contains(issues, i => i.Contains("生成长度"));
        Assert.Contains(issues, i => i.Contains("重复次数"));
        Assert.Contains(issues, i => i.Contains("预热次数"));
        Assert.Contains(issues, i => i.Contains("冷却时间"));
    }
}
