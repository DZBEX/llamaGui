namespace LlamaLauncher.Application.Capabilities;

/// <summary>一个受管理的 llama.cpp 命令列参数定义。</summary>
public sealed record LlamaOptionDefinition(
    string CanonicalName,
    string[] Flags,
    string UiLabel,
    string? Description = null,
    bool Checkable = true);

/// <summary>
/// llama.cpp 常见参数的规范化目录：定义规范名、候选 flag（首选在前）与 UI 标签。
/// 目录只用于“识别与映射”，实际是否可用必须以能力快照（--help 探测结果）为准。
/// </summary>
public static class LlamaOptionCatalog
{
    public static readonly IReadOnlyList<LlamaOptionDefinition> All = new List<LlamaOptionDefinition>
    {
        new("help", new[] { "--help", "-h", "--usage" }, "帮助", Checkable: false),
        new("version", new[] { "--version" }, "版本", Checkable: false),
        new("list-devices", new[] { "--list-devices" }, "设备列表", Checkable: false),
        new("model", new[] { "--model", "-m" }, "模型路径"),
        new("host", new[] { "--host" }, "监听地址"),
        new("port", new[] { "--port" }, "端口"),
        new("ctx-size", new[] { "--ctx-size", "-c" }, "上下文长度"),
        new("threads", new[] { "--threads", "-t" }, "CPU 线程"),
        new("threads-batch", new[] { "--threads-batch", "-tb" }, "批处理线程"),
        new("batch-size", new[] { "--batch-size", "-b" }, "批大小"),
        new("ubatch-size", new[] { "--ubatch-size", "-ub" }, "物理批大小"),
        new("n-gpu-layers", new[] { "--n-gpu-layers", "-ngl", "--gpu-layers" }, "GPU 层数"),
        new("device", new[] { "--device", "-dev" }, "设备选择"),
        new("split-mode", new[] { "--split-mode", "-sm" }, "分割模式"),
        new("tensor-split", new[] { "--tensor-split", "-ts" }, "张量分割"),
        new("main-gpu", new[] { "--main-gpu", "-mg" }, "主 GPU"),
        new("cache-type-k", new[] { "--cache-type-k", "-ctk" }, "KV cache K 类型"),
        new("cache-type-v", new[] { "--cache-type-v", "-ctv" }, "KV cache V 类型"),
        new("flash-attn", new[] { "--flash-attn", "-fa" }, "Flash Attention"),
        new("alias", new[] { "--alias", "-a" }, "模型别名"),
        new("parallel", new[] { "--parallel", "-np" }, "并行槽位"),
        new("cont-batching", new[] { "--cont-batching" }, "连续批处理"),
        new("no-cont-batching", new[] { "--no-cont-batching" }, "禁用连续批处理", Checkable: false),
        new("verbose", new[] { "--verbose", "-v" }, "详细日志"),
        new("no-mmap", new[] { "--no-mmap" }, "禁用内存映射"),
        new("api-key", new[] { "--api-key" }, "API Key"),
        new("api-key-file", new[] { "--api-key-file" }, "API Key 文件"),
        new("keep-alive", new[] { "--timeout" }, "请求超时", Checkable: false),
        new("log-verbosity", new[] { "--log-verbosity" }, "日志等级", Checkable: false),
        new("jinja", new[] { "--jinja" }, "启用 Jinja 模板", Checkable: false),
    };

    private static readonly Dictionary<string, LlamaOptionDefinition> ByFlag =
        BuildFlagIndex();

    /// <summary>已知的“由 UI 直接管理”的 flag 集合（高级参数冲突检测用）。</summary>
    public static readonly IReadOnlySet<string> ManagedFlags =
        All.Where(d => d.Checkable)
            .SelectMany(d => d.Flags)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>通过任意已知 flag 找到定义。</summary>
    public static LlamaOptionDefinition? FindByFlag(string flag)
        => ByFlag.TryGetValue(flag, out var definition) ? definition : null;

    public static LlamaOptionDefinition? FindByCanonicalName(string canonicalName)
        => All.FirstOrDefault(d => string.Equals(d.CanonicalName, canonicalName, StringComparison.OrdinalIgnoreCase));

    /// <summary>环境变量允许列表（其余名称给出显式警告）。</summary>
    public static readonly IReadOnlySet<string> KnownEnvironmentVariables =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CUDA_VISIBLE_DEVICES",
            "HIP_VISIBLE_DEVICES",
            "ROCR_VISIBLE_DEVICES",
            "GPU_DEVICE_ORDINAL",
            "GGML_VK_VISIBLE_DEVICES",
            "ONEAPI_DEVICE_SELECTOR",
            "ZES_ENABLE_SYSMAN",
            "OMP_NUM_THREADS",
        };

    private static Dictionary<string, LlamaOptionDefinition> BuildFlagIndex()
    {
        var index = new Dictionary<string, LlamaOptionDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in All)
        {
            foreach (var flag in definition.Flags)
            {
                index[flag] = definition;
            }
        }

        return index;
    }
}
