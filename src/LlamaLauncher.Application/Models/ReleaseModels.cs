using LlamaLauncher.Domain;

namespace LlamaLauncher.Application.Models;

/// <summary>一个 GitHub Release 的元数据（只来自官方仓库）。</summary>
public sealed class ReleaseInfo
{
    public required string TagName { get; init; }

    public string? Name { get; init; }

    public bool Prerelease { get; init; }

    public DateTimeOffset PublishedAtUtc { get; init; }

    public string? BodyMarkdown { get; init; }

    public string HtmlUrl { get; init; } = string.Empty;

    public List<ReleaseAsset> Assets { get; init; } = new();
}

/// <summary>Release 中的一个资产（下载文件）。</summary>
public sealed class ReleaseAsset
{
    public required string Name { get; init; }

    public long? SizeBytes { get; init; }

    public required string DownloadUrl { get; init; }

    /// <summary>GitHub 提供的官方 digest（"sha256:..."），可能为空。</summary>
    public string? Sha256Digest { get; init; }

    public string? BrowserUrl { get; init; }
}

/// <summary>Release 查询结果。</summary>
public sealed record ReleaseFeedResult(
    bool Success,
    IReadOnlyList<ReleaseInfo> Releases,
    string? Error,
    DateTimeOffset FetchedUtc)
{
    /// <summary>数据是否来自本地缓存（未与 GitHub 实时核对）。</summary>
    public bool FromCache { get; init; }

    /// <summary>成功但有需要用户知晓的提示（如：频率上限期间显示缓存数据）；无提示时为 null。</summary>
    public string? Warning { get; init; }
}

/// <summary>资产筛选后的候选项（含判定依据，供用户确认）。</summary>
public sealed class AssetCandidate
{
    public required ReleaseAsset Asset { get; init; }

    public required string ReleaseTag { get; init; }

    public BackendKind Backend { get; init; } = BackendKind.Unknown;

    /// <summary>如 "12.4"；无法识别时为 null。</summary>
    public string? CudaVersion { get; init; }

    /// <summary>是否为 CUDA 运行时辅助包（cudart），不是主程序。</summary>
    public bool IsSupportFile { get; init; }

    /// <summary>基于本机硬件与名称的推荐项（仅建议，需用户确认）。</summary>
    public bool Recommended { get; init; }

    /// <summary>展示说明（如“未识别构建类型”）。</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>下载进度（总大小未知时为不确定进度）。</summary>
public sealed record DownloadProgressInfo(
    long BytesReceived,
    long? TotalBytes,
    double BytesPerSecond)
{
    public double? Fraction => TotalBytes is { } total && total > 0
        ? Math.Clamp((double)BytesReceived / total, 0, 1)
        : null;
}

/// <summary>更新检查结果。</summary>
public sealed record UpdateCheckResult(
    bool Success,
    bool UpdateAvailable,
    string? CurrentVersionLabel,
    ReleaseInfo? LatestRelease,
    string? Error,
    DateTimeOffset CheckedUtc)
{
    /// <summary>成功但有提示（如展示的是缓存数据）；无提示时为 null。</summary>
    public string? Warning { get; init; }
}
