using System.Text.RegularExpressions;
using LlamaLauncher.Application.Capabilities;
using LlamaLauncher.Application.Security;
using LlamaLauncher.Domain;
using LlamaLauncher.Domain.Validation;

namespace LlamaLauncher.Application.CommandLine;

/// <summary>命令行构建请求。</summary>
public sealed class CommandLineBuildRequest
{
    public required ServerLaunchOptions Options { get; init; }

    /// <summary>能力快照；为空表示尚未探测，此时使用候选 flag 并给出警告。</summary>
    public ServerCapabilities? Capabilities { get; init; }

    /// <summary>已从密钥存储解析出的 API Key（仅用于构建，不持久化、不写日志）。</summary>
    public string? ResolvedApiKey { get; init; }

    public bool AllowExtraArgsOverrideManaged { get; init; }

    public string? ExtraArguments { get; init; }

    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }
}

/// <summary>命令行构建结果（argv tokens + 脱敏预览 + 问题列表）。</summary>
public sealed class CommandLineBuildResult
{
    public List<string> Tokens { get; } = new();

    public List<string> MaskedTokens { get; } = new();

    public List<ValidationIssue> Issues { get; } = new();

    public List<string> UsedCanonicalOptions { get; } = new();

    public string MaskedPreview { get; internal set; } = string.Empty;

    public bool HasErrors => Issues.Any(i => i.IsBlocking);
}

