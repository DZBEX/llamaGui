using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Models;
using LlamaLauncher.Application.Updates;
using LlamaLauncher.Domain;
using LlamaLauncher.IntegrationTests.TestSupport;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Updates;

public sealed class LlamaUpdateServiceTests
{
    private sealed class FakeReleaseFeed : IReleaseFeedService
    {
        public List<ReleaseInfo> Releases { get; } = new();

        public string? Error { get; set; }

        public string? Warning { get; set; }

        public bool FromCache { get; set; }

        public Task<ReleaseFeedResult> GetReleasesAsync(
            UpdateChannel channel,
            int maxReleases = 30,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Error is null
                ? new ReleaseFeedResult(true, Releases, null, DateTimeOffset.UtcNow)
                {
                    Warning = Warning,
                    FromCache = FromCache,
                }
                : new ReleaseFeedResult(false, Array.Empty<ReleaseInfo>(), Error, DateTimeOffset.UtcNow)
                {
                    Warning = Warning,
                });
    }

    private sealed class FakeDownloadService : IDownloadService
    {
        private readonly Dictionary<string, string> _urlToFile = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _cancelUrls = new(StringComparer.OrdinalIgnoreCase);

        public void Map(string url, string filePath) => _urlToFile[url] = filePath;

        public void MarkCancelled(string url) => _cancelUrls.Add(url);

        public async Task<DownloadResult> DownloadAsync(
            string url,
            string destinationPath,
            long? expectedSizeBytes,
            IProgress<DownloadProgressInfo>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (_cancelUrls.Contains(url))
            {
                return new DownloadResult(false, null, 0, null, DownloadErrorKind.Cancelled, "下载已取消。");
            }

            if (!_urlToFile.TryGetValue(url, out var source) || !File.Exists(source))
            {
                return new DownloadResult(false, null, 0, null, DownloadErrorKind.Network, "未映射的 URL。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(source, destinationPath, overwrite: true);
            var bytes = await File.ReadAllBytesAsync(destinationPath, cancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            progress?.Report(new DownloadProgressInfo(bytes.Length, bytes.Length, bytes.Length));
            return new DownloadResult(true, destinationPath, bytes.Length, hash, DownloadErrorKind.None, null);
        }
    }

    private static byte[] CreateZip(
        TempDirectory directory,
        string executableContent,
        string zipName,
        bool copyRealChildProcess = false)
    {
        var path = directory.Combine(zipName);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("llama-server.exe");
            using (var entryStream = entry.Open())
            {
                if (copyRealChildProcess)
                {
                    entryStream.Write(File.ReadAllBytes(IntegrationFixture.ChildExecutablePath));
                }
                else
                {
                    entryStream.Write(Encoding.UTF8.GetBytes(executableContent));
                }
            }

            if (copyRealChildProcess)
            {
                // 补齐 .NET 应用程序运行所需的相邻文件（apphost + dll + 配置）
                var baseDirectory = Path.GetDirectoryName(IntegrationFixture.ChildExecutablePath)!;
                foreach (var companion in new[]
                         {
                             "LlamaLauncher.TestChildProcess.dll",
                             "LlamaLauncher.TestChildProcess.runtimeconfig.json",
                             "LlamaLauncher.TestChildProcess.deps.json",
                         })
                {
                    var source = Path.Combine(baseDirectory, companion);
                    if (!File.Exists(source))
                    {
                        continue;
                    }

                    var companionEntry = archive.CreateEntry(companion);
                    using var companionStream = companionEntry.Open();
                    companionStream.Write(File.ReadAllBytes(source));
                }
            }

            var dll = archive.CreateEntry("llama-common.dll");
            using (var dllStream = dll.Open())
            {
                dllStream.Write("fake-dll"u8.ToArray());
            }
        }

        return File.ReadAllBytes(path);
    }

    private static ReleaseInfo CreateRelease(string tag, string assetName, string url, string? digest)
        => new()
        {
            TagName = tag,
            PublishedAtUtc = DateTimeOffset.UtcNow,
            Assets =
            {
                new ReleaseAsset
                {
                    Name = assetName,
                    DownloadUrl = url,
                    SizeBytes = null,
                    Sha256Digest = digest,
                },
            },
        };

    /// <summary>把测试子进程（含运行所需的 dll/配置）释放为某个版本目录里的 llama-server.exe。</summary>
    private static void CopyChildProcessInto(string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        File.Copy(
            IntegrationFixture.ChildExecutablePath,
            Path.Combine(targetDirectory, "llama-server.exe"),
            overwrite: true);
        var baseDirectory = Path.GetDirectoryName(IntegrationFixture.ChildExecutablePath)!;
        foreach (var companion in new[]
                 {
                     "LlamaLauncher.TestChildProcess.dll",
                     "LlamaLauncher.TestChildProcess.runtimeconfig.json",
                     "LlamaLauncher.TestChildProcess.deps.json",
                 })
        {
            var source = Path.Combine(baseDirectory, companion);
            if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(targetDirectory, companion), overwrite: true);
            }
        }
    }

