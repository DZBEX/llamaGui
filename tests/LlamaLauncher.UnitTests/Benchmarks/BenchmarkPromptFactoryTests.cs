using LlamaLauncher.Application.Benchmarks;
using Xunit;

namespace LlamaLauncher.UnitTests.Benchmarks;

public sealed class BenchmarkPromptFactoryTests
{
    [Fact]
    public void Build_ProducesApproximateRequestedLength()
    {
        var prompt = BenchmarkPromptFactory.Build(512, "abcd1234");

        var expectedChars = (int)Math.Ceiling(512 * BenchmarkPromptFactory.CharsPerTokenEstimate);
        Assert.True(
            prompt.Length >= expectedChars,
            $"提示长度 {prompt.Length} 应不少于目标字符数 {expectedChars}。");
        Assert.True(
            prompt.Length < expectedChars + 2000,
            $"提示长度 {prompt.Length} 不应显著超过目标字符数 {expectedChars}。");
    }

    [Fact]
    public void Build_IncludesSaltPrefixForCacheBypass()
    {
        var prompt = BenchmarkPromptFactory.Build(32, "salt-001");

        Assert.StartsWith("Benchmark probe salt-001. ", prompt);
    }

    [Fact]
    public void Build_LongerTargetProducesLongerPrompt()
    {
        var shortPrompt = BenchmarkPromptFactory.Build(128, "s");
        var longPrompt = BenchmarkPromptFactory.Build(2048, "s");

        Assert.True(longPrompt.Length > shortPrompt.Length * 4);
    }

    [Fact]
    public void Build_RejectsNonPositiveTarget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BenchmarkPromptFactory.Build(0, "s"));
        Assert.Throws<ArgumentOutOfRangeException>(() => BenchmarkPromptFactory.Build(-1, "s"));
    }

    [Fact]
    public void CreateSalt_ReturnsEightCharacterToken()
    {
        var salt = BenchmarkPromptFactory.CreateSalt();

        Assert.Equal(8, salt.Length);
        Assert.NotEqual(salt, BenchmarkPromptFactory.CreateSalt());
    }
}
