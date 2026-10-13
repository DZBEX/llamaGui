namespace LlamaLauncher.Domain;

/// <summary>启动 llama-server 的结构化选项。未设置的项不生成命令行参数。</summary>
public sealed class ServerLaunchOptions
{
    /// <summary>模型文件绝对路径（启动时由配置引用的模型解析而来）。</summary>
    public string? ModelPath { get; set; }

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 8080;

    /// <summary>上下文长度 --ctx-size；null 使用程序默认值。</summary>
    public int? ContextSize { get; set; }

    /// <summary>CPU 线程数 --threads；null 使用程序默认值。</summary>
    public int? Threads { get; set; }

    /// <summary>批处理线程数 --threads-batch。</summary>
    public int? ThreadsBatch { get; set; }

    /// <summary>逻辑批大小 --batch-size。</summary>
    public int? BatchSize { get; set; }

    /// <summary>物理批大小 --ubatch-size。</summary>
    public int? UBatchSize { get; set; }

    /// <summary>GPU 层数 --n-gpu-layers；取值 "auto" / "all" / 非负整数（按当前版本的帮助文本校验取值形式）。</summary>
    public string? GpuLayers { get; set; }

    /// <summary>设备选择 --device（可多个）。</summary>
    public List<string> Devices { get; set; } = new();

    /// <summary>分割模式 --split-mode（none/layer/row）。</summary>
    public string? SplitMode { get; set; }

    /// <summary>张量分割 --tensor-split（如 "3,1"）。</summary>
    public string? TensorSplit { get; set; }

    /// <summary>主 GPU --main-gpu。</summary>
    public int? MainGpu { get; set; }

    /// <summary>KV cache K 类型 --cache-type-k。</summary>
    public string? CacheTypeK { get; set; }

    /// <summary>KV cache V 类型 --cache-type-v。</summary>
    public string? CacheTypeV { get; set; }

    /// <summary>Flash Attention 模式；null 表示不显式设置。</summary>
    public FlashAttentionMode? FlashAttention { get; set; }

    /// <summary>模型别名 --alias。</summary>
    public string? Alias { get; set; }

    /// <summary>并行槽位 --parallel。</summary>
    public int? ParallelSlots { get; set; }

    /// <summary>连续批处理 --cont-batching / --no-cont-batching；null 不显式设置。</summary>
    public bool? ContinuousBatching { get; set; }

    /// <summary>详细日志 --verbose。</summary>
    public bool VerboseLogging { get; set; }

    /// <summary>禁用内存映射 --no-mmap。</summary>
    public bool NoMemoryMap { get; set; }

    /// <summary>是否传入 API Key（密钥本体由 ISecretStore 提供，不存入配置档案）。</summary>
    public bool ApiKeyEnabled { get; set; }

    /// <summary>密钥存储中的秘密名（而非密钥本体）。</summary>
    public string? ApiKeySecretName { get; set; }

    /// <summary>启动健康检查总超时（秒）。</summary>
    public int StartupTimeoutSeconds { get; set; } = 600;

    /// <summary>停止宽限期（秒）。</summary>
    public int StopGraceSeconds { get; set; } = 5;
}

/// <summary>Flash Attention 显式模式。</summary>
public enum FlashAttentionMode
{
    Off = 0,
    On = 1,
    Auto = 2,
}