    [Fact]
    public async Task FullFlow_InstallsProbesAndBecomesDefault()
    {
        using var fixture = new IntegrationFixture();
        using var directory = new TempDirectory();
        var zipBytes = CreateZip(directory, string.Empty, "b1000.zip", copyRealChildProcess: true);
        var zipPath = directory.Combine("b1000.zip");
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();
        const string url = "https://github.com/ggml-org/llama.cpp/releases/download/b1000/llama-b1000-bin-win-cpu-x64.zip";

        var feed = new FakeReleaseFeed();
        var release = CreateRelease("b1000", "llama-b1000-bin-win-cpu-x64.zip", url, digest);
        feed.Releases.Add(release);
        var downloads = new FakeDownloadService();
        downloads.Map(url, zipPath);

        var service = fixture.CreateUpdateService(feed, downloads);
        var installation = await service.DownloadAndInstallAsync(release, release.Assets[0]);

        Assert.Equal("b1000", installation.VersionLabel);
        Assert.True(installation.IsDefault);
        Assert.True(File.Exists(installation.ExecutablePath));
        Assert.True(installation.Sha256FromOfficialDigest);
        Assert.Equal(InstallationSourceKind.OfficialRelease, installation.SourceKind);
        Assert.Contains("4321", installation.BuildInfo);

        // staging 目录已清理，版本目录位于 runtimes 之下
        Assert.Empty(Directory.GetDirectories(fixture.Paths.RuntimesDirectory, ".staging-*"));
        Assert.StartsWith(
            Path.GetFullPath(fixture.Paths.RuntimesDirectory),
            Path.GetFullPath(installation.Directory),
            StringComparison.OrdinalIgnoreCase);

        // 清单指针已写入
        var manifest = fixture.ManifestStore.Load();
        Assert.Equal(installation.Id, manifest.DefaultInstallationId);

        // 审计记录：最近一次为 Active
        var history = await service.GetHistoryAsync();
        Assert.Equal(UpdateStage.Active, history[0].Stage);
    }

    [Fact]
    public async Task DigestMismatch_FailsWithoutRegistering()
    {
        using var fixture = new IntegrationFixture();
        using var directory = new TempDirectory();
        var zipBytes = CreateZip(directory, string.Empty, "b1001.zip", copyRealChildProcess: true);
        var zipPath = directory.Combine("b1001.zip");
        const string url = "https://github.com/ggml-org/llama.cpp/releases/download/b1001/asset.zip";

        var feed = new FakeReleaseFeed();
        var release = CreateRelease("b1001", "asset.zip", url, "sha256:" + new string('0', 64));
        feed.Releases.Add(release);
        var downloads = new FakeDownloadService();
        downloads.Map(url, zipPath);

        var service = fixture.CreateUpdateService(feed, downloads);
        var exception = await Assert.ThrowsAsync<UpdateException>(
            () => service.DownloadAndInstallAsync(release, release.Assets[0]));

        Assert.Contains("SHA-256", exception.Message);
        Assert.Empty(await fixture.InstallationsRepo.GetAllAsync());
        Assert.Empty(Directory.GetDirectories(fixture.Paths.RuntimesDirectory, ".staging-*"));
        _ = zipBytes;
    }