/// <summary>
/// 由结构化选项生成 llama-server argv。
/// 铁律：仅生成能力快照确认支持的参数；不支持的关键参数直接报错，绝不静默忽略。
/// </summary>
public static class CommandLineBuilder
{
    private static readonly Regex EnvironmentNamePattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public static CommandLineBuildResult Build(string executablePath, CommandLineBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ctx = new BuildContext(request);

        // 模型路径（必需）
        if (string.IsNullOrWhiteSpace(request.Options.ModelPath))
        {
            ctx.Issues.Add(new ValidationIssue(ValidationSeverity.Error, "model", "未选择模型文件。"));
        }
        else
        {
            EmitValue(ctx, "model", request.Options.ModelPath!);
        }

        // 监听地址与端口（始终显式生成）
        EmitValue(ctx, "host", request.Options.Host);
        EmitValue(ctx, "port", request.Options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // 常规数值参数
        if (request.Options.ContextSize is { } ctxSize)
        {
            EmitValue(ctx, "ctx-size", ctxSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (request.Options.Threads is { } threads)
        {
            EmitValue(ctx, "threads", threads.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (request.Options.ThreadsBatch is { } threadsBatch)
        {
            EmitValue(ctx, "threads-batch", threadsBatch.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (request.Options.BatchSize is { } batchSize)
        {
            EmitValue(ctx, "batch-size", batchSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (request.Options.UBatchSize is { } ubatchSize)
        {
            EmitValue(ctx, "ubatch-size", ubatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // GPU 层数（含 auto/all 取值能力判断）
        if (!string.IsNullOrWhiteSpace(request.Options.GpuLayers))
        {
            var layers = request.Options.GpuLayers!.Trim();
            if (layers.Equals("auto", StringComparison.OrdinalIgnoreCase)
                || layers.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                var supported = FindSupported(ctx, "n-gpu-layers");
                if (supported is not null && !HintAllowsGpuLayersAuto(supported))
                {
                    ctx.Issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        nameof(ServerLaunchOptions.GpuLayers),
                        $"当前 llama.cpp 版本不支持 GPU 层数取值“{layers}”。",
                        "请改为具体整数层数。"));
                }
                else
                {
                    EmitValue(ctx, "n-gpu-layers", layers.ToLowerInvariant());
                }
            }
            else
            {
                EmitValue(ctx, "n-gpu-layers", layers);
            }
        }

        // 设备与多 GPU
        foreach (var device in request.Options.Devices.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            EmitValue(ctx, "device", device.Trim());
        }

        if (!string.IsNullOrWhiteSpace(request.Options.SplitMode))
        {
            EmitValue(ctx, "split-mode", request.Options.SplitMode!.Trim().ToLowerInvariant());
        }

        if (!string.IsNullOrWhiteSpace(request.Options.TensorSplit))
        {
            EmitValue(ctx, "tensor-split", request.Options.TensorSplit!.Trim());
        }

        if (request.Options.MainGpu is { } mainGpu)
        {
            EmitValue(ctx, "main-gpu", mainGpu.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // KV cache 类型
        if (!string.IsNullOrWhiteSpace(request.Options.CacheTypeK))
        {
            EmitValue(ctx, "cache-type-k", request.Options.CacheTypeK!.Trim().ToLowerInvariant());
        }

        if (!string.IsNullOrWhiteSpace(request.Options.CacheTypeV))
        {
            EmitValue(ctx, "cache-type-v", request.Options.CacheTypeV!.Trim().ToLowerInvariant());
        }

        // Flash Attention
        EmitFlashAttention(ctx, request.Options);

        // 别名与槽位
        if (!string.IsNullOrWhiteSpace(request.Options.Alias))
        {
            EmitValue(ctx, "alias", request.Options.Alias!);
        }

        if (request.Options.ParallelSlots is { } slots)
        {
            EmitValue(ctx, "parallel", slots.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // 连续批处理
        if (request.Options.ContinuousBatching is { } contBatching)
        {
            if (contBatching)
            {
                EmitFlag(ctx, "cont-batching", ValidationSeverity.Warning);
            }
            else
            {
                EmitFlag(ctx, "no-cont-batching", ValidationSeverity.Warning);
            }
        }

        // 日志与内存映射（非关键：不支持时警告并跳过）
        if (request.Options.VerboseLogging)
        {
            EmitFlag(ctx, "verbose", ValidationSeverity.Warning);
        }

        if (request.Options.NoMemoryMap)
        {
            EmitFlag(ctx, "no-mmap", ValidationSeverity.Warning);
        }

        // API Key（敏感；预览脱敏）
        if (request.Options.ApiKeyEnabled)
        {
            var supported = FindSupported(ctx, "api-key");
            if (supported is null && ctx.Capabilities is not null)
            {
                ctx.Issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    nameof(ServerLaunchOptions.ApiKeyEnabled),
                    "当前 llama.cpp 版本不支持 --api-key 参数，无法启用 API Key。"));
            }
            else if (string.IsNullOrEmpty(request.ResolvedApiKey))
            {
                ctx.Issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "ApiKey",
                    "已启用 API Key，但密钥存储中没有可用密钥。",
                    "请在设置中配置 API Key 后重试。"));
            }
            else
            {
                EmitValue(ctx, "api-key", request.ResolvedApiKey!, sensitive: true);
            }
        }

        // 环境变量校验
        foreach (var (name, _) in request.EnvironmentVariables ?? new Dictionary<string, string>())
        {
            if (!EnvironmentNamePattern.IsMatch(name))
            {
                ctx.Issues.Add(new ValidationIssue(ValidationSeverity.Error, "Environment", $"环境变量名称无效：{name}"));
            }
            else if (!LlamaOptionCatalog.KnownEnvironmentVariables.Contains(name))
            {
                ctx.Issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning,
                    "Environment",
                    $"环境变量 {name} 不在允许列表内，将原样传递给服务进程。"));
            }
        }

        // 高级额外参数
        AppendExtraArguments(ctx, request.ExtraArguments, request.AllowExtraArgsOverrideManaged);

        var result = new CommandLineBuildResult();
        result.Tokens.AddRange(ctx.Tokens);
        result.MaskedTokens.AddRange(ctx.MaskedTokens);
        result.Issues.AddRange(ctx.Issues);
        result.UsedCanonicalOptions.AddRange(ctx.UsedCanonicalOptions);
        result.MaskedPreview = WindowsCommandLine.BuildPreview(executablePath, ctx.MaskedTokens);
        return result;
    }

    private static void EmitFlashAttention(BuildContext ctx, ServerLaunchOptions options)
    {
        if (options.FlashAttention is not { } mode)
        {
            return;
        }

        if (ctx.Capabilities is null)
        {
            ctx.Issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                nameof(ServerLaunchOptions.FlashAttention),
                "尚未探测该版本的能力快照，Flash Attention 设置未生成（请先执行能力探测）。"));
            return;
        }

        var supported = FindSupported(ctx, "flash-attn");
        if (supported is null)
        {
            return; // FindSupported 已记录错误/警告
        }

        if (supported.ValueKind == OptionValueKind.None)
        {
            switch (mode)
            {
                case FlashAttentionMode.On:
                    EmitFlag(ctx, "flash-attn", ValidationSeverity.Error);
                    break;
                case FlashAttentionMode.Auto:
                    ctx.Issues.Add(new ValidationIssue(
                        ValidationSeverity.Warning,
                        nameof(ServerLaunchOptions.FlashAttention),
                        "该版本 --flash-attn 为开关形式，auto 与 on 等效。"));
                    EmitFlag(ctx, "flash-attn", ValidationSeverity.Error);
                    break;
                case FlashAttentionMode.Off:
                    ctx.Issues.Add(new ValidationIssue(
                        ValidationSeverity.Warning,
                        nameof(ServerLaunchOptions.FlashAttention),
                        "该版本不支持显式关闭 Flash Attention，已忽略该设置。"));
                    break;
            }

            return;
        }

        var value = mode switch
        {
            FlashAttentionMode.On => "on",
            FlashAttentionMode.Off => "off",
            _ => "auto",
        };

        var hint = supported.ValueHint;
        if (!string.IsNullOrEmpty(hint)
            && hint.Contains('|')
            && !hint.Contains(value, StringComparison.OrdinalIgnoreCase))
        {
            ctx.Issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                nameof(ServerLaunchOptions.FlashAttention),
                $"当前版本 Flash Attention 取值不支持“{value}”（帮助：{hint}）。"));
            return;
        }

        EmitValue(ctx, "flash-attn", value);
    }

    private static bool HintAllowsGpuLayersAuto(SupportedOption option)
    {
        var haystack = (option.ValueHint ?? string.Empty) + " " + (option.Description ?? string.Empty);
        return haystack.Contains("auto", StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("all", StringComparison.OrdinalIgnoreCase);
    }

    private static SupportedOption? FindSupported(BuildContext ctx, string canonicalName)
    {
        if (ctx.Capabilities is null)
        {
            ctx.WarnMissingCapabilitiesOnce();
            return null;
        }

        return ctx.Capabilities.FindOption(canonicalName);
    }

    private static string? ResolveFlag(BuildContext ctx, string canonicalName, ValidationSeverity missingSeverity)
    {
        var definition = LlamaOptionCatalog.FindByCanonicalName(canonicalName);
        if (definition is null)
        {
            return null;
        }

        if (ctx.Capabilities is null)
        {
            ctx.WarnMissingCapabilitiesOnce();
            return definition.Flags[0];
        }

        var supported = ctx.Capabilities.FindOption(canonicalName);
        if (supported is null)
        {
            if (missingSeverity == ValidationSeverity.Error)
            {
                ctx.Issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    canonicalName,
                    $"当前 llama.cpp 版本不支持参数 {definition.Flags[0]}（能力快照未包含，禁止静默忽略）。",
                    "请在参数面板中移除该设置，或改用该版本支持的参数形式。"));
            }
            else
            {
                ctx.Issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning,
                    canonicalName,
                    $"当前 llama.cpp 版本不支持参数 {definition.Flags[0]}，已跳过该设置。"));
            }

            return null;
        }

        foreach (var flag in definition.Flags)
        {
            if (supported.Flags.Contains(flag, StringComparer.OrdinalIgnoreCase))
            {
                return flag;
            }
        }

        return supported.Flags.FirstOrDefault();
    }

