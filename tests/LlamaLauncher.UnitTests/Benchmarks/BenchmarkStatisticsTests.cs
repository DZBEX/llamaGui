using LlamaLauncher.Domain.Benchmarks;
using Xunit;

namespace LlamaLauncher.UnitTests.Benchmarks;

public sealed class BenchmarkStatisticsTests
{
    [Fact]
    public void Summarize_ComputesMeanSampleStdDevMinMax()
    {
        var summary = BenchmarkStatistics.Summarize(new double?[] { 10, 20, 30 });

        Assert.Equal(3, summary.Count);
        Assert.Equal(20, summary.Mean);
        Assert.Equal(10, summary.StdDev!.Value, 6);
        Assert.Equal(10, summary.Min);
        Assert.Equal(30, summary.Max);
    }

    [Fact]
    public void Summarize_IgnoresNullAndNonFiniteValues()
    {
        var summary = BenchmarkStatistics.Summarize(new double?[] { 5, null, 15, double.NaN });

        Assert.Equal(2, summary.Count);
        Assert.Equal(10, summary.Mean);
        Assert.Equal(7.071068, summary.StdDev!.Value, 5);
        Assert.Equal(5, summary.Min);
        Assert.Equal(15, summary.Max);
    }

    [Fact]
    public void Summarize_SingleValueHasNoStdDev()
    {
        var summary = BenchmarkStatistics.Summarize(new double?[] { 42 });

        Assert.Equal(1, summary.Count);
        Assert.Equal(42, summary.Mean);
        Assert.Null(summary.StdDev);
    }

    [Fact]
    public void Summarize_EmptyReturnsEmptySummary()
    {
        var summary = BenchmarkStatistics.Summarize(Array.Empty<double?>());

        Assert.Equal(0, summary.Count);
        Assert.Null(summary.Mean);
        Assert.Null(summary.StdDev);
        Assert.Null(summary.Min);
        Assert.Null(summary.Max);
    }

    [Fact]
    public void Aggregate_GroupsByTargetAndExcludesWarmupsAndFailures()
    {
        var run = new BenchmarkRunRecord();
        run.Samples.Add(Sample(512, 0, warmup: true, pp: 100, tg: 20, ttft: 50, total: 1000));
        run.Samples.Add(Sample(512, 1, warmup: false, pp: 200, tg: 40, ttft: 100, total: 2000));
        run.Samples.Add(Sample(512, 2, warmup: false, pp: 300, tg: 60, ttft: 150, total: 3000));
        run.Samples.Add(Sample(512, 3, warmup: false, pp: null, tg: null, ttft: null, total: null, error: "失败"));
        run.Samples.Add(Sample(2048, 1, warmup: false, pp: 400, tg: 80, ttft: 200, total: 4000));

        var groups = BenchmarkStatistics.Aggregate(run);

        Assert.Equal(2, groups.Count);
        var small = groups[0];
        Assert.Equal(512, small.TargetPromptTokens);
        Assert.Equal(2, small.SuccessCount);
        Assert.Equal(1, small.FailureCount);
        Assert.Equal(250, small.PromptTokensPerSecond.Mean);
        Assert.Equal(50, small.GeneratedTokensPerSecond.Mean);
        Assert.Equal(125, small.TimeToFirstTokenMs.Mean);
        Assert.Equal(2500, small.TotalMs.Mean);
        Assert.Equal(70.710678, small.PromptTokensPerSecond.StdDev!.Value, 5);

        var large = groups[1];
        Assert.Equal(2048, large.TargetPromptTokens);
        Assert.Equal(1, large.SuccessCount);
        Assert.Equal(0, large.FailureCount);
        Assert.Equal(400, large.PromptTokensPerSecond.Mean);
        Assert.Equal(2048, large.AveragePromptTokens);
    }

    [Fact]
    public void Aggregate_AveragesActualPromptTokensWhenReported()
    {
        var run = new BenchmarkRunRecord();
        var first = Sample(512, 1, warmup: false, pp: 100, tg: 20, ttft: 50, total: 1000);
        first.PromptTokens = 500;
        var second = Sample(512, 2, warmup: false, pp: 100, tg: 20, ttft: 50, total: 1000);
        second.PromptTokens = 540;
        run.Samples.Add(first);
        run.Samples.Add(second);

        var group = Assert.Single(BenchmarkStatistics.Aggregate(run));

        Assert.Equal(520, group.AveragePromptTokens);
    }

    [Fact]
    public void FormatMetric_FormatsMeanWithStdDev()
    {
        var summary = new MetricSummary(3, 512.34, 12.5, 500, 530);

        Assert.Equal("512.3 ± 12.5", BenchmarkStatistics.FormatMetric(summary, 1));
        Assert.Equal("512 ± 13", BenchmarkStatistics.FormatMetric(summary, 0));
    }

    [Fact]
    public void FormatMetric_WithoutStdDevOrMean()
    {
        Assert.Equal("42", BenchmarkStatistics.FormatMetric(new MetricSummary(1, 42, null, 42, 42), 1));
        Assert.Equal("—", BenchmarkStatistics.FormatMetric(MetricSummary.Empty, 1));
    }

    private static BenchmarkSample Sample(
        int target,
        int repetition,
        bool warmup,
        double? pp,
        double? tg,
        double? ttft,
        double? total,
        string? error = null) => new()
    {
        TargetPromptTokens = target,
        Repetition = repetition,
        IsWarmup = warmup,
        PromptTokens = target,
        PromptTokensPerSecond = pp,
        GeneratedTokensPerSecond = tg,
        TimeToFirstTokenMs = ttft,
        TotalMs = total,
        Error = error,
    };
}