    [Fact]
    public async Task ProbeFailure_IsNotRegistered()
    {
        using var fixture = new IntegrationFixture();
        using var directory = new TempDirectory();
        CreateZip(directory, "这不是一个可执行文件", "b1002.zip");
        var zipPath = directory.Combine("b1002.zip");
        const string url = "https://github.com/ggml-org/llama.cpp/releases/download/b1002/asset.zip";

        var feed = new FakeReleaseFeed();
        var release = CreateRelease("b1002", "asset.zip", url, null);
        var downloads = new FakeDownloadService();
        downloads.Map(url, zipPath);

        var service = fixture.CreateUpdateService(feed, downloads);
        var exception = await Assert.ThrowsAsync<UpdateException>(
            () => service.DownloadAndInstallAsync(release, release.Assets[0]));

        Assert.Contains("能力验证失败", exception.Message);
        Assert.Empty(await fixture.InstallationsRepo.GetAllAsync());
        Assert.Empty(Directory.GetDirectories(fixture.Paths.RuntimesDirectory, ".staging-*"));
    }

    [Fact]
    public async Task NonOfficialDownloadHost_IsRejected()
    {
        using var fixture = new IntegrationFixture();
        var feed = new FakeReleaseFeed();
        var release = CreateRelease("b1003", "asset.zip", "https://evil.example.com/asset.zip", null);
        var service = fixture.CreateUpdateService(feed, new FakeDownloadService());

        var exception = await Assert.ThrowsAsync<UpdateException>(
            () => service.DownloadAndInstallAsync(release, release.Assets[0]));

        Assert.Contains("官方", exception.Message);
    }

    private static string CreateCudartZip(TempDirectory directory, string zipName, params (string Entry, string Content)[] entries)
    {
        var path = directory.Combine(zipName);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            var zipEntry = archive.CreateEntry(entry);
            using var entryStream = zipEntry.Open();
            entryStream.Write(Encoding.UTF8.GetBytes(content));
        }

