namespace LlamaLauncher.Domain;

/// <summary>启动配置档案：模型引用 + llama.cpp 安装引用 + 结构化选项。</summary>
public sealed class LaunchProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public Guid? ModelId { get; set; }

    /// <summary>模型引用快照（导入/导出与历史展示用）。</summary>
    public ModelReferenceSnapshot? ModelSnapshot { get; set; }

    public Guid? InstallationId { get; set; }

    public InstallationReferenceSnapshot? InstallationSnapshot { get; set; }

    public ServerLaunchOptions Options { get; set; } = new();

    /// <summary>高级额外参数原文（按 Windows 解析规则拆分为 token）。</summary>
    public string? ExtraArguments { get; set; }

    /// <summary>允许高级参数覆盖 UI 管理的关键参数（默认 false：冲突则阻止）。</summary>
    public bool AllowExtraArgsOverrideManaged { get; set; }

    /// <summary>附加环境变量（名称 → 值）。</summary>
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsDefault { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>模型引用快照。</summary>
public sealed class ModelReferenceSnapshot
{
    public Guid? ModelId { get; set; }

    public string? Name { get; set; }

    public string? FilePath { get; set; }

    /// <summary>相对路径（相对导出文件所在目录）或绝对路径；导入时按此语义解析。</summary>
    public bool PathIsRelative { get; set; }
}

/// <summary>llama.cpp 安装引用快照。</summary>
public sealed class InstallationReferenceSnapshot
{
    public Guid? InstallationId { get; set; }

    public string? VersionLabel { get; set; }

    public string? ExecutablePath { get; set; }

    public bool PathIsRelative { get; set; }
}
