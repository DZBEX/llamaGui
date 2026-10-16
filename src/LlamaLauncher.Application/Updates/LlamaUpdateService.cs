using System.Text.Json;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Models;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Application.Updates;

/// <summary>
/// llama.cpp 程序更新编排：下载 → 校验 → 安全解压 → staging 探测 → 注册 → 切换/回滚/清理。
/// 所有阶段写入 UpdateOperations；任一步失败都不会影响当前默认或正在使用的版本。
/// </summary>
public sealed class LlamaUpdateService : ILlamaUpdateService
{
    private static readonly string[] AllowedOfficialDownloadHosts =
    {
        "github.com",
        "objects.githubusercontent.com",
        "github-releases.githubusercontent.com",
        "release-assets.githubusercontent.com",
    };

    private readonly IReleaseFeedService _feed;
    private readonly IDownloadService _downloads;
    private readonly IZipExtractor _extractor;
    private readonly ILlamaBinaryProbeService _probe;
    private readonly IInstallationRepository _installations;
    private readonly ILlamaInstallationService _installationService;
    private readonly IProfileRepository _profiles;
    private readonly IInstanceRepository _instances;
    private readonly IUpdateOperationRepository _operations;
    private readonly IInstallationManifestStore _manifest;
    private readonly IAppPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LlamaUpdateService> _logger;

    public LlamaUpdateService(
        IReleaseFeedService feed,
        IDownloadService downloads,
        IZipExtractor extractor,
        ILlamaBinaryProbeService probe,
        IInstallationRepository installations,
        ILlamaInstallationService installationService,
        IProfileRepository profiles,
        IInstanceRepository instances,
        IUpdateOperationRepository operations,
        IInstallationManifestStore manifest,
        IAppPaths paths,
        TimeProvider timeProvider,
        ILogger<LlamaUpdateService> logger)
    {
        _feed = feed;
        _downloads = downloads;
        _extractor = extractor;
        _probe = probe;
        _installations = installations;
        _installationService = installationService;
        _profiles = profiles;
        _instances = instances;
        _operations = operations;
        _manifest = manifest;
        _paths = paths;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string OfficialReleasesPageUrl => "https://github.com/ggml-org/llama.cpp/releases";

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        UpdateChannel channel,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var current = await _installations.GetDefaultAsync(cancellationToken).ConfigureAwait(false);
        var feed = await _feed.GetReleasesAsync(channel, 30, cancellationToken).ConfigureAwait(false);

        var operation = new UpdateOperation
        {
            Kind = UpdateOperationKind.Check,
            FromVersion = current?.VersionLabel,
            StartedUtc = now,
        };

        if (!feed.Success)
        {
            operation.Stage = UpdateStage.Failed;
            operation.Error = feed.Error;
            operation.CompletedUtc = _timeProvider.GetUtcNow();
            await _operations.AddAsync(operation, cancellationToken).ConfigureAwait(false);
            return new UpdateCheckResult(false, false, current?.VersionLabel, null, feed.Error, now)
            {
                Warning = feed.Warning,
            };
        }

        // 找到最新的、包含可用 Windows x64 资产的发行
        ReleaseInfo? latest = null;
        foreach (var release in feed.Releases.OrderByDescending(r => r.PublishedAtUtc))
        {
            var candidates = AssetSelector.SelectCandidates(release);
            if (candidates.Count > 0)
            {
                latest = release;
                break;
            }
        }

        if (latest is null)
        {
            const string message = "最新发行中没有识别到适用于 Windows x64 的资产。可打开官方发布页手动下载并导入。";
            operation.Stage = UpdateStage.NotAvailable;
            operation.Error = message;
            operation.CompletedUtc = _timeProvider.GetUtcNow();
            await _operations.AddAsync(operation, cancellationToken).ConfigureAwait(false);
            return new UpdateCheckResult(true, false, current?.VersionLabel, null, message, now)
            {
                Warning = feed.Warning,
            };
        }

        var updateAvailable = current is null
            || !string.Equals(current.VersionLabel, latest.TagName, StringComparison.OrdinalIgnoreCase);
        operation.ToVersion = latest.TagName;
        operation.Stage = updateAvailable ? UpdateStage.Available : UpdateStage.NotAvailable;
        operation.CompletedUtc = _timeProvider.GetUtcNow();
        await _operations.AddAsync(operation, cancellationToken).ConfigureAwait(false);

        return new UpdateCheckResult(true, updateAvailable, current?.VersionLabel, latest, null, now)
        {
            Warning = feed.Warning,
        };
    }

