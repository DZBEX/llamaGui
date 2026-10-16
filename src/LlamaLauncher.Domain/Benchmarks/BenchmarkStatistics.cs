using System.Globalization;

namespace LlamaLauncher.Domain.Benchmarks;

/// <summary>单项指标的统计摘要（样本标准差，n-1）。</summary>
public sealed record MetricSummary(int Count, double? Mean, double? StdDev, double? Min, double? Max)
{
    /// <summary>空摘要。</summary>
    public static readonly MetricSummary Empty = new(0, null, null, null, null);
}

/// <summary>按提示长度档位聚合的统计（不含预热与失败样本）。</summary>
public sealed record BenchmarkGroupSummary(
    int TargetPromptTokens,
    int SuccessCount,
    int FailureCount,
    double? AveragePromptTokens,
    MetricSummary PromptTokensPerSecond,
    MetricSummary GeneratedTokensPerSecond,
    MetricSummary TimeToFirstTokenMs,
    MetricSummary TotalMs);

/// <summary>基准测试的限量与配置校验（所有入口共用；纯逻辑，无 I/O）。</summary>
public static class BenchmarkLimits
{
    public const int MinPromptTokens = 16;

    public const int MaxPromptTokens = 131_072;

    public const int MaxPromptLengths = 6;

    public const int MinGenerateTokens = 1;

    public const int MaxGenerateTokens = 4096;

    public const int MinRepetitions = 1;

    public const int MaxRepetitions = 20;

    public const int MaxWarmupRuns = 10;

    public const int MaxCooldownSeconds = 300;

    /// <summary>校验配置；返回人类可读的问题列表（为空表示合法）。</summary>
    public static IReadOnlyList<string> Validate(BenchmarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var issues = new List<string>();

        if (options.PromptTokens is null || options.PromptTokens.Count == 0)
        {
            issues.Add("至少需要一个提示长度档位。");
        }
        else
        {
            if (options.PromptTokens.Count > MaxPromptLengths)
            {
                issues.Add($"提示长度档位最多 {MaxPromptLengths} 个。");
            }

            var duplicates = options.PromptTokens
                .GroupBy(v => v)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (duplicates.Count > 0)
            {
                issues.Add($"提示长度档位存在重复值：{string.Join("、", duplicates)}。");
            }

            foreach (var value in options.PromptTokens)
            {
                if (value < MinPromptTokens || value > MaxPromptTokens)
                {
                    issues.Add(
                        $"提示长度 {value} 超出允许范围（{MinPromptTokens}–{MaxPromptTokens} token）。");
                }
            }
        }

        if (options.GenerateTokens < MinGenerateTokens || options.GenerateTokens > MaxGenerateTokens)
        {
            issues.Add($"生成长度需在 {MinGenerateTokens}–{MaxGenerateTokens} token 之间。");
        }

        if (options.Repetitions < MinRepetitions || options.Repetitions > MaxRepetitions)
        {
            issues.Add($"重复次数需在 {MinRepetitions}–{MaxRepetitions} 之间。");
        }

        if (options.WarmupRuns < 0 || options.WarmupRuns > MaxWarmupRuns)
        {
            issues.Add($"预热次数需在 0–{MaxWarmupRuns} 之间。");
        }

        if (options.CooldownSeconds < 0 || options.CooldownSeconds > MaxCooldownSeconds)
        {
            issues.Add($"冷却时间需在 0–{MaxCooldownSeconds} 秒之间。");
        }

        return issues;
    }
}

/// <summary>基准记录的聚合计算（纯函数）。</summary>
public static class BenchmarkStatistics
{
    /// <summary>
    /// 按提示长度档位聚合统计：排除预热样本与失败样本；失败数按档位单独计数。
    /// </summary>
    public static IReadOnlyList<BenchmarkGroupSummary> Aggregate(BenchmarkRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var results = new List<BenchmarkGroupSummary>();

        foreach (var group in run.Samples.GroupBy(s => s.TargetPromptTokens).OrderBy(g => g.Key))
        {
            var measured = group.Where(s => !s.IsWarmup && s.Succeeded).ToList();
            var failures = group.Count(s => !s.IsWarmup && !s.Succeeded);
            var averages = measured
                .Where(s => s.PromptTokens is not null)
                .Select(s => (double)s.PromptTokens!.Value)
                .ToList();

            results.Add(new BenchmarkGroupSummary(
                group.Key,
                measured.Count,
                failures,
                averages.Count > 0 ? averages.Average() : null,
                Summarize(measured.Select(s => s.PromptTokensPerSecond)),
                Summarize(measured.Select(s => s.GeneratedTokensPerSecond)),
                Summarize(measured.Select(s => s.TimeToFirstTokenMs)),
                Summarize(measured.Select(s => s.TotalMs))));
        }

        return results;
    }

    /// <summary>汇总一组数值（忽略 null；样本标准差 n-1）。</summary>
    public static MetricSummary Summarize(IEnumerable<double?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var list = values.Where(v => v is not null && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value))
            .Select(v => v!.Value)
            .ToList();
        if (list.Count == 0)
        {
            return MetricSummary.Empty;
        }

        var mean = list.Average();
        double? stdDev = null;
        if (list.Count >= 2)
        {
            var sum = list.Sum(v => (v - mean) * (v - mean));
            stdDev = Math.Sqrt(sum / (list.Count - 1));
        }

        return new MetricSummary(list.Count, mean, stdDev, list.Min(), list.Max());
    }

    /// <summary>格式化为“均值 ± 标准差”（无标准差时仅均值；空摘要为 —）。</summary>
    public static string FormatMetric(MetricSummary summary, int decimals)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (summary.Mean is null)
        {
            return "—";
        }

        var format = decimals <= 0 ? "0" : "0." + new string('#', decimals);
        var mean = summary.Mean.Value.ToString(format, CultureInfo.InvariantCulture);
        return summary.StdDev is { } sd
            ? $"{mean} ± {sd.ToString(format, CultureInfo.InvariantCulture)}"
            : mean;
    }
}
