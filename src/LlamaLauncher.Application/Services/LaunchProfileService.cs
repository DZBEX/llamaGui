using System.Text.Json;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.CommandLine;
using LlamaLauncher.Domain;
using LlamaLauncher.Domain.StateMachine;
using LlamaLauncher.Domain.Validation;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Application.Services;

/// <summary>启动配置档案服务实现。</summary>
public sealed class LaunchProfileService : ILaunchProfileService
{
    private const int DefaultPort = 8080;

    private readonly IProfileRepository _profiles;
    private readonly IModelRepository _models;
    private readonly IInstallationRepository _installations;
    private readonly IInstanceRepository _instances;
    private readonly ILlamaBinaryProbeService _probeService;
    private readonly ISecretStore _secrets;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LaunchProfileService> _logger;

    public LaunchProfileService(
        IProfileRepository profiles,
        IModelRepository models,
        IInstallationRepository installations,
        IInstanceRepository instances,
        ILlamaBinaryProbeService probeService,
        ISecretStore secrets,
        TimeProvider timeProvider,
        ILogger<LaunchProfileService> logger)
    {
        _profiles = profiles;
        _models = models;
        _installations = installations;
        _instances = instances;
        _probeService = probeService;
        _secrets = secrets;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task<IReadOnlyList<LaunchProfile>> GetAllAsync(CancellationToken cancellationToken = default)
        => _profiles.GetAllAsync(cancellationToken);

    public Task<LaunchProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => _profiles.GetByIdAsync(id, cancellationToken);

    public async Task<LaunchProfile> CreateAsync(
        string name,
        Guid? modelId,
        Guid? installationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new LaunchProfileException("配置名称不能为空。");
        }

        var trimmed = name.Trim();
        var existing = await _profiles.FindByNameAsync(trimmed, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            throw new LaunchProfileException($"已存在同名配置：{trimmed}。");
        }

        var now = _timeProvider.GetUtcNow();
        var profile = new LaunchProfile
        {
            Id = Guid.NewGuid(),
            Name = trimmed,
            ModelId = modelId,
            InstallationId = installationId,
            Options = new ServerLaunchOptions
            {
                Host = "127.0.0.1",
                Port = await SuggestPortAsync(cancellationToken).ConfigureAwait(false),
            },
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        if (modelId is { } id)
        {
            var model = await _models.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
            if (model is not null)
            {
                profile.Options.ModelPath = model.FilePath;
                profile.ModelSnapshot = new ModelReferenceSnapshot
                {
                    ModelId = model.Id,
                    Name = model.DisplayName,
                    FilePath = model.FilePath,
                };
            }
        }

        if (installationId is { } installation)
        {
            var record = await _installations.GetByIdAsync(installation, cancellationToken).ConfigureAwait(false);
            if (record is not null)
            {
                profile.InstallationSnapshot = new InstallationReferenceSnapshot
                {
                    InstallationId = record.Id,
                    VersionLabel = record.VersionLabel,
                    ExecutablePath = record.ExecutablePath,
                };
            }
        }

        await _profiles.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);
        return profile;
    }

    public async Task SaveAsync(LaunchProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new LaunchProfileException("配置名称不能为空。");
        }

        profile.Name = profile.Name.Trim();
        var existing = await _profiles.FindByNameAsync(profile.Name, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.Id != profile.Id)
        {
            throw new LaunchProfileException($"已存在同名配置：{profile.Name}。");
        }

