namespace LlamaLauncher.Domain;

/// <summary>一次对 llama-server.exe 的完整探测结果（能力快照）。</summary>
public sealed class ServerCapabilities
{
    public string? ExecutablePath { get; set; }

    /// <summary>可执行文件指纹（大小 + 最后写入时间的组合标识），用于缓存失效判断。</summary>
    public string? ExecutableStamp { get; set; }

    public string? Sha256 { get; set; }

    public string? VersionText { get; set; }

    public string? HelpText { get; set; }

    public string? ListDevicesText { get; set; }

    public ProbeOutcome Outcome { get; set; } = ProbeOutcome.Unknown;

    public DateTimeOffset ProbedUtc { get; set; }

    public List<SupportedOption> Options { get; set; } = new();

    public List<DetectedDevice> Devices { get; set; } = new();

    public List<ProbeResult> ProbeResults { get; set; } = new();

    public List<string> Warnings { get; set; } = new();

    /// <summary>按规范化名称查找参数（同时匹配别名 flag）。</summary>
    public SupportedOption? FindOption(string canonicalName) =>
        Options.FirstOrDefault(o => string.Equals(o.CanonicalName, canonicalName, StringComparison.OrdinalIgnoreCase));

    /// <summary>是否支持指定规范化参数。</summary>
    public bool Supports(string canonicalName) => FindOption(canonicalName) is not null;

    /// <summary>帮助文本中是否出现过指定 flag（用于别名确认）。</summary>
    public bool HelpMentionsFlag(string flag) =>
        !string.IsNullOrEmpty(HelpText)
        && HelpText!.Contains(flag, StringComparison.Ordinal);

    /// <summary>--list-devices 输出中探测到的后端集合。</summary>
    public IReadOnlyCollection<BackendKind> DetectedBackends =>
        Devices.Select(d => d.Backend).Distinct().ToArray();
}

/// <summary>探测总体结果。</summary>
public enum ProbeOutcome
{
    Unknown = 0,
    Success = 1,
    PartialFailure = 2,
    Failed = 3,
}

/// <summary>单个探测命令的结果（保留原始输出便于诊断）。</summary>
public sealed class ProbeResult
{
    public string Command { get; set; } = string.Empty;

    public int? ExitCode { get; set; }

    public string? StandardOutput { get; set; }

    public string? StandardError { get; set; }

    public bool TimedOut { get; set; }

    public long DurationMs { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset StartedUtc { get; set; }

    public bool Succeeded => !TimedOut && ErrorMessage is null && ExitCode == 0;
}

/// <summary>--help 中解析出的受支持参数。</summary>
public sealed class SupportedOption
{
    /// <summary>规范化名称（如 ctx-size、n-gpu-layers）。</summary>
    public string CanonicalName { get; set; } = string.Empty;

    /// <summary>帮助文本中出现的全部写法（如 ["--ctx-size","-c"]）。</summary>
    public List<string> Flags { get; set; } = new();

    public OptionValueKind ValueKind { get; set; } = OptionValueKind.None;

    /// <summary>帮助文本中的取值提示（如 N、[on|off|auto]）。</summary>
    public string? ValueHint { get; set; }

    public string? Description { get; set; }
}

/// <summary>参数取值形式。</summary>
public enum OptionValueKind
{
    None = 0,
    Required = 1,
    Optional = 2,
}

/// <summary>--list-devices 中列出的设备。</summary>
public sealed class DetectedDevice
{
    public int? Index { get; set; }

    public string Name { get; set; } = string.Empty;

    public BackendKind Backend { get; set; } = BackendKind.Unknown;

    public string? RawLine { get; set; }
}
