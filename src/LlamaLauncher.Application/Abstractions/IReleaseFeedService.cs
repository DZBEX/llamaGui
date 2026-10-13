using LlamaLauncher.Application.Models;
using LlamaLauncher.Domain;

namespace LlamaLauncher.Application.Abstractions;

/// <summary>官方 Releases 元数据查询（仅 ggml-org/llama.cpp）。</summary>
public interface IReleaseFeedService
{
    /// <summary>查询发行列表（稳定/预发布通道）。失败时返回带 Error 的结果，不抛异常。</summary>
    Task<ReleaseFeedResult> GetReleasesAsync(
        UpdateChannel channel,
        int maxReleases = 30,
        CancellationToken cancellationToken = default);
}

/// <summary>下载失败原因分类。</summary>
public enum DownloadErrorKind
{
    None = 0,
    Network = 1,
    HttpStatus = 2,
    Cancelled = 3,
    Disk = 4,
    TooLarge = 5,
    InvalidContent = 6,
}

/// <summary>下载结果（成功时包含本地 SHA-256 指纹）。</summary>
public sealed record DownloadResult(
    bool Success,
    string? FilePath,
    long BytesWritten,
    string? Sha256,
    DownloadErrorKind ErrorKind,
    string? Error);

/// <summary>流式下载服务（不把整个文件载入内存；取消时清理临时文件）。</summary>
public interface IDownloadService
{
    Task<DownloadResult> DownloadAsync(
        string url,
        string destinationPath,
        long? expectedSizeBytes,
        IProgress<DownloadProgressInfo>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>解压结果。</summary>
public sealed record ExtractResult(
    bool Success,
    IReadOnlyList<string> ExtractedFiles,
    string? Error);

/// <summary>ZIP 安全解压（拒绝路径穿越、绝对路径、符号链接与超限展开）。</summary>
public interface IZipExtractor
{
    Task<ExtractResult> ExtractAsync(
        string zipPath,
        string destinationDirectory,
        CancellationToken cancellationToken = default);
}
