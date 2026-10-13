using System.Globalization;

namespace LlamaLauncher.Domain.Validation;

/// <summary>启动前可获得的运行环境信息（由 Application 层收集后传入）。</summary>
public sealed class LaunchValidationContext
{
    /// <summary>API Key 是否已在密钥存储中配置。</summary>
    public bool ApiKeyConfigured { get; init; }

    /// <summary>用户是否已对局域网监听完成风险确认。</summary>
    public bool LanAccessConfirmed { get; init; }

    /// <summary>端口预检查是否发现占用。</summary>
    public bool PortInUse { get; init; }

    public int ProcessorCount { get; init; } = Environment.ProcessorCount;

    public string? ModelFilePath { get; init; }

    public string? ExecutablePath { get; init; }

    public long? ModelFileSizeBytes { get; init; }

    public long? TotalMemoryBytes { get; init; }

    public long? AvailableDiskBytes { get; init; }
}

/// <summary>启动参数的值域校验（不含能力快照校验，能力校验在 Application 层结合命令行构建完成）。</summary>
public static class LaunchOptionsValidator
{
    private static readonly string[] KnownCacheTypes =
    {
        "f32", "f16", "bf16", "q8_0", "q4_0", "q4_1", "q5_0", "q5_1", "iq4_nl",
    };

    /// <summary>校验值域与安全约束。返回全部问题（含警告）。</summary>
    public static IReadOnlyList<ValidationIssue> ValidateValues(
        ServerLaunchOptions options,
        LaunchValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);

        var issues = new List<ValidationIssue>();

