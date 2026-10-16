using System.Text;

namespace LlamaLauncher.Application.Benchmarks;

/// <summary>
/// 基准提示构造（纯逻辑）：按目标 token 数生成近似长度的固定文本。
/// 每个样本附加唯一盐前缀，避免服务端 KV 缓存（cache_prompt）在重复测量中复用前缀导致偏快。
/// </summary>
public static class BenchmarkPromptFactory
{
    /// <summary>字符数→token 数的经验估计（英文文本约 4 字符 / token）。</summary>
    public const double CharsPerTokenEstimate = 4.0;

    /// <summary>固定填充文本（与模型无关，保证跨运行可比）。</summary>
    private const string Filler =
        "Above the harbor a low fog drifted between the cranes while the morning ferries sounded " +
        "their horns in slow succession. Engineers on the north pier checked the tide gauges " +
        "twice each hour and recorded every reading in a narrow paper logbook that smelled " +
        "faintly of salt. The wind had shifted overnight, so the weather station on the ridge " +
        "sent a short message to the control room describing steady pressure and a thin band of " +
        "cloud moving east. Nobody expected trouble before noon, yet the schedule allowed a wide " +
        "margin for decisions, because the instruments were old and the sea rarely behaved the " +
        "same way for two days in a row. On the far side of the bay, a crew painted the hull of " +
        "a retired trawler, taking care to work only while the humidity stayed low, and the " +
        "sound of their brushes carried across the water between the bells of the channel markers.";

    /// <summary>生成一个短随机盐（用于让每个样本的提示前缀不同）。</summary>
    public static string CreateSalt() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// 生成近似 <paramref name="targetPromptTokens"/> 个 token 的提示文本。
    /// 实际 token 数以服务端 timings.prompt_n 为准。
    /// </summary>
    public static string Build(int targetPromptTokens, string salt)
    {
        ArgumentNullException.ThrowIfNull(salt);
        if (targetPromptTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetPromptTokens),
                targetPromptTokens,
                "目标提示长度必须为正数。");
        }

        var targetChars = (int)Math.Ceiling(targetPromptTokens * CharsPerTokenEstimate);
        var prefix = $"Benchmark probe {salt}. ";
        var builder = new StringBuilder(targetChars + Filler.Length + prefix.Length + 8);
        builder.Append(prefix);
        while (builder.Length - prefix.Length < targetChars)
        {
            if (builder.Length > prefix.Length)
            {
                builder.Append(' ');
            }

            builder.Append(Filler);
        }

        return builder.ToString();
    }
}