    private static void EmitValue(BuildContext ctx, string canonicalName, string value, bool sensitive = false)
    {
        var flag = ResolveFlag(ctx, canonicalName, ValidationSeverity.Error);
        if (flag is null)
        {
            return;
        }

        ctx.Tokens.Add(flag);
        ctx.Tokens.Add(value);
        ctx.MaskedTokens.Add(flag);
        ctx.MaskedTokens.Add(sensitive ? SecretMasker.Mask : value);
        ctx.UsedCanonicalOptions.Add(canonicalName);
    }

    private static void EmitFlag(BuildContext ctx, string canonicalName, ValidationSeverity missingSeverity)
    {
        var flag = ResolveFlag(ctx, canonicalName, missingSeverity);
        if (flag is null)
        {
            return;
        }

        ctx.Tokens.Add(flag);
        ctx.MaskedTokens.Add(flag);
        ctx.UsedCanonicalOptions.Add(canonicalName);
    }

    private static void AppendExtraArguments(BuildContext ctx, string? extraArguments, bool allowOverride)
    {
        var tokens = WindowsArgumentParser.Parse(extraArguments);
        if (tokens.Count == 0)
        {
            return;
        }

        var emittedFlags = ctx.Tokens
            .Where(t => t.StartsWith("-", StringComparison.Ordinal))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<string>();
        var unknown = new List<string>();

        foreach (var token in tokens)
        {
            if (!token.StartsWith("-", StringComparison.Ordinal) || token == "-" || token == "--")
            {
                continue;
            }

            var flagName = token.Split('=', 2)[0];
            if (LlamaOptionCatalog.ManagedFlags.Contains(flagName) || emittedFlags.Contains(flagName))
            {
                conflicts.Add(flagName);
            }
            else if (ctx.Capabilities is not null && !ctx.Capabilities.HelpMentionsFlag(flagName))
            {
                unknown.Add(flagName);
            }
        }

        if (conflicts.Count > 0)
        {
            var list = string.Join(", ", conflicts.Distinct(StringComparer.OrdinalIgnoreCase));
            if (allowOverride)
            {
                ctx.Issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning,
                    "ExtraArguments",
                    $"高级参数覆盖了 UI 管理的参数（{list}）：执行时以高级参数为准。"));
            }
            else
            {
                ctx.Issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "ExtraArguments",
                    $"高级参数与参数面板冲突：{list}。",
                    "请移除冲突参数，或在配置中启用“允许高级参数覆盖”。"));
            }
        }

        if (unknown.Count > 0)
        {
            ctx.Issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "ExtraArguments",
                $"高级参数未在当前版本帮助中确认：{string.Join(", ", unknown.Distinct(StringComparer.OrdinalIgnoreCase))}。若启动失败请检查该参数。"));
        }

        ctx.Tokens.AddRange(tokens);
        ctx.MaskedTokens.AddRange(tokens.Select(t =>
            LlamaOptionCatalog.ManagedFlags.Contains(t.Split('=', 2)[0]) && t.Contains('=')
                ? t.Split('=', 2)[0] + "=" + SecretMasker.Mask
                : t));
    }

    private sealed class BuildContext
    {
        private bool _warnedMissingCapabilities;

        public BuildContext(CommandLineBuildRequest request)
        {
            Capabilities = request.Capabilities;
        }

        public ServerCapabilities? Capabilities { get; }

        public List<string> Tokens { get; } = new();

        public List<string> MaskedTokens { get; } = new();

        public List<ValidationIssue> Issues { get; } = new();

        public List<string> UsedCanonicalOptions { get; } = new();

        public void WarnMissingCapabilitiesOnce()
        {
            if (_warnedMissingCapabilities)
            {
                return;
            }

            _warnedMissingCapabilities = true;
            Issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "Capabilities",
                "尚未探测该 llama.cpp 程序的能力快照，无法验证参数是否受支持。",
                "请先在“硬件与后端”或“版本管理”页面对该程序执行能力探测。"));
        }
    }
}
