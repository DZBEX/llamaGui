namespace LlamaLauncher.Domain;

/// <summary>一套 llama.cpp 安装（独立版本目录或用户指定的外部程序）。</summary>
public sealed class LlamaCppInstallation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string VersionLabel { get; set; } = "未知";

    /// <summary>版本命令输出摘要 / 构建信息。</summary>
    public string? BuildInfo { get; set; }

    public string? AssetName { get; set; }

    public string? SourceUrl { get; set; }

    public InstallationSourceKind SourceKind { get; set; } = InstallationSourceKind.ExternalPath;

    public string Directory { get; set; } = string.Empty;

    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>归档包 SHA-256（官方 digest 或本地计算，后者会标注）。</summary>
    public string? Sha256 { get; set; }

    public bool Sha256FromOfficialDigest { get; set; }

    public long? ArchiveSizeBytes { get; set; }

    public InstallationStatus Status { get; set; } = InstallationStatus.Unknown;

    public bool IsDefault { get; set; }

    /// <summary>最近一次能力快照（ServerCapabilities 的 JSON）。</summary>
    public string? CapabilitiesJson { get; set; }

    public DateTimeOffset? LastProbedUtc { get; set; }

    public DateTimeOffset InstalledUtc { get; set; }

    public string? Notes { get; set; }

    public List<BackendKind> BackendTags { get; set; } = new();
}