        // 模型与程序路径
        if (string.IsNullOrWhiteSpace(context.ModelFilePath))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, nameof(options.ModelPath), "未选择模型文件。"));
        }

        if (string.IsNullOrWhiteSpace(context.ExecutablePath))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "ExecutablePath", "未选择 llama-server.exe 程序路径。"));
        }

        // 监听地址与安全
        var scope = HostAddress.Classify(options.Host);
        if (scope == AddressScope.Invalid)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, nameof(options.Host), $"监听地址“{options.Host}”无效。"));
        }
        else if (scope == AddressScope.Public)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                nameof(options.Host),
                "不允许将服务直接暴露到公网地址。",
                "请使用 127.0.0.1 或局域网内网地址。"));
        }
        else if (HostAddress.IsNonLocal(scope))
        {
            if (!options.ApiKeyEnabled)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    nameof(options.ApiKeyEnabled),
                    "局域网监听必须先启用 API Key。",
                    "当前 llama.cpp 版本的 --api-key 参数支持情况需先探测。"));
            }

            if (!context.ApiKeyConfigured)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "ApiKey",
                    "局域网监听需要先配置有效 API Key。"));
            }

            if (!context.LanAccessConfirmed)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "LanAccess",
                    "局域网监听属于高风险操作，需要显式确认后才能启动。"));
            }

            issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                nameof(options.Host),
                $"服务将监听“{options.Host}”，同网段设备可访问。",
                "API Key 不提供传输加密，请勿在不可信网络中使用明文 HTTP。"));
        }

        // 端口
        if (options.Port is < 1 or > 65535)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, nameof(options.Port), "端口必须在 1-65535 之间。"));
        }
        else if (context.PortInUse)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                nameof(options.Port),
                $"端口 {options.Port} 已被占用。",
                "请更换端口；启动器不会抢占其他进程的端口。"));
        }

        // 上下文
        if (options.ContextSize is { } ctx)
        {
            if (ctx < 1)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, nameof(options.ContextSize), "上下文长度必须大于 0。"));
            }
            else if (ctx > 1_048_576)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning,
                    nameof(options.ContextSize),
                    $"上下文长度 {ctx} 非常大，可能需要大量内存/显存。"));
            }
        }

        // 线程
        if (options.Threads is { } threads)
        {
            if (threads < 1)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, nameof(options.Threads), "CPU 线程数必须大于 0。"));
            }
            else if (threads > context.ProcessorCount * 2)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning,
                    nameof(options.Threads),
                    $"线程数 {threads} 超过逻辑处理器数量的 2 倍（{context.ProcessorCount * 2}），可能降低性能。"));
            }
        }

        if (options.ThreadsBatch is { } threadsBatch && threadsBatch < 1)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, nameof(options.ThreadsBatch), "批处理线程数必须大于 0。"));
        }

        // 批大小
        if (options.BatchSize is { } batch && batch < 1)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, nameof(options.BatchSize), "批大小必须大于 0。"));
        }

        if (options.UBatchSize is { } ubatch && ubatch < 1)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, nameof(options.UBatchSize), "物理批大小必须大于 0。"));
        }

        if (options is { BatchSize: { } b, UBatchSize: { } ub } && ub > b)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                nameof(options.UBatchSize),
                $"物理批大小（{ub}）不能大于批大小（{b}）。"));
        }

        // GPU 层数
        if (!string.IsNullOrWhiteSpace(options.GpuLayers))
        {
            var value = options.GpuLayers!.Trim();
            var isAuto = value.Equals("auto", StringComparison.OrdinalIgnoreCase)
                || value.Equals("all", StringComparison.OrdinalIgnoreCase);
            if (!isAuto)
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layers) || layers < 0)
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        nameof(options.GpuLayers),
                        "GPU 层数必须是 0 以上的整数，或当前版本支持时使用 auto/all。"));
                }
            }
        }

        // 设备与分割
        if (options.SplitMode is { } split
            && !split.Equals("none", StringComparison.OrdinalIgnoreCase)
            && !split.Equals("layer", StringComparison.OrdinalIgnoreCase)
            && !split.Equals("row", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, nameof(options.SplitMode), "分割模式必须是 none、layer 或 row。"));
        }

        if (!string.IsNullOrWhiteSpace(options.TensorSplit))
        {
            var parts = options.TensorSplit!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var valid = parts.Length > 0 && parts.All(p =>
                double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0);
            if (!valid)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    nameof(options.TensorSplit),
                    "张量分割必须是逗号分隔的正数序列（如 3,1）。"));
            }
        }

        if (options.MainGpu is { } mainGpu && mainGpu < 0)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, nameof(options.MainGpu), "主 GPU 序号不能为负数。"));
        }

        // KV cache 类型
        ValidateCacheType(options.CacheTypeK, nameof(options.CacheTypeK), issues);
        ValidateCacheType(options.CacheTypeV, nameof(options.CacheTypeV), issues);

        // 槽位
        if (options.ParallelSlots is { } slots && slots < 1)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, nameof(options.ParallelSlots), "并行槽位必须大于 0。"));
        }

        // 超时
        if (options.StartupTimeoutSeconds is < 10 or > 86400)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, nameof(options.StartupTimeoutSeconds), "启动超时必须在 10-86400 秒之间。"));
        }

        if (options.StopGraceSeconds is < 0 or > 600)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, nameof(options.StopGraceSeconds), "停止宽限期必须在 0-600 秒之间。"));
        }

        // 设备列表
        foreach (var name in options.Devices.Where(string.IsNullOrWhiteSpace))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, nameof(options.Devices), "设备名称不能为空。"));
            break;
        }

        // 粗略内存风险估算（明确标注为估算）
        if (context is { ModelFileSizeBytes: { } size, TotalMemoryBytes: { } total }
            && options.ContextSize is { } contextSize
            && contextSize > 8192
            && size > total / 2)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                nameof(options.ContextSize),
                "模型文件较大且上下文较长：KV cache 可能占用大量内存/显存（估算值，仅供参考）。",
                "如启动失败可尝试降低上下文长度、批大小或 GPU 层数。"));
        }

        return issues;
    }

    private static void ValidateCacheType(string? value, string field, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (!KnownCacheTypes.Contains(normalized))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                field,
                $"KV cache 类型“{value}”不在内置常见列表内，是否受支持取决于当前 llama.cpp 版本。"));
        }
    }
}
