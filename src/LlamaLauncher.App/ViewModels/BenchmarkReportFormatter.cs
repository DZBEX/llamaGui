using System.Globalization;
using System.Text;
using System.Text.Json;
using LlamaLauncher.Domain;
using LlamaLauncher.Domain.Benchmarks;

namespace LlamaLauncher.App.ViewModels;

/// <summary>基准测试报告格式化（Markdown 复制 / JSON 导出；纯函数，便于测试）。</summary>
public static class BenchmarkReportFormatter
{
    /// <summary>运行状态中文文本。</summary>
    public static string StateText(BenchmarkRunState state) => state switch
    {
        BenchmarkRunState.Running => "运行中",
        BenchmarkRunState.Succeeded => "已完成",
        BenchmarkRunState.Failed => "失败",
        BenchmarkRunState.Canceled => "已取消",
        _ => "未知",
    };

    /// <summary>生成 Markdown 报告（汇总表 + 样本明细）。</summary>
    public static string BuildMarkdown(BenchmarkRunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var builder = new StringBuilder();
        builder.AppendLine("# 模型性能基准报告");
        builder.AppendLine();
        builder.AppendLine($"- 运行 ID：{record.RunId}");
        builder.AppendLine($"- 状态：{StateText(record.State)}");
        var started = Formatting.LocalTime(record.StartedUtc);
        var ended = Formatting.LocalTime(record.EndedUtc);
        var duration = record.DurationMs is { } ms
            ? Formatting.Duration(TimeSpan.FromMilliseconds(ms))
            : "—";
        builder.AppendLine($"- 开始：{started} · 结束：{ended} · 总耗时：{duration}");

        var target = new StringBuilder($"- 实例：{record.InstanceNameSnapshot}");
        if (!string.IsNullOrEmpty(record.ModelNameSnapshot))
        {
            target.Append($" · 模型：{record.ModelNameSnapshot}");
        }

        if (!string.IsNullOrEmpty(record.ProfileNameSnapshot))
        {
            target.Append($" · 启动配置：{record.ProfileNameSnapshot}");
        }

        if (!string.IsNullOrEmpty(record.VersionLabel))
        {
            target.Append($" · llama.cpp：{record.VersionLabel}");
        }

        builder.AppendLine(target.ToString());
        builder.AppendLine(
            $"- 测试配置：提示长度 {string.Join(" / ", record.Options.PromptTokens)} tokens · " +
            $"生成长度 {record.Options.GenerateTokens} tokens · " +
            $"每档重复 {record.Options.Repetitions} 次 · " +
            $"预热 {record.Options.WarmupRuns} 次 · " +
            $"冷却 {record.Options.CooldownSeconds} 秒");
        builder.AppendLine($"- 应用版本：{record.AppVersion}");
        builder.AppendLine();
        builder.AppendLine("## 汇总（不含预热与失败样本；提示处理/生成速度单位 token/s，首 token 与总耗时单位 ms）");
        builder.AppendLine();
        builder.AppendLine("| 目标提示 | 实际提示(平均) | 提示处理 | 生成速度 | 首 token | 单次总耗时 | 样本 |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var summary in BenchmarkStatistics.Aggregate(record))
        {
            var actual = summary.AveragePromptTokens is { } average
                ? average.ToString("0", CultureInfo.InvariantCulture)
                : "—";
            builder.AppendLine(
                $"| {summary.TargetPromptTokens} | {actual} | " +
                $"{BenchmarkStatistics.FormatMetric(summary.PromptTokensPerSecond, 1)} | " +
                $"{BenchmarkStatistics.FormatMetric(summary.GeneratedTokensPerSecond, 1)} | " +
                $"{BenchmarkStatistics.FormatMetric(summary.TimeToFirstTokenMs, 0)} | " +
                $"{BenchmarkStatistics.FormatMetric(summary.TotalMs, 0)} | " +
                $"{summary.SuccessCount} 成功 / {summary.FailureCount} 失败 |");
        }

        builder.AppendLine();
        builder.AppendLine("## 样本明细");
        builder.AppendLine();
        builder.AppendLine("| 目标提示 | 序号 | 实际提示 | 提示处理(t/s) | 生成(tokens) | 生成速度(t/s) | 首 token(ms) | 总耗时(ms) | 备注 |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var sample in record.Samples)
        {
            builder.AppendLine(
                $"| {sample.TargetPromptTokens} | {(sample.IsWarmup ? "预热" : "第 " + sample.Repetition + " 次")} | " +
                $"{FormatDouble(sample.PromptTokens)} | {FormatDouble(sample.PromptTokensPerSecond)} | " +
                $"{FormatDouble(sample.GeneratedTokens)} | {FormatDouble(sample.GeneratedTokensPerSecond)} | " +
                $"{FormatDouble(sample.TimeToFirstTokenMs)} | {FormatDouble(sample.TotalMs)} | " +
                $"{sample.Error ?? ""} |");
        }

        if (!string.IsNullOrEmpty(record.ErrorSummary))
        {
            builder.AppendLine();
            builder.AppendLine("## 备注");
            builder.AppendLine();
            builder.AppendLine(record.ErrorSummary);
        }

        return builder.ToString();
    }

    /// <summary>生成 JSON 导出文本（含配置与样本明细）。</summary>
    public static string BuildJson(BenchmarkRunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return JsonSerializer.Serialize(record, JsonDefaults.Export);
    }

    private static string FormatDouble(double? value) => value is null
        ? "—"
        : value.Value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatDouble(int? value) => value is null
        ? "—"
        : value.Value.ToString(CultureInfo.InvariantCulture);
}
