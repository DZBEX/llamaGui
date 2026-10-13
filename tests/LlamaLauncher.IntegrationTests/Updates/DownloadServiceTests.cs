using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Models;
using LlamaLauncher.IntegrationTests.TestSupport;
using LlamaLauncher.Infrastructure.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Updates;

public sealed class DownloadServiceTests
{
    private static HttpDownloadService CreateService() =>
        new(NullLogger<HttpDownloadService>.Instance);

    [Fact]
    public async Task DownloadsFileWithProgressAndHash()
    {
        using var directory = new TempDirectory();
        var payload = new byte[300_000];
        Random.Shared.NextBytes(payload);
        using var server = new LocalHttpFileServer(payload);
        var destination = directory.Combine("downloads", "asset.zip");
        var progressReports = new List<DownloadProgressInfo>();

        using var service = CreateService();
        var result = await service.DownloadAsync(
            server.Url(),
            destination,
            payload.Length,
            new Progress<DownloadProgressInfo>(progressReports.Add));

        Assert.True(result.Success, result.Error);
        Assert.Equal(payload.Length, result.BytesWritten);
        Assert.True(File.Exists(destination));
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.NotNull(result.Sha256);
        Assert.Equal(64, result.Sha256!.Length);
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task TruncatedDownload_IsRejectedAndTempCleaned()
    {
        using var directory = new TempDirectory();
        var payload = new byte[1024];
        using var server = new LocalHttpFileServer(payload, declareLongerContent: true);
        var destination = directory.Combine("downloads", "asset.zip");

        using var service = CreateService();
        var result = await service.DownloadAsync(server.Url(), destination, null);

        Assert.False(result.Success);
        Assert.Equal(DownloadErrorKind.InvalidContent, result.ErrorKind);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task HttpError_IsReported()
    {
        using var directory = new TempDirectory();
        using var server = new LocalHttpFileServer("not found"u8.ToArray(), status: 404);
        var destination = directory.Combine("downloads", "asset.zip");

        using var service = CreateService();
        var result = await service.DownloadAsync(server.Url(), destination, null);

        Assert.False(result.Success);
        Assert.Equal(DownloadErrorKind.HttpStatus, result.ErrorKind);
    }

    [Fact]
    public async Task HtmlErrorPage_IsDetected()
    {
        using var directory = new TempDirectory();
        using var server = new LocalHttpFileServer(
            "<!DOCTYPE html><html><body>Access denied</body></html>"u8.ToArray(),
            contentType: "text/html");
        var destination = directory.Combine("downloads", "asset.zip");

        using var service = CreateService();
        var result = await service.DownloadAsync(server.Url(), destination, null);

        Assert.False(result.Success);
        Assert.Equal(DownloadErrorKind.InvalidContent, result.ErrorKind);
    }

    [Fact]
    public async Task Cancellation_CleansTempFile()
    {
        using var directory = new TempDirectory();
        var payload = new byte[512_000];
        Random.Shared.NextBytes(payload);
        using var server = new LocalHttpFileServer(payload, chunkDelayMs: 80);
        var destination = directory.Combine("downloads", "asset.zip");
        using var cts = new CancellationTokenSource();

        using var service = CreateService();
        var downloadTask = service.DownloadAsync(server.Url(), destination, null, null, cts.Token);
        await Task.Delay(200);
        cts.Cancel();
        var result = await downloadTask;

        Assert.False(result.Success);
        Assert.Equal(DownloadErrorKind.Cancelled, result.ErrorKind);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task NetworkFailure_IsReported()
    {
        using var directory = new TempDirectory();
        var port = IntegrationFixture.GetFreePort();
        var destination = directory.Combine("downloads", "asset.zip");

        using var service = CreateService();
        var result = await service.DownloadAsync(
            $"http://127.0.0.1:{port}/asset.zip",
            destination,
            null);

        Assert.False(result.Success);
        Assert.Equal(DownloadErrorKind.Network, result.ErrorKind);
    }
}