        profile.UpdatedUtc = _timeProvider.GetUtcNow();
        await _profiles.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LaunchProfile> DuplicateAsync(
        Guid id,
        string? newName,
        CancellationToken cancellationToken = default)
    {
        var source = await _profiles.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new LaunchProfileException("要复制的配置不存在。");

        var name = string.IsNullOrWhiteSpace(newName) ? source.Name + " 副本" : newName.Trim();
        var suffix = 2;
        while (await _profiles.FindByNameAsync(name, cancellationToken).ConfigureAwait(false) is not null)
        {
            name = (string.IsNullOrWhiteSpace(newName) ? source.Name + " 副本" : newName.Trim()) + " " + suffix;
            suffix++;
            if (suffix > 100)
            {
                throw new LaunchProfileException("无法生成唯一的配置名称。");
            }
        }

        var json = JsonSerializer.Serialize(source, JsonDefaults.Storage);
        var copy = JsonSerializer.Deserialize<LaunchProfile>(json, JsonDefaults.Storage)!;
        copy.Id = Guid.NewGuid();
        copy.Name = name;
        copy.IsDefault = false;
        copy.CreatedUtc = _timeProvider.GetUtcNow();
        copy.UpdatedUtc = copy.CreatedUtc;
        await _profiles.UpsertAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var instances = await _instances.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var active = instances
            .Where(i => i.ProfileId == id && InstanceStateMachine.IsActive(i.State))
            .ToList();
        if (active.Count > 0)
        {
            throw new LaunchProfileException(
                $"仍有运行中的实例使用该配置（{string.Join("、", active.Select(i => i.Name))}），请先停止。");
        }

        await _profiles.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public Task SetDefaultAsync(Guid id, CancellationToken cancellationToken = default)
        => _profiles.SetDefaultAsync(id, cancellationToken);

    public Task<LaunchProfile?> GetDefaultAsync(CancellationToken cancellationToken = default)
        => _profiles.GetDefaultAsync(cancellationToken);

    public async Task<ProfileValidationReport> ValidateAsync(
        LaunchProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var issues = new List<ValidationIssue>();

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "name", "配置名称不能为空。"));
        }

        var model = profile.ModelId is { } modelId
            ? await _models.GetByIdAsync(modelId, cancellationToken).ConfigureAwait(false)
            : null;
        var modelPath = model?.FilePath ?? profile.Options.ModelPath;
        var modelExists = !string.IsNullOrWhiteSpace(modelPath) && File.Exists(modelPath);

        var installation = profile.InstallationId is { } installationId
            ? await _installations.GetByIdAsync(installationId, cancellationToken).ConfigureAwait(false)
            : null;
        var executablePath = installation?.ExecutablePath ?? profile.InstallationSnapshot?.ExecutablePath;
        var executableExists = !string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath);

        var options = JsonSerializer.Deserialize<ServerLaunchOptions>(
            JsonSerializer.Serialize(profile.Options, JsonDefaults.Storage),
            JsonDefaults.Storage) ?? new ServerLaunchOptions();
        options.ModelPath = modelPath;

        var apiKeyConfigured = true;
        if (options.ApiKeyEnabled)
        {
            var name = options.ApiKeySecretName ?? SecretNames.DefaultApiKey;
            apiKeyConfigured = await _secrets.ExistsAsync(name, cancellationToken).ConfigureAwait(false);
        }

        issues.AddRange(LaunchOptionsValidator.ValidateValues(options, new LaunchValidationContext
        {
            ApiKeyConfigured = apiKeyConfigured,
            LanAccessConfirmed = true,
            PortInUse = false,
            ModelFilePath = modelExists ? modelPath : null,
            ExecutablePath = executableExists ? executablePath : null,
            ModelFileSizeBytes = model?.FileSizeBytes,
        }));

        string? preview = null;
        if (executableExists)
        {
            var capabilities = await _probeService.ProbeAsync(executablePath!, forceRefresh: false, cancellationToken)
                .ConfigureAwait(false);
            if (capabilities.Outcome == ProbeOutcome.Failed)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "Probe",
                    "无法探测该 llama.cpp 程序的能力，请检查文件是否完整。"));
            }
            else
            {
                string? apiKey = null;
                if (options.ApiKeyEnabled)
                {
                    var name = options.ApiKeySecretName ?? SecretNames.DefaultApiKey;
                    try
                    {
                        apiKey = await _secrets.GetAsync(name, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        apiKey = null;
                    }
                }

                var build = CommandLineBuilder.Build(executablePath!, new CommandLineBuildRequest
                {
                    Options = options,
                    Capabilities = capabilities,
                    ResolvedApiKey = apiKey,
                    ExtraArguments = profile.ExtraArguments,
                    AllowExtraArgsOverrideManaged = profile.AllowExtraArgsOverrideManaged,
                    EnvironmentVariables = profile.EnvironmentVariables,
                });
                issues.AddRange(build.Issues);
                preview = build.MaskedPreview;
            }
        }

        return new ProfileValidationReport(issues, preview);
    }

    public async Task<int> SuggestPortAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await _profiles.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var used = profiles.Select(p => p.Options.Port).ToHashSet();
        var port = DefaultPort;
        while (used.Contains(port) && port < 65000)
        {
            port++;
        }

        return port;
    }
}