    public async Task<LlamaCppInstallation> DownloadAndInstallAsync(
        ReleaseInfo release,
        ReleaseAsset asset,
        IProgress<DownloadProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(asset);

        if (!IsAllowedOfficialDownloadHost(asset.DownloadUrl))
        {
            throw new UpdateException(
                "仅允许从官方 GitHub 发布来源自动下载资产。其他来源请使用“导入本地压缩包”并确认来源。");
        }

        var downloadPath = Path.Combine(_paths.DownloadsDirectory, SanitizeFileName(asset.Name));
        var operation = new UpdateOperation
        {
            Kind = UpdateOperationKind.Install,
            ToVersion = release.TagName,
            AssetName = asset.Name,
            AssetUrl = asset.DownloadUrl,
            AssetSizeBytes = asset.SizeBytes,
            Stage = UpdateStage.Downloading,
            StartedUtc = _timeProvider.GetUtcNow(),
        };
        await _operations.AddAsync(operation, cancellationToken).ConfigureAwait(false);

        try
        {
            var download = await _downloads
                .DownloadAsync(asset.DownloadUrl, downloadPath, asset.SizeBytes, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!download.Success)
            {
                return await FailOperationAsync(
                    operation,
                    download.ErrorKind == DownloadErrorKind.Cancelled ? UpdateStage.Cancelled : UpdateStage.Failed,
                    download.Error ?? "下载失败。",
                    cancellationToken).ConfigureAwait(false);
            }

            operation.Stage = UpdateStage.Verifying;
            operation.Sha256 = download.Sha256;
            var officialDigest = ParseSha256Digest(asset.Sha256Digest);
            if (officialDigest is not null)
            {
                if (!string.Equals(officialDigest, download.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return await FailOperationAsync(
                        operation,
                        UpdateStage.Failed,
                        "SHA-256 校验失败：下载的压缩包与官方 digest 不一致，已拒绝安装（文件保留在下载目录供检查）。",
                        cancellationToken).ConfigureAwait(false);
                }

                operation.Sha256Verified = true;
                operation.Sha256FromOfficialDigest = true;
            }
            else
            {
                // 官方发布未提供 digest 时只记录本地指纹，并明确标注不代表可信来源校验
                operation.Sha256Verified = false;
                operation.Sha256FromOfficialDigest = false;
            }

            await _operations.UpdateAsync(operation, cancellationToken).ConfigureAwait(false);

            var installation = await InstallFromArchiveAsync(
                operation,
                download.FilePath!,
                release.TagName,
                InstallationSourceKind.OfficialRelease,
                asset.Name,
                asset.DownloadUrl,
                download.Sha256,
                operation.Sha256Verified,
                notes: officialDigest is null
                    ? "官方发布未提供 SHA-256 digest；当前记录的是下载后本地计算的指纹（不构成来源验证）。"
                    : null,
                progress,
                cancellationToken).ConfigureAwait(false);

            operation.Stage = UpdateStage.Active;
            operation.InstalledDirectory = installation.Directory;
            operation.CompletedUtc = _timeProvider.GetUtcNow();
            await _operations.UpdateAsync(operation, cancellationToken).ConfigureAwait(false);
            return installation;
        }
        catch (OperationCanceledException)
        {
            operation.Stage = UpdateStage.Cancelled;
            operation.Error = "操作已取消。";
            operation.CompletedUtc = _timeProvider.GetUtcNow();
            await _operations.UpdateAsync(operation, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (UpdateException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "安装 llama.cpp 版本 {Tag} 失败", release.TagName);
            operation.Stage = UpdateStage.Failed;
            operation.Error = ex.Message;
            operation.CompletedUtc = _timeProvider.GetUtcNow();
            await _operations.UpdateAsync(operation, CancellationToken.None).ConfigureAwait(false);
            throw new UpdateException($"安装失败：{ex.Message}", ex);
        }
    }

    public async Task<LlamaCppInstallation> ImportLocalAssetAsync(
        string zipPath,
        bool isOfficialSource,
        string? sourceNote,
        IProgress<DownloadProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            throw new UpdateException($"压缩包不存在：{zipPath}");
        }

        if (!zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("请选择 llama.cpp 的 Windows zip 发行包（.zip）。");
        }

        var fileName = Path.GetFileName(zipPath);
        var operation = new UpdateOperation
        {
            Kind = UpdateOperationKind.Import,
            AssetName = fileName,
            AssetUrl = null,
            Stage = UpdateStage.Verifying,
            StartedUtc = _timeProvider.GetUtcNow(),
        };
        await _operations.AddAsync(operation, cancellationToken).ConfigureAwait(false);

        try
        {
            var installation = await InstallFromArchiveAsync(
                operation,
                zipPath,
                ExtractVersionFromFileName(fileName),
                isOfficialSource ? InstallationSourceKind.OfficialRelease : InstallationSourceKind.ManualImport,
                fileName,
                sourceUrl: null,
                sha256: null,
                sha256Verified: false,
                notes: isOfficialSource
                    ? "由用户手动导入（自述为官方来源），已执行相同的安全检查与能力探测。"
                    : "非官方来源手动导入：" + (string.IsNullOrWhiteSpace(sourceNote) ? "用户自述来源未提供。" : sourceNote) +
                      " 已执行相同的安全检查与能力探测，请自行确认可执行文件可信。",
                progress,
                cancellationToken).ConfigureAwait(false);

            operation.Stage = UpdateStage.Active;
            operation.InstalledDirectory = installation.Directory;
            operation.CompletedUtc = _timeProvider.GetUtcNow();
            await _operations.UpdateAsync(operation, cancellationToken).ConfigureAwait(false);
            return installation;
        }
        catch (OperationCanceledException)
        {
            operation.Stage = UpdateStage.Cancelled;
            operation.CompletedUtc = _timeProvider.GetUtcNow();
            await _operations.UpdateAsync(operation, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (UpdateException)
        {
            throw;
        }
        catch (Exception ex)
        {
            operation.Stage = UpdateStage.Failed;
            operation.Error = ex.Message;
            operation.CompletedUtc = _timeProvider.GetUtcNow();
            await _operations.UpdateAsync(operation, CancellationToken.None).ConfigureAwait(false);
            throw new UpdateException($"导入失败：{ex.Message}", ex);
        }
    }

    public async Task<IReadOnlyList<LlamaCppInstallation>> DiscoverLocalVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        var discovered = new List<LlamaCppInstallation>();
        if (!Directory.Exists(_paths.RuntimesDirectory))
        {
            return discovered;
        }

        var existing = await _installations.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var knownPaths = new HashSet<string>(
            existing.Select(i => Path.GetFullPath(i.ExecutablePath)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var directory in Directory
                     .EnumerateDirectories(_paths.RuntimesDirectory)
                     .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var executablePath = Path.Combine(directory, "llama-server.exe");
            if (!File.Exists(executablePath))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(executablePath);
            if (!knownPaths.Add(fullPath))
            {
                continue;
            }

            try
            {
                // 复用外部登记流程：校验路径 → 能力探测 → 写库
                var installation = await _installationService
                    .RegisterExternalAsync(fullPath, cancellationToken)
                    .ConfigureAwait(false);

                if (ApplyFolderLabelFallback(installation, Path.GetFileName(directory)))
                {
                    await _installations.UpsertAsync(installation, cancellationToken).ConfigureAwait(false);
                }

                discovered.Add(installation);
                _logger.LogInformation(
                    "已发现并登记本地 llama.cpp 版本 {Version}（{Path}）",
                    installation.VersionLabel,
                    fullPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "登记本地 llama.cpp 版本失败：{Path}", fullPath);
            }
        }

        if (discovered.Count == 0)
        {
            return discovered;
        }

        // 与“首个下载安装自动成为默认”一致：没有默认版本时，取首个探测可用的版本
        var hasDefault = await _installations.GetDefaultAsync(cancellationToken).ConfigureAwait(false) is not null;
        if (!hasDefault
            && discovered.FirstOrDefault(i => i.Status == InstallationStatus.Available) is { } firstAvailable)
        {
            await _installations.SetDefaultAsync(firstAvailable.Id, cancellationToken).ConfigureAwait(false);
            firstAvailable.IsDefault = true;
            _logger.LogInformation("已将发现的首个可用版本 {Version} 设为默认", firstAvailable.VersionLabel);
        }

        // 同步镜像文件（与安装/切换流程保持一致）
        var all = await _installations.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var manifest = _manifest.Load();
        if (all.FirstOrDefault(i => i.IsDefault) is { } currentDefault)
        {
            manifest.DefaultInstallationId = currentDefault.Id;
        }

        manifest.Entries = all.Select(i => new InstallationManifestEntry
        {
            Id = i.Id,
            VersionLabel = i.VersionLabel,
            Directory = i.Directory,
            ExecutablePath = i.ExecutablePath,
            Sha256 = i.Sha256,
        }).ToList();
        _manifest.Save(manifest);

        return discovered;
    }

    /// <summary>
    /// 版本目录命名约定为“&lt;版本标签&gt;-&lt;指纹&gt;”（如 b10618-91b16d71）：
    /// 该命名下以目录名标签为准（比 --version 文本更可靠）；否则仅在探测未得到有效标签时用目录名兜底。
    /// 返回是否修改了版本标签。
    /// </summary>
    private static bool ApplyFolderLabelFallback(LlamaCppInstallation installation, string folderName)
    {
        var managedLabel = TryExtractManagedLabel(folderName);
        if (managedLabel is not null)
        {
            if (string.Equals(installation.VersionLabel, managedLabel, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            installation.VersionLabel = managedLabel;
            return true;
        }

        if (installation.VersionLabel is not ("未知" or "探测中" or ""))
        {
            return false;
        }

        var trimmed = folderName.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        installation.VersionLabel = trimmed.Length > 40 ? trimmed[..40] : trimmed;
        return true;
    }

    private static string? TryExtractManagedLabel(string folderName)
    {
        var dash = folderName.IndexOf('-');
        if (dash <= 0)
        {
            return null;
        }

        var prefix = folderName[..dash];
        return prefix.Length is > 1 and <= 16
               && prefix[0] is 'b' or 'B'
               && prefix[1..].All(char.IsAsciiDigit)
            ? "b" + prefix[1..]
            : null;
    }

    public async Task SetDefaultAsync(Guid installationId, CancellationToken cancellationToken = default)
    {
        var target = await _installations.GetByIdAsync(installationId, cancellationToken).ConfigureAwait(false)
            ?? throw new UpdateException("安装记录不存在。");
        if (!File.Exists(target.ExecutablePath))
        {
            throw new UpdateException($"该版本的 llama-server.exe 不存在：{target.ExecutablePath}");
        }

        var current = await _installations.GetDefaultAsync(cancellationToken).ConfigureAwait(false);
        var operation = new UpdateOperation
        {
            Kind = UpdateOperationKind.SwitchDefault,
            FromVersion = current?.VersionLabel,
            ToVersion = target.VersionLabel,
            Stage = UpdateStage.ReadyToSwitch,
            StartedUtc = _timeProvider.GetUtcNow(),
        };
        await _operations.AddAsync(operation, cancellationToken).ConfigureAwait(false);

        await _installations.SetDefaultAsync(installationId, cancellationToken).ConfigureAwait(false);

        var manifest = _manifest.Load();
        if (current is not null && current.Id != installationId)
        {
            manifest.PreviousDefaultInstallationId = current.Id;
        }

        manifest.DefaultInstallationId = installationId;
        var all = await _installations.GetAllAsync(cancellationToken).ConfigureAwait(false);
        manifest.Entries = all.Select(i => new InstallationManifestEntry
        {
            Id = i.Id,
            VersionLabel = i.VersionLabel,
            Directory = i.Directory,
            ExecutablePath = i.ExecutablePath,
            Sha256 = i.Sha256,
        }).ToList();
        _manifest.Save(manifest);

        operation.Stage = UpdateStage.Active;
        operation.CompletedUtc = _timeProvider.GetUtcNow();
        await _operations.UpdateAsync(operation, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("默认 llama.cpp 版本已切换为 {Version}", target.VersionLabel);
    }

    public async Task<RollbackResult> RollbackAsync(CancellationToken cancellationToken = default)
    {
        var manifest = _manifest.Load();
        if (manifest.PreviousDefaultInstallationId is not { } previousId)
        {
            return new RollbackResult(false, "没有可回滚的历史默认版本记录。", null);
        }

        var previous = await _installations.GetByIdAsync(previousId, cancellationToken).ConfigureAwait(false);
        if (previous is null || !File.Exists(previous.ExecutablePath))
        {
            return new RollbackResult(false, "上一次的默认版本已不存在或文件缺失，无法回滚。", null);
        }

        var current = await _installations.GetDefaultAsync(cancellationToken).ConfigureAwait(false);
        var operation = new UpdateOperation
        {
            Kind = UpdateOperationKind.Rollback,
            FromVersion = current?.VersionLabel,
            ToVersion = previous.VersionLabel,
            Stage = UpdateStage.ReadyToSwitch,
            StartedUtc = _timeProvider.GetUtcNow(),
        };
        await _operations.AddAsync(operation, cancellationToken).ConfigureAwait(false);

        await _installations.SetDefaultAsync(previousId, cancellationToken).ConfigureAwait(false);
        manifest.DefaultInstallationId = previousId;
        manifest.PreviousDefaultInstallationId = current?.Id;
        _manifest.Save(manifest);

        operation.Stage = UpdateStage.RolledBack;
        operation.RolledBack = true;
        operation.CompletedUtc = _timeProvider.GetUtcNow();
        await _operations.UpdateAsync(operation, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("已回滚默认版本：{From} → {To}", current?.VersionLabel, previous.VersionLabel);
        return new RollbackResult(
            true,
            $"已回滚默认版本到 {previous.VersionLabel}（原默认 {current?.VersionLabel ?? "未知"} 保留可用）。",
            previousId);
    }

    public async Task DeleteInstallationAsync(
        Guid installationId,
        bool deleteFiles,
        CancellationToken cancellationToken = default)
    {
        var installation = await _installations.GetByIdAsync(installationId, cancellationToken).ConfigureAwait(false)
            ?? throw new UpdateException("安装记录不存在。");

        if (installation.IsDefault)
        {
            throw new UpdateException("不能删除当前默认版本。请先切换到其他版本。");
        }

        var profiles = await _profiles.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var referencing = profiles.Where(p => p.InstallationId == installationId).ToList();
        if (referencing.Count > 0)
        {
            throw new UpdateException(
                $"仍有 {referencing.Count} 个配置档案使用该版本（如：{string.Join("、", referencing.Take(3).Select(p => p.Name))}），请先改绑。");
        }

        var instances = await _instances.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var running = instances.Where(i =>
            i.InstallationId == installationId
            && Domain.StateMachine.InstanceStateMachine.IsActive(i.State)).ToList();
        if (running.Count > 0)
        {
            throw new UpdateException(
                $"仍有运行中的实例使用该版本（{string.Join("、", running.Select(i => i.Name))}），请先停止。");
        }

        var operation = new UpdateOperation
        {
            Kind = UpdateOperationKind.Cleanup,
            ToVersion = installation.VersionLabel,
            Stage = UpdateStage.Active,
            StartedUtc = _timeProvider.GetUtcNow(),
        };

        await _installations.DeleteAsync(installationId, cancellationToken).ConfigureAwait(false);

        if (deleteFiles)
        {
            var targetDirectory = Path.GetFullPath(installation.Directory);
            if (installation.SourceKind == InstallationSourceKind.ExternalPath)
            {
                throw new UpdateException("外部登记的程序不会删除磁盘文件（记录已移除）。");
            }

            // 允许删除当前与默认版本目录下的文件（自定义目录变更后仍可清理旧版本）
            var runtimesRoots = new[] { _paths.RuntimesDirectory, _paths.DefaultRuntimesDirectory }
                .Select(root => Path.GetFullPath(root).TrimEnd('\\') + Path.DirectorySeparatorChar)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!runtimesRoots.Any(root => targetDirectory.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            {
                throw new UpdateException(
                    $"出于安全考虑，拒绝删除运行目录之外的路径：{targetDirectory}（记录已移除）。");
            }

            if (Directory.Exists(targetDirectory))
            {
                Directory.Delete(targetDirectory, recursive: true);
            }
        }

        operation.InstalledDirectory = installation.Directory;
        operation.CompletedUtc = _timeProvider.GetUtcNow();
        await _operations.AddAsync(operation, cancellationToken).ConfigureAwait(false);

        var manifest = _manifest.Load();
        var all = await _installations.GetAllAsync(cancellationToken).ConfigureAwait(false);
        manifest.Entries = all.Select(i => new InstallationManifestEntry
        {
            Id = i.Id,
            VersionLabel = i.VersionLabel,
            Directory = i.Directory,
            ExecutablePath = i.ExecutablePath,
            Sha256 = i.Sha256,
        }).ToList();
        _manifest.Save(manifest);
        _logger.LogInformation("已移除安装 {Version}（删除文件：{DeleteFiles}）", installation.VersionLabel, deleteFiles);
    }

    public Task<IReadOnlyList<UpdateOperation>> GetHistoryAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
        => _operations.GetRecentAsync(limit, cancellationToken);

    private async Task<LlamaCppInstallation> InstallFromArchiveAsync(
        UpdateOperation operation,
        string archivePath,
        string versionLabel,
        InstallationSourceKind sourceKind,
        string assetName,
        string? sourceUrl,
        string? sha256,
        bool sha256Verified,
        string? notes,
        IProgress<DownloadProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        operation.Stage = UpdateStage.Extracting;
        await _operations.UpdateAsync(operation, cancellationToken).ConfigureAwait(false);

        var stagingDirectory = Path.Combine(
            _paths.RuntimesDirectory,
            $".staging-{SanitizeFileName(versionLabel)}-{Guid.NewGuid().ToString("N")[..8]}");
        operation.StagingDirectory = stagingDirectory;

        var extract = await _extractor.ExtractAsync(archivePath, stagingDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (!extract.Success)
        {
            return await FailOperationAsync(
                operation,
                UpdateStage.Failed,
                "解压失败：" + extract.Error,
                cancellationToken).ConfigureAwait(false);
        }

        var stagedExecutable = FindLlamaServerExecutable(stagingDirectory);
        if (stagedExecutable is null)
        {
            TryDeleteDirectory(stagingDirectory);
            return await FailOperationAsync(
                operation,
                UpdateStage.Failed,
                "压缩包中未找到 llama-server.exe，无法完成安装。",
                cancellationToken).ConfigureAwait(false);
        }

        operation.Stage = UpdateStage.Probing;
        await _operations.UpdateAsync(operation, cancellationToken).ConfigureAwait(false);
        var capabilities = await _probe
            .ProbeAsync(stagedExecutable, forceRefresh: true, cancellationToken)
            .ConfigureAwait(false);

        if (capabilities.Outcome == ProbeOutcome.Failed)
        {
            var reason = capabilities.Warnings.FirstOrDefault() ?? "版本/帮助探测均失败。";
            TryDeleteDirectory(stagingDirectory);
            return await FailOperationAsync(
                operation,
                UpdateStage.Failed,
                "安装后能力验证失败，未注册该版本：" + reason,
                cancellationToken).ConfigureAwait(false);
        }

        operation.Stage = UpdateStage.ReadyToSwitch;
        await _operations.UpdateAsync(operation, cancellationToken).ConfigureAwait(false);

        var finalDirectory = Path.Combine(
            _paths.RuntimesDirectory,
            $"{SanitizeFileName(versionLabel)}-{(sha256 is { Length: >= 8 } ? sha256[..8] : Guid.NewGuid().ToString("N")[..8])}");
        if (Directory.Exists(finalDirectory))
        {
            TryDeleteDirectory(stagingDirectory);
            return await FailOperationAsync(
                operation,
                UpdateStage.Failed,
                $"目标版本目录已存在：{finalDirectory}（可能是同一版本的重复安装）。",
                cancellationToken).ConfigureAwait(false);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(finalDirectory)!);
        Directory.Move(stagingDirectory, finalDirectory);
        var relativeExecutable = Path.GetRelativePath(stagingDirectory, stagedExecutable);
        var finalExecutable = Path.Combine(finalDirectory, relativeExecutable);

        var hasDefault = await _installations.GetDefaultAsync(cancellationToken).ConfigureAwait(false) is not null;
        var installation = new LlamaCppInstallation
        {
            Id = Guid.NewGuid(),
            VersionLabel = versionLabel,
            BuildInfo = capabilities.VersionText,
            AssetName = assetName,
            SourceUrl = sourceUrl,
            SourceKind = sourceKind,
            Directory = finalDirectory,
            ExecutablePath = finalExecutable,
            Sha256 = sha256,
            Sha256FromOfficialDigest = sha256Verified,
            ArchiveSizeBytes = new FileInfo(archivePath).Exists ? new FileInfo(archivePath).Length : null,
            Status = InstallationStatus.Available,
            IsDefault = !hasDefault,
            CapabilitiesJson = JsonSerializer.Serialize(capabilities, JsonDefaults.Storage),
            LastProbedUtc = _timeProvider.GetUtcNow(),
            InstalledUtc = _timeProvider.GetUtcNow(),
            Notes = notes,
            BackendTags = capabilities.DetectedBackends.ToList(),
        };

        await _installations.UpsertAsync(installation, cancellationToken).ConfigureAwait(false);

        var all = await _installations.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var manifest = _manifest.Load();
        if (installation.IsDefault)
        {
            manifest.DefaultInstallationId = installation.Id;
        }

        manifest.Entries = all.Select(i => new InstallationManifestEntry
        {
            Id = i.Id,
            VersionLabel = i.VersionLabel,
            Directory = i.Directory,
            ExecutablePath = i.ExecutablePath,
            Sha256 = i.Sha256,
        }).ToList();
        _manifest.Save(manifest);

        _logger.LogInformation(
            "llama.cpp {Version} 已安装到 {Directory}（来源：{Source}，探测：{Outcome}）",
            versionLabel, finalDirectory, sourceKind, capabilities.Outcome);
        return installation;
    }

    private async Task<LlamaCppInstallation> FailOperationAsync(
        UpdateOperation operation,
        UpdateStage stage,
        string message,
        CancellationToken cancellationToken)
    {
        operation.Stage = stage;
        operation.Error = message;
        operation.CompletedUtc = _timeProvider.GetUtcNow();
        await _operations.UpdateAsync(operation, cancellationToken).ConfigureAwait(false);
        if (operation.StagingDirectory is { } staging)
        {
            TryDeleteDirectory(staging);
        }

        throw new UpdateException(message);
    }

    private static string? FindLlamaServerExecutable(string rootDirectory)
    {
        try
        {
            var direct = Path.Combine(rootDirectory, "llama-server.exe");
            if (File.Exists(direct))
            {
                return direct;
            }

            return Directory
                .EnumerateFiles(rootDirectory, "llama-server.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 为已安装的 CUDA 版本补装官方 cudart 运行时辅助包：
    /// 下载 → SHA-256 校验（官方 digest 存在时）→ 安全解压 → 仅复制 DLL 到可执行文件所在目录。
    /// 不修改默认版本指针，不触碰其他已安装版本；全过程写入 UpdateOperations（Kind=RuntimePack）。
    /// </summary>
    public async Task<RuntimePackInstallResult> InstallRuntimePackAsync(
        ReleaseInfo release,
        ReleaseAsset asset,
        LlamaCppInstallation installation,
        IProgress<DownloadProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(installation);

        if (installation.Status != InstallationStatus.Available
            || !File.Exists(installation.ExecutablePath))
        {
            throw new UpdateException("目标版本不可用（安装目录或可执行文件不存在），无法安装运行时辅助包。");
        }

        if (!IsAllowedOfficialDownloadHost(asset.DownloadUrl))
        {
            throw new UpdateException("运行时辅助包仅允许从官方 GitHub 发布来源自动下载。");
        }

        var targetDirectory = Path.GetDirectoryName(installation.ExecutablePath)!;
        var downloadPath = Path.Combine(_paths.DownloadsDirectory, SanitizeFileName(asset.Name));
        var operation = new UpdateOperation
        {
            Kind = UpdateOperationKind.RuntimePack,
            ToVersion = release.TagName,
            AssetName = asset.Name,
            AssetUrl = asset.DownloadUrl,
            AssetSizeBytes = asset.SizeBytes,
            Stage = UpdateStage.Downloading,
            StartedUtc = _timeProvider.GetUtcNow(),
        };
        await _operations.AddAsync(operation, cancellationToken).ConfigureAwait(false);

        var download = await _downloads
            .DownloadAsync(asset.DownloadUrl, downloadPath, asset.SizeBytes, progress, cancellationToken)
            .ConfigureAwait(false);
        if (!download.Success)
        {
            await FailRuntimePackOperationAsync(
                operation,
                download.ErrorKind == DownloadErrorKind.Cancelled ? UpdateStage.Cancelled : UpdateStage.Failed,
                download.Error ?? "运行时辅助包下载失败。",
                cancellationToken).ConfigureAwait(false);
        }

        operation.Stage = UpdateStage.Verifying;
        operation.Sha256 = download.Sha256;
        var officialDigest = ParseSha256Digest(asset.Sha256Digest);
        if (officialDigest is not null)
        {
            if (!string.Equals(officialDigest, download.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await FailRuntimePackOperationAsync(
                    operation,
                    UpdateStage.Failed,
                    "SHA-256 校验失败：运行时辅助包与官方 digest 不一致，已拒绝安装。",
                    cancellationToken).ConfigureAwait(false);
            }

            operation.Sha256Verified = true;
            operation.Sha256FromOfficialDigest = true;
        }

        await _operations.UpdateAsync(operation, cancellationToken).ConfigureAwait(false);

        operation.Stage = UpdateStage.Extracting;
        var stagingDirectory = Path.Combine(
            _paths.RuntimesDirectory,
            $".staging-cudart-{Guid.NewGuid().ToString("N")[..8]}");
        operation.StagingDirectory = stagingDirectory;
        var extract = await _extractor.ExtractAsync(download.FilePath!, stagingDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (!extract.Success)
        {
            await FailRuntimePackOperationAsync(
                operation,
                UpdateStage.Failed,
                "运行时辅助包解压失败：" + extract.Error,
                cancellationToken).ConfigureAwait(false);
        }

        var dlls = Directory.EnumerateFiles(stagingDirectory, "*.dll", SearchOption.AllDirectories).ToList();
        if (dlls.Count == 0)
        {
            await FailRuntimePackOperationAsync(
                operation,
                UpdateStage.Failed,
                "运行时辅助包中未找到 DLL 文件，未对版本目录做任何更改。",
                cancellationToken).ConfigureAwait(false);
        }

        var copied = 0;
        foreach (var dll in dlls)
        {
            File.Copy(dll, Path.Combine(targetDirectory, Path.GetFileName(dll)), overwrite: true);
            copied++;
        }

        TryDeleteDirectory(stagingDirectory);
        operation.StagingDirectory = null;
        operation.Stage = UpdateStage.Active;
        operation.InstalledDirectory = targetDirectory;
        operation.CompletedUtc = _timeProvider.GetUtcNow();
        await _operations.UpdateAsync(operation, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "CUDA 运行时辅助包 {Asset} 已安装：{Count} 个 DLL → {Directory}",
            asset.Name,
            copied,
            targetDirectory);
        return new RuntimePackInstallResult(asset.Name, copied, targetDirectory);
    }

    private async Task FailRuntimePackOperationAsync(
        UpdateOperation operation,
        UpdateStage stage,
        string message,
        CancellationToken cancellationToken)
    {
        operation.Stage = stage;
        operation.Error = message;
        operation.CompletedUtc = _timeProvider.GetUtcNow();
        await _operations.UpdateAsync(operation, cancellationToken).ConfigureAwait(false);
        if (operation.StagingDirectory is { } staging)
        {
            TryDeleteDirectory(staging);
        }

        throw new UpdateException(message);
    }

    private static bool IsAllowedOfficialDownloadHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return AllowedOfficialDownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }

    private static string? ParseSha256Digest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        var value = digest.Trim();
        const string prefix = "sha256:";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[prefix.Length..];
        }

        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;
    }

    private static string ExtractVersionFromFileName(string fileName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(fileName, @"(b\d{3,6})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups[1].Value.ToLowerInvariant();
        }

        var withoutExtension = Path.GetFileNameWithoutExtension(fileName);
        return withoutExtension.Length <= 60 ? withoutExtension : withoutExtension[..60];
    }

    private static string SanitizeFileName(string name)
    {
        var safe = new string(name
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
            .ToArray())
            .Trim();
        return safe.Length == 0 ? "llama.cpp-asset" : safe;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }
}