        return path;
    }

    [Fact]
    public async Task RuntimePack_CopiesDllsIntoInstallationDirectory()
    {
        using var fixture = new IntegrationFixture();
        using var directory = new TempDirectory();

        var mainZip = CreateZip(directory, string.Empty, "main-cuda.zip", copyRealChildProcess: true);
        var cudartPath = CreateCudartZip(
            directory,
            "cudart-cu12.4.zip",
            ("cudart64_12.dll", "fake-cudart"),
            ("lib/cublas64_12.dll", "fake-cublas"),
            ("README.txt", "not a dll"));
        var cudartBytes = File.ReadAllBytes(cudartPath);

        const string mainUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b3000/llama-b3000-bin-win-cu12.4-x64.zip";
        const string packUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b3000/cudart-llama-bin-win-cu12.4-x64.zip";

        var release = new ReleaseInfo { TagName = "b3000", PublishedAtUtc = DateTimeOffset.UtcNow };
        release.Assets.Add(new ReleaseAsset
        {
            Name = "llama-b3000-bin-win-cu12.4-x64.zip",
            DownloadUrl = mainUrl,
            Sha256Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(mainZip)).ToLowerInvariant(),
        });
        release.Assets.Add(new ReleaseAsset
        {
            Name = "cudart-llama-bin-win-cu12.4-x64.zip",
            DownloadUrl = packUrl,
            SizeBytes = cudartBytes.Length,
            Sha256Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(cudartBytes)).ToLowerInvariant(),
        });

        var feed = new FakeReleaseFeed();
        feed.Releases.Add(release);
        var downloads = new FakeDownloadService();
        downloads.Map(mainUrl, directory.Combine("main-cuda.zip"));
        downloads.Map(packUrl, cudartPath);
        var service = fixture.CreateUpdateService(feed, downloads);

        var installation = await service.DownloadAndInstallAsync(release, release.Assets[0]);
        var pack = AssetSelector.FindCudartForMainAsset(release, release.Assets[0]);
        Assert.NotNull(pack);

        var result = await service.InstallRuntimePackAsync(release, pack!.Asset, installation);

        Assert.Equal(2, result.DllCount);
        var executableDirectory = Path.GetDirectoryName(installation.ExecutablePath)!;
        Assert.True(File.Exists(Path.Combine(executableDirectory, "cudart64_12.dll")));
        Assert.True(File.Exists(Path.Combine(executableDirectory, "cublas64_12.dll")), "子目录中的 DLL 应被扁平化到可执行文件目录。");
        Assert.False(File.Exists(Path.Combine(executableDirectory, "README.txt")));
        Assert.Empty(Directory.GetDirectories(fixture.Paths.RuntimesDirectory, ".staging-*"));
        Assert.True(File.Exists(installation.ExecutablePath), "主程序与版本目录不应受影响。");

        var history = await service.GetHistoryAsync();
        var packOperation = history.First(h => h.Kind == UpdateOperationKind.RuntimePack);
        Assert.Equal(UpdateStage.Active, packOperation.Stage);
        Assert.True(packOperation.Sha256Verified);
    }

    [Fact]
    public async Task RuntimePack_WithoutDlls_FailsAndKeepsVersionIntact()
    {
        using var fixture = new IntegrationFixture();
        using var directory = new TempDirectory();

        var mainZip = CreateZip(directory, string.Empty, "main-cuda2.zip", copyRealChildProcess: true);
        var emptyPack = CreateCudartZip(directory, "cudart-empty.zip", ("README.txt", "no dlls"));

        const string mainUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b3001/llama-b3001-bin-win-cu12.4-x64.zip";
        const string packUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b3001/cudart-llama-bin-win-cu12.4-x64.zip";

        var release = new ReleaseInfo { TagName = "b3001", PublishedAtUtc = DateTimeOffset.UtcNow };
        release.Assets.Add(new ReleaseAsset
        {
            Name = "llama-b3001-bin-win-cu12.4-x64.zip",
            DownloadUrl = mainUrl,
            Sha256Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(mainZip)).ToLowerInvariant(),
        });
        release.Assets.Add(new ReleaseAsset
        {
            Name = "cudart-llama-bin-win-cu12.4-x64.zip",
            DownloadUrl = packUrl,
        });

        var feed = new FakeReleaseFeed();
        feed.Releases.Add(release);
        var downloads = new FakeDownloadService();
        downloads.Map(mainUrl, directory.Combine("main-cuda2.zip"));
        downloads.Map(packUrl, emptyPack);
        var service = fixture.CreateUpdateService(feed, downloads);

        var installation = await service.DownloadAndInstallAsync(release, release.Assets[0]);

        var exception = await Assert.ThrowsAsync<UpdateException>(
            () => service.InstallRuntimePackAsync(release, release.Assets[1], installation));
        Assert.Contains("DLL", exception.Message);
        Assert.True(File.Exists(installation.ExecutablePath));
        Assert.Empty(Directory.GetDirectories(fixture.Paths.RuntimesDirectory, ".staging-*"));

        var history = await service.GetHistoryAsync();
        Assert.Contains(history, h => h.Kind == UpdateOperationKind.RuntimePack && h.Stage == UpdateStage.Failed);
    }

    [Fact]
    public async Task SwitchDefaultThenRollback_RestoresPrevious()
    {
        using var fixture = new IntegrationFixture();
        using var directory = new TempDirectory();

        var firstZip = CreateZip(directory, string.Empty, "v1.zip", copyRealChildProcess: true);
        var secondZip = CreateZip(directory, string.Empty, "v2.zip", copyRealChildProcess: true);
        const string url1 = "https://github.com/ggml-org/llama.cpp/releases/download/b2001/v1.zip";
        const string url2 = "https://github.com/ggml-org/llama.cpp/releases/download/b2002/v2.zip";

        var feed = new FakeReleaseFeed();
        var release1 = CreateRelease(
            "b2001",
            "v1.zip",
            url1,
            "sha256:" + Convert.ToHexString(SHA256.HashData(firstZip)).ToLowerInvariant());
        var release2 = CreateRelease(
            "b2002",
            "v2.zip",
            url2,
            "sha256:" + Convert.ToHexString(SHA256.HashData(secondZip)).ToLowerInvariant());
        feed.Releases.Add(release1);
        feed.Releases.Add(release2);

        var downloads = new FakeDownloadService();
        downloads.Map(url1, directory.Combine("v1.zip"));
        downloads.Map(url2, directory.Combine("v2.zip"));
        var service = fixture.CreateUpdateService(feed, downloads);

        var installation1 = await service.DownloadAndInstallAsync(release1, release1.Assets[0]);
        var installation2 = await service.DownloadAndInstallAsync(release2, release2.Assets[0]);
        Assert.True(installation1.IsDefault);
        Assert.False(installation2.IsDefault);

        await service.SetDefaultAsync(installation2.Id);
        var defaultNow = await fixture.InstallationsRepo.GetDefaultAsync();
        Assert.Equal(installation2.Id, defaultNow!.Id);
        Assert.Equal(installation1.Id, fixture.ManifestStore.Load().PreviousDefaultInstallationId);

        var rollback = await service.RollbackAsync();
        Assert.True(rollback.Success, rollback.Message);
        var afterRollback = await fixture.InstallationsRepo.GetDefaultAsync();
        Assert.Equal(installation1.Id, afterRollback!.Id);

        var history = await service.GetHistoryAsync();
        Assert.Contains(history, h => h.Stage == UpdateStage.RolledBack && h.RolledBack);
    }

    [Fact]
    public async Task DeleteInstallation_BlocksWhenReferencedByProfile()
    {
        using var fixture = new IntegrationFixture();
        using var directory = new TempDirectory();
        var zipBytes = CreateZip(directory, string.Empty, "v3.zip", copyRealChildProcess: true);
        const string url = "https://github.com/ggml-org/llama.cpp/releases/download/b2003/v3.zip";
        var feed = new FakeReleaseFeed();
        var release = CreateRelease(
            "b2003",
            "v3.zip",
            url,
            "sha256:" + Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant());
        feed.Releases.Add(release);
        var downloads = new FakeDownloadService();
        downloads.Map(url, directory.Combine("v3.zip"));
        downloads.Map(
            "https://github.com/ggml-org/llama.cpp/releases/download/b2004/v4.zip",
            directory.Combine("v3.zip"));
        var service = fixture.CreateUpdateService(feed, downloads);

        var installation1 = await service.DownloadAndInstallAsync(release, release.Assets[0]);
        var release2 = CreateRelease(
            "b2004",
            "v4.zip",
            "https://github.com/ggml-org/llama.cpp/releases/download/b2004/v4.zip",
            null);
        var installation2 = await service.DownloadAndInstallAsync(release2, release2.Assets[0]);
        _ = installation1;

        var now = fixture.TimeProvider.GetUtcNow();
        var profile = new LaunchProfile
        {
            Name = "引用配置",
            InstallationId = installation2.Id,
            Options = new ServerLaunchOptions { Port = 8080 },
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        await fixture.Profiles.UpsertAsync(profile);

        var exception = await Assert.ThrowsAsync<UpdateException>(
            () => service.DeleteInstallationAsync(installation2.Id, deleteFiles: true));
        Assert.Contains("配置档案", exception.Message);

        await fixture.Profiles.DeleteAsync(profile.Id);
        await service.DeleteInstallationAsync(installation2.Id, deleteFiles: true);
        Assert.Null(await fixture.InstallationsRepo.GetByIdAsync(installation2.Id));
        Assert.False(Directory.Exists(installation2.Directory));
    }

    [Fact]
    public async Task ImportLocalAsset_RegistersManualInstallation()
    {
        using var fixture = new IntegrationFixture();
        using var directory = new TempDirectory();
        CreateZip(directory, string.Empty, "llama-b3000-bin-win-cpu-x64.zip", copyRealChildProcess: true);
        var zipPath = directory.Combine("llama-b3000-bin-win-cpu-x64.zip");
        var service = fixture.CreateUpdateService(new FakeReleaseFeed(), new FakeDownloadService());

        var installation = await service.ImportLocalAssetAsync(zipPath, isOfficialSource: false, "测试来源");

        Assert.Equal("b3000", installation.VersionLabel);
        Assert.Equal(InstallationSourceKind.ManualImport, installation.SourceKind);
        Assert.Contains("非官方来源", installation.Notes);
        Assert.True(File.Exists(installation.ExecutablePath));
    }

    [Fact]
    public async Task CheckForUpdates_WithoutInstalledVersion_ReportsAvailable()
    {
        using var fixture = new IntegrationFixture();
        var feed = new FakeReleaseFeed();
        feed.Releases.Add(CreateRelease(
            "b4000",
            "llama-b4000-bin-win-cpu-x64.zip",
            "https://github.com/ggml-org/llama.cpp/releases/download/b4000/llama-b4000-bin-win-cpu-x64.zip",
            null));
        var service = fixture.CreateUpdateService(feed, new FakeDownloadService());

        var result = await service.CheckForUpdatesAsync(UpdateChannel.Stable);

        Assert.True(result.Success);
        Assert.True(result.UpdateAvailable);
        Assert.Null(result.CurrentVersionLabel);
        Assert.Equal("b4000", result.LatestRelease!.TagName);
    }

    [Fact]
    public async Task CheckForUpdates_MatchingVersion_ReportsNoUpdate()
    {
        using var fixture = new IntegrationFixture();
        using var directory = new TempDirectory();
        var zipBytes = CreateZip(directory, string.Empty, "v5.zip", copyRealChildProcess: true);
        const string url = "https://github.com/ggml-org/llama.cpp/releases/download/b5000/v5.zip";
        var feed = new FakeReleaseFeed();
        var release = CreateRelease(
            "b5000",
            "llama-b5000-bin-win-cpu-x64.zip",
            url,
            "sha256:" + Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant());
        feed.Releases.Add(release);
        var downloads = new FakeDownloadService();
        downloads.Map(url, directory.Combine("v5.zip"));
        var service = fixture.CreateUpdateService(feed, downloads);
        await service.DownloadAndInstallAsync(release, release.Assets[0]);

        var result = await service.CheckForUpdatesAsync(UpdateChannel.Stable);

        Assert.True(result.Success);
        Assert.False(result.UpdateAvailable);
        Assert.Equal("b5000", result.CurrentVersionLabel);
    }

    [Fact]
    public async Task CheckForUpdates_PropagatesCacheFallbackWarning()
    {
        using var fixture = new IntegrationFixture();
        const string warning = "GitHub 请求频率已达上限，当前显示 2026-10-17 10:00 获取的缓存数据（限额约 10:30 恢复（约 30 分钟））。";
        var feed = new FakeReleaseFeed
        {
            Warning = warning,
            FromCache = true,
        };
        feed.Releases.Add(CreateRelease(
            "b4000",
            "llama-b4000-bin-win-cpu-x64.zip",
            "https://github.com/ggml-org/llama.cpp/releases/download/b4000/llama-b4000-bin-win-cpu-x64.zip",
            null));
        var service = fixture.CreateUpdateService(feed, new FakeDownloadService());

        var result = await service.CheckForUpdatesAsync(UpdateChannel.Stable);

        Assert.True(result.Success);
        Assert.True(result.UpdateAvailable);
        Assert.Equal(warning, result.Warning);
    }

    [Fact]
    public async Task DiscoverLocalVersions_RegistersFolders_IsIdempotent_AndBecomesDefault()
    {
        using var fixture = new IntegrationFixture();
        try
        {
            // 模拟用户手动拷入的版本目录：runtimes\llama.cpp\<标签>-<指纹>\llama-server.exe
            var versionDirectory = Path.Combine(fixture.Paths.RuntimesDirectory, "b9999-deadbeef");
            CopyChildProcessInto(versionDirectory);
            Directory.CreateDirectory(Path.Combine(fixture.Paths.RuntimesDirectory, "dlls-only-no-exe"));

            var service = fixture.CreateUpdateService(new FakeReleaseFeed(), new FakeDownloadService());

            var discovered = await service.DiscoverLocalVersionsAsync();

            var installation = Assert.Single(discovered);
            // 托管目录命名（b9999-…）优先于 --version 文本中的标签
            Assert.Equal("b9999", installation.VersionLabel);
            Assert.Equal(InstallationStatus.Available, installation.Status);
            Assert.Equal(
                Path.Combine(versionDirectory, "llama-server.exe"),
                installation.ExecutablePath);
            Assert.True(installation.IsDefault, "无默认版本时首个可用版本应自动设为默认。");

            // 写回数据库（标签覆盖已持久化）
            var persisted = Assert.Single(await fixture.InstallationsRepo.GetAllAsync());
            Assert.Equal("b9999", persisted.VersionLabel);
            Assert.True(persisted.IsDefault);

            // 镜像文件同步
            var manifest = fixture.ManifestStore.Load();
            Assert.Contains(manifest.Entries, e => e.Id == installation.Id && e.VersionLabel == "b9999");

            // 幂等：再次扫描不重复登记；无 exe 的目录被忽略
            Assert.Empty(await service.DiscoverLocalVersionsAsync());
            Assert.Single(await fixture.InstallationsRepo.GetAllAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LLAMALAUNCHER_TEST_VERSION", null);
        }
    }

    [Fact]
    public async Task DiscoverLocalVersions_UsesFolderNameWhenProbeHasNoVersion_AndKeepsExistingDefault()
    {
        using var fixture = new IntegrationFixture();
        try
        {
            // 已有默认版本（外部登记）：发现流程不得改默认
            var seed = await fixture.InstallationService.RegisterExternalAsync(IntegrationFixture.ChildExecutablePath);
            await fixture.InstallationsRepo.SetDefaultAsync(seed.Id);

            // 探测输出为空白 → 解析不到版本标签，应回退为目录名
            fixture.SetEnvironment("LLAMALAUNCHER_TEST_VERSION", "   ");
            var versionDirectory = Path.Combine(fixture.Paths.RuntimesDirectory, "my-build");
            CopyChildProcessInto(versionDirectory);

            var service = fixture.CreateUpdateService(new FakeReleaseFeed(), new FakeDownloadService());

            var discovered = await service.DiscoverLocalVersionsAsync();

            var installation = Assert.Single(discovered);
            Assert.Equal("my-build", installation.VersionLabel);
            Assert.False(installation.IsDefault);
            var currentDefault = await fixture.InstallationsRepo.GetDefaultAsync();
            Assert.Equal(seed.Id, currentDefault!.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LLAMALAUNCHER_TEST_VERSION", null);
        }
    }

    [Fact]
    public async Task DiscoverLocalVersions_UsesCustomRuntimesDirectory()
    {
        using var fixture = new IntegrationFixture();
        var customRuntimes = fixture.Directory.Combine("custom-runtimes");
        fixture.Paths.ApplyDirectoryOverrides(customRuntimes, null);

        var versionDirectory = Path.Combine(customRuntimes, "b8888-cafebabe");
        CopyChildProcessInto(versionDirectory);
        // 默认目录里的版本不应再被扫描（覆盖已生效）
        CopyChildProcessInto(Path.Combine(fixture.Paths.DefaultRuntimesDirectory, "b7777-aa"));

        var service = fixture.CreateUpdateService(new FakeReleaseFeed(), new FakeDownloadService());

        var discovered = await service.DiscoverLocalVersionsAsync();

        var installation = Assert.Single(discovered);
        Assert.Equal("b8888", installation.VersionLabel);
        Assert.StartsWith(
            Path.GetFullPath(customRuntimes),
            installation.ExecutablePath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoverLocalVersions_ReplacesMissingDefaultWithDiscoveredVersion()
    {
        using var fixture = new IntegrationFixture();
        var stale = new LlamaCppInstallation
        {
            Id = Guid.NewGuid(),
            VersionLabel = "b11702",
            Directory = @"D:\OldComputer\runtimes\llama.cpp\b11702-deadbeef",
            ExecutablePath = @"D:\OldComputer\runtimes\llama.cpp\b11702-deadbeef\llama-server.exe",
            SourceKind = InstallationSourceKind.ExternalPath,
            Status = InstallationStatus.Missing,
            IsDefault = true,
            InstalledUtc = fixture.TimeProvider.GetUtcNow(),
        };
        await fixture.InstallationsRepo.UpsertAsync(stale);

        var versionDirectory = Path.Combine(fixture.Paths.RuntimesDirectory, "b9998-cafebabe");
        CopyChildProcessInto(versionDirectory);
        var service = fixture.CreateUpdateService(new FakeReleaseFeed(), new FakeDownloadService());

        var discovered = await service.DiscoverLocalVersionsAsync();

        var installation = Assert.Single(discovered);
        Assert.Equal(InstallationStatus.Available, installation.Status);
        var currentDefault = await fixture.InstallationsRepo.GetDefaultAsync();
        Assert.Equal(installation.Id, currentDefault!.Id);
        Assert.False((await fixture.InstallationsRepo.GetByIdAsync(stale.Id))!.IsDefault);
    }

    [Fact]
    public async Task DeleteInstallation_HandlesMovedRuntimesDirectory()
    {
        using var fixture = new IntegrationFixture();

        // 在默认版本目录里放一个“此前安装”的版本
        var defaultVersionDirectory = Path.Combine(fixture.Paths.DefaultRuntimesDirectory, "b6666-abcdef");
        CopyChildProcessInto(defaultVersionDirectory);
        var installation = new LlamaCppInstallation
        {
            Id = Guid.NewGuid(),
            VersionLabel = "b6666",
            Directory = defaultVersionDirectory,
            ExecutablePath = Path.Combine(defaultVersionDirectory, "llama-server.exe"),
            SourceKind = InstallationSourceKind.OfficialRelease,
            Status = InstallationStatus.Available,
            InstalledUtc = fixture.TimeProvider.GetUtcNow(),
        };
        await fixture.InstallationsRepo.UpsertAsync(installation);

        // 用户随后把版本目录改到其他位置
        fixture.Paths.ApplyDirectoryOverrides(fixture.Directory.Combine("moved-runtimes"), null);
        var service = fixture.CreateUpdateService(new FakeReleaseFeed(), new FakeDownloadService());

        // 旧默认目录下的版本仍可连同文件一起删除
        await service.DeleteInstallationAsync(installation.Id, deleteFiles: true);

        Assert.False(Directory.Exists(defaultVersionDirectory));
        Assert.Null(await fixture.InstallationsRepo.GetByIdAsync(installation.Id));

        // 两个版本根之外的路径依旧拒绝删除文件（记录移除、文件保留）
        var outsideDirectory = fixture.Directory.Combine("outside-runtimes");
        CopyChildProcessInto(outsideDirectory);
        var outsideInstallation = new LlamaCppInstallation
        {
            Id = Guid.NewGuid(),
            VersionLabel = "b5555",
            Directory = outsideDirectory,
            ExecutablePath = Path.Combine(outsideDirectory, "llama-server.exe"),
            SourceKind = InstallationSourceKind.OfficialRelease,
            Status = InstallationStatus.Available,
            InstalledUtc = fixture.TimeProvider.GetUtcNow(),
        };
        await fixture.InstallationsRepo.UpsertAsync(outsideInstallation);

        await Assert.ThrowsAsync<UpdateException>(
            () => service.DeleteInstallationAsync(outsideInstallation.Id, deleteFiles: true));
        Assert.True(Directory.Exists(outsideDirectory));
    }
}
