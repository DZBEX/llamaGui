using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Capabilities;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Application.Services;

/// <summary>llama.cpp 安装管理实现。</summary>
public sealed class LlamaInstallationService : ILlamaInstallationService
{
    private readonly IInstallationRepository _repository;
    private readonly IProfileRepository _profiles;
    private readonly ILlamaBinaryProbeService _probeService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LlamaInstallationService> _logger;

    public LlamaInstallationService(
        IInstallationRepository repository,
        IProfileRepository profiles,
        ILlamaBinaryProbeService probeService,
        TimeProvider timeProvider,
        ILogger<LlamaInstallationService> logger)
    {
        _repository = repository;
        _profiles = profiles;
        _probeService = probeService;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task<IReadOnlyList<LlamaCppInstallation>> GetAllAsync(CancellationToken cancellationToken = default)
        => _repository.GetAllAsync(cancellationToken);

    public Task<LlamaCppInstallation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _repository.GetByIdAsync(id, cancellationToken);

    public Task<LlamaCppInstallation?> GetDefaultAsync(CancellationToken cancellationToken = default)
        => _repository.GetDefaultAsync(cancellationToken);

    public async Task<LlamaCppInstallation> RegisterExternalAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new LlamaInstallationException("可执行文件路径不能为空。");
        }

        var fullPath = Path.GetFullPath(executablePath.Trim());
        if (!File.Exists(fullPath))
        {
            throw new LlamaInstallationException($"文件不存在：{fullPath}");
        }

        if (!fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new LlamaInstallationException("请选择 llama-server.exe 可执行文件。");
        }

        var existing = await _repository.FindByExecutablePathAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // 已登记：重新探测并更新
            return await RefreshProbeAsync(existing.Id, cancellationToken).ConfigureAwait(false);
        }

        var now = _timeProvider.GetUtcNow();
        var installation = new LlamaCppInstallation
        {
            Id = Guid.NewGuid(),
            Directory = Path.GetDirectoryName(fullPath) ?? string.Empty,
            ExecutablePath = fullPath,
            SourceKind = InstallationSourceKind.ExternalPath,
            InstalledUtc = now,
            VersionLabel = "探测中",
        };

        var capabilities = await _probeService.ProbeAsync(fullPath, forceRefresh: true, cancellationToken)
            .ConfigureAwait(false);
        ApplyCapabilities(installation, capabilities, now);

        await _repository.UpsertAsync(installation, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "已登记外部 llama.cpp 程序 {Path}，版本 {Version}，探测结果 {Outcome}",
            fullPath, installation.VersionLabel, capabilities.Outcome);
        return installation;
    }

    public async Task<LlamaCppInstallation> RefreshProbeAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var installation = await _repository.GetByIdAsync(installationId, cancellationToken).ConfigureAwait(false)
            ?? throw new LlamaInstallationException("安装记录不存在。");

        if (!File.Exists(installation.ExecutablePath))
        {
            installation.Status = InstallationStatus.Missing;
            installation.LastProbedUtc = _timeProvider.GetUtcNow();
            await _repository.UpsertAsync(installation, cancellationToken).ConfigureAwait(false);
            return installation;
        }

        var capabilities = await _probeService
            .ProbeAsync(installation.ExecutablePath, forceRefresh: true, cancellationToken)
            .ConfigureAwait(false);
        ApplyCapabilities(installation, capabilities, _timeProvider.GetUtcNow());
        await _repository.UpsertAsync(installation, cancellationToken).ConfigureAwait(false);
        return installation;
    }

    public Task SetDefaultAsync(Guid installationId, CancellationToken cancellationToken = default)
        => _repository.SetDefaultAsync(installationId, cancellationToken);

    public async Task RemoveAsync(Guid installationId, CancellationToken cancellationToken = default)
    {
        var profiles = await _profiles.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var referencing = profiles.Where(p => p.InstallationId == installationId).ToList();
        if (referencing.Count > 0)
        {
            throw new LlamaInstallationException(
                $"仍有 {referencing.Count} 个配置档案使用该程序（如：{string.Join("、", referencing.Take(3).Select(p => p.Name))}）。" +
                "请先在这些配置中改绑其他版本。");
        }

        await _repository.DeleteAsync(installationId, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("已移除 llama.cpp 安装记录 {Id}（磁盘文件未删除）", installationId);
    }

    private void ApplyCapabilities(
        LlamaCppInstallation installation,
        ServerCapabilities capabilities,
        DateTimeOffset now)
    {
        installation.CapabilitiesJson = System.Text.Json.JsonSerializer.Serialize(capabilities, JsonDefaults.Storage);
        installation.LastProbedUtc = now;
        installation.BuildInfo = BuildSummary(capabilities);

        var label = HelpTextParser.ExtractVersionLabel(capabilities.VersionText);
        if (!string.IsNullOrWhiteSpace(label))
        {
            installation.VersionLabel = label!;
        }
        else if (installation.VersionLabel is "探测中" or "")
        {
            installation.VersionLabel = "未知";
        }

        installation.BackendTags = capabilities.DetectedBackends.ToList();
        installation.Status = capabilities.Outcome switch
        {
            ProbeOutcome.Success => InstallationStatus.Available,
            ProbeOutcome.PartialFailure => InstallationStatus.Available,
            ProbeOutcome.Failed => InstallationStatus.Unhealthy,
            _ => InstallationStatus.Unknown,
        };
    }

    private static string? BuildSummary(ServerCapabilities capabilities)
    {
        var versionLines = (capabilities.VersionText ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return versionLines.Length == 0 ? null : string.Join(" | ", versionLines.Take(3));
    }
}
