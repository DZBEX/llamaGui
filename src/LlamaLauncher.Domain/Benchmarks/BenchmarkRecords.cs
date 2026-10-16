namespace LlamaLauncher.Domain.Benchmarks;

/// <summary>基准测试运行状态。</summary>
public enum BenchmarkRunState
{
    /// <summary>运行中（进行中的记录；异常退出后不会保留该状态）。</summary>
    Running = 0,

    /// <summary>已完成（允许包含部分失败样本或提前中止说明）。</summary>
    Succeeded = 1,

    /// <summary>失败（没有任何可用的成功样本）。</summary>
    Failed = 2,

    /// <summary>已取消（保留已完成的样本）。</summary>
    Canceled = 3,
}

/// <summary>基准测试配置（随运行记录一起保存，保证可复现与可比）。</summary>
public sealed class BenchmarkOptions
{
    /// <summary>提示长度档位（目标 token 数；实际以服务端统计为准）。</summary>
    public List<int> PromptTokens { get; set; } = new() { 512, 2048 };

    /// <summary>每次请求生成的最大 token 数。</summary>
    public int GenerateTokens { get; set; } = 128;

    /// <summary>每个档位的正式重复次数（不含预热）。</summary>
    public int Repetitions { get; set; } = 3;

    /// <summary>每个档位的预热次数（不计入统计）。</summary>
    public int WarmupRuns { get; set; } = 1;

    /// <summary>样本之间的冷却等待（秒）。</summary>
    public int CooldownSeconds { get; set; }

    /// <summary>复制一份（用于记录快照，避免调用方后续修改）。</summary>
    public BenchmarkOptions Clone() => new()
    {
        PromptTokens = new List<int>(PromptTokens),
        GenerateTokens = GenerateTokens,
        Repetitions = Repetitions,
        WarmupRuns = WarmupRuns,
        CooldownSeconds = CooldownSeconds,
    };
}

/// <summary>单个样本（一次 /completion 调用的测量结果）。</summary>
public sealed class BenchmarkSample
{
    /// <summary>目标提示长度（token，近似值；生成为唯一表示）。</summary>
    public int TargetPromptTokens { get; set; }

    /// <summary>正式重复序号（1 起；预热样本为 0）。</summary>
    public int Repetition { get; set; }

    /// <summary>是否为预热样本（不计入统计）。</summary>
    public bool IsWarmup { get; set; }

    /// <summary>服务端实际评估的提示 token 数。</summary>
    public int? PromptTokens { get; set; }

    /// <summary>提示处理耗时（毫秒，服务端统计）。</summary>
    public double? PromptMs { get; set; }

    /// <summary>提示处理速度（token/秒）。</summary>
    public double? PromptTokensPerSecond { get; set; }

    /// <summary>服务端实际生成的 token 数。</summary>
    public int? GeneratedTokens { get; set; }

    /// <summary>生成耗时（毫秒，服务端统计）。</summary>
    public double? GeneratedMs { get; set; }

    /// <summary>生成速度（token/秒）。</summary>
    public double? GeneratedTokensPerSecond { get; set; }

    /// <summary>首 token 延迟（毫秒，客户端实测；失败样本为 null）。</summary>
    public double? TimeToFirstTokenMs { get; set; }

    /// <summary>单次请求总耗时（毫秒，客户端实测）。</summary>
    public double? TotalMs { get; set; }

    /// <summary>失败原因（null 表示成功）。</summary>
    public string? Error { get; set; }

    /// <summary>是否成功。</summary>
    public bool Succeeded => Error is null;
}

/// <summary>一次基准测试运行的完整记录（配置快照 + 样本明细）。</summary>
public sealed class BenchmarkRunRecord
{
    /// <summary>运行 ID（唯一）。</summary>
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>目标实例 ID。</summary>
    public Guid InstanceId { get; set; }

    /// <summary>实例名称快照。</summary>
    public string InstanceNameSnapshot { get; set; } = string.Empty;

    /// <summary>模型文件名快照（可空）。</summary>
    public string? ModelNameSnapshot { get; set; }

    /// <summary>启动配置名称快照（可空）。</summary>
    public string? ProfileNameSnapshot { get; set; }

    /// <summary>llama.cpp 版本标签快照（可空）。</summary>
    public string? VersionLabel { get; set; }

    /// <summary>执行时的应用版本。</summary>
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>开始时间（UTC）。</summary>
    public DateTimeOffset StartedUtc { get; set; }

    /// <summary>结束时间（UTC）。</summary>
    public DateTimeOffset? EndedUtc { get; set; }

    /// <summary>总耗时（毫秒）。</summary>
    public double? DurationMs { get; set; }

    /// <summary>最终状态。</summary>
    public BenchmarkRunState State { get; set; } = BenchmarkRunState.Running;

    /// <summary>运行配置快照。</summary>
    public BenchmarkOptions Options { get; set; } = new();

    /// <summary>样本明细（按执行顺序）。</summary>
    public List<BenchmarkSample> Samples { get; set; } = new();

    /// <summary>错误摘要（失败原因 / 提前中止说明 / 取消说明）。</summary>
    public string? ErrorSummary { get; set; }
}

/// <summary>历史列表用的运行摘要（不加载样本明细）。</summary>
public sealed class BenchmarkRunSummary
{
    public string RunId { get; set; } = string.Empty;

    public Guid InstanceId { get; set; }

    public string InstanceNameSnapshot { get; set; } = string.Empty;

    public string? ModelNameSnapshot { get; set; }

    public string? VersionLabel { get; set; }

    public DateTimeOffset StartedUtc { get; set; }

    public DateTimeOffset? EndedUtc { get; set; }

    public double? DurationMs { get; set; }

    public BenchmarkRunState State { get; set; }

    public string? ErrorSummary { get; set; }
}
