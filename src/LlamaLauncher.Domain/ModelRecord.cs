namespace LlamaLauncher.Domain;

/// <summary>已登记的本地 GGUF 模型记录。</summary>
public sealed class ModelRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>调用方提供的原始路径。</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>规范化后的绝对路径（用于去重与失效检测，不区分大小写）。</summary>
    public string CanonicalPath { get; set; } = string.Empty;

    public long? FileSizeBytes { get; set; }

    public DateTimeOffset? FileLastWriteUtc { get; set; }

    public ModelFileStatus Status { get; set; } = ModelFileStatus.Unknown;

    public MetadataParseStatus MetadataStatus { get; set; } = MetadataParseStatus.NotParsed;

    public GgufModelInfo? Metadata { get; set; }

    public string? MetadataError { get; set; }

    /// <summary>展示别名（用户可选设置）。</summary>
    public string? Alias { get; set; }

    public List<string> Tags { get; set; } = new();

    public string? Notes { get; set; }

    public Guid? DefaultProfileId { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public DateTimeOffset? LastCheckedUtc { get; set; }

    /// <summary>用于 UI 展示的名称：别名优先。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Name : Alias!;
}
