using System.Diagnostics;
using System.Security.Cryptography;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Models;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.Updates;

// 流式下载：边下边写盘 + 增量 SHA-256，不把整个压缩包载入内存。
// 取消或失败时清理临时文件，绝不影响当前默认/正在使用的版本。

/// <summary>基于 HttpClient 的流式下载服务。</summary>
public sealed class HttpDownloadService : IDownloadService, IDisposable
{
    private const long MaxAllowedBytes = 8L * 1024 * 1024 * 1024; // 8 GiB 上限

    private readonly HttpClient _client;
    private readonly ILogger<HttpDownloadService> _logger;

    public HttpDownloadService(ILogger<HttpDownloadService> logger)
    {
        _logger = logger;
        _client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(20) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("LlamaLauncher/0.9");
    }

    public async Task<DownloadResult> DownloadAsync(
        string url,
        string destinationPath,
        long? expectedSizeBytes,
        IProgress<DownloadProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fullDestination = Path.GetFullPath(destinationPath);
        var fullTemp = fullDestination + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new DownloadResult(
                    false,
                    null,
                    0,
                    null,
                    DownloadErrorKind.HttpStatus,
                    $"下载失败：HTTP {(int)response.StatusCode}。");
            }

            var total = response.Content.Headers.ContentLength ?? expectedSizeBytes;
            if (total is > MaxAllowedBytes)
            {
                return new DownloadResult(
                    false,
                    null,
                    0,
                    null,
                    DownloadErrorKind.TooLarge,
                    $"文件大小（{total} 字节）超出允许上限。");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = new FileStream(
                fullTemp,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous);

            using var sha256 = SHA256.Create();
            var buffer = new byte[128 * 1024];
            long received = 0;
            var stopwatch = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;

            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                received += read;
                if (received > MaxAllowedBytes)
                {
                    throw new InvalidDataException("下载内容超出允许上限。");
                }

                sha256.TransformBlock(buffer, 0, read, null, 0);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                if (stopwatch.Elapsed - lastReport > TimeSpan.FromMilliseconds(250))
                {
                    lastReport = stopwatch.Elapsed;
                    var speed = stopwatch.Elapsed.TotalSeconds > 0.1
                        ? received / stopwatch.Elapsed.TotalSeconds
                        : 0;
                    progress?.Report(new DownloadProgressInfo(received, total, speed));
                }
            }

            sha256.TransformFinalBlock(buffer, 0, 0);
            var hash = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            await destination.DisposeAsync().ConfigureAwait(false);

            if (total is { } knownSize && knownSize > 0 && received != knownSize)
            {
                TryDelete(fullTemp);
                return new DownloadResult(
                    false,
                    null,
                    received,
                    null,
                    DownloadErrorKind.InvalidContent,
                    $"下载不完整：期望 {knownSize} 字节，实际 {received} 字节。");
            }

            if (received < 1024 && LooksLikeHtmlError(fullTemp))
            {
                TryDelete(fullTemp);
                return new DownloadResult(
                    false,
                    null,
                    received,
                    null,
                    DownloadErrorKind.InvalidContent,
                    "下载内容看起来是网页而非压缩包（可能被网关或代理拦截）。");
            }

            File.Move(fullTemp, fullDestination, overwrite: true);
            progress?.Report(new DownloadProgressInfo(
                received,
                total,
                received / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001)));
            _logger.LogInformation(
                "下载完成：{Url} → {Path}（{Bytes} 字节，SHA-256 {Hash}）",
                url, fullDestination, received, hash);
            return new DownloadResult(true, fullDestination, received, hash, DownloadErrorKind.None, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(fullTemp);
            return new DownloadResult(false, null, 0, null, DownloadErrorKind.Cancelled, "下载已取消。");
        }
        catch (OperationCanceledException)
        {
            TryDelete(fullTemp);
            return new DownloadResult(false, null, 0, null, DownloadErrorKind.Network, "下载超时。");
        }
        catch (HttpRequestException ex)
        {
            TryDelete(fullTemp);
            return new DownloadResult(false, null, 0, null, DownloadErrorKind.Network, "网络错误：" + ex.Message);
        }
        catch (IOException ex)
        {
            TryDelete(fullTemp);
            // HttpClient 在读满 Content-Length 之前连接被关闭（响应被截断）
            if (ex.Message.Contains("premature", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("ended", StringComparison.OrdinalIgnoreCase))
            {
                return new DownloadResult(
                    false, null, 0, null, DownloadErrorKind.InvalidContent,
                    "下载被中断（响应在完成前结束）。");
            }

            return new DownloadResult(false, null, 0, null, DownloadErrorKind.Disk, "写入文件失败：" + ex.Message);
        }
        catch (InvalidDataException ex)
        {
            TryDelete(fullTemp);
            return new DownloadResult(false, null, 0, null, DownloadErrorKind.TooLarge, ex.Message);
        }
    }

    public void Dispose() => _client.Dispose();

    private static bool LooksLikeHtmlError(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> head = stackalloc byte[64];
            var read = stream.Read(head);
            var text = System.Text.Encoding.ASCII.GetString(head[..read]).TrimStart();
            return text.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }
}
