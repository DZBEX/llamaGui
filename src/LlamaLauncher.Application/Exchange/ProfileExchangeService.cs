using System.Text.Json;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Application.Exchange;

/// <summary>配置档案交换失败（面向用户的消息）。</summary>
public sealed class ProfileExchangeException : Exception
{
    public ProfileExchangeException(string userMessage, Exception? inner = null)
        : base(userMessage, inner)
    {
    }
}

/// <summary>配置档案 JSON 导出 / 预览 / 导入服务。</summary>
public interface IProfileExchangeService
{
    /// <summary>导出档案到指定文件；返回实际写入路径。</summary>
    Task<string> ExportAsync(
        IReadOnlyCollection<Guid>? profileIds,
        string filePath,
        bool includeSecretReferences,
        CancellationToken cancellationToken = default);

    /// <summary>解析并生成导入预览（不写入任何数据）。</summary>
    Task<ImportPlan> PreviewAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>执行导入；selectedIndexes 为空表示全部适用条目。</summary>
    Task<ImportResult> ApplyAsync(
        ImportPlan plan,
        IReadOnlySet<int>? selectedIndexes,
        CancellationToken cancellationToken = default);
}

/// <summary>配置档案交换服务实现。</summary>
public sealed class ProfileExchangeService : IProfileExchangeService
{
    private readonly IProfileRepository _profiles;
    private readonly IModelRepository _models;
    private readonly IInstallationRepository _installations;
    private readonly IModelCatalogService _modelCatalog;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProfileExchangeService> _logger;

    public ProfileExchangeService(
        IProfileRepository profiles,
        IModelRepository models,
        IInstallationRepository installations,
        IModelCatalogService modelCatalog,
        TimeProvider timeProvider,
        ILogger<ProfileExchangeService> logger)
    {
        _profiles = profiles;
        _models = models;
        _installations = installations;
        _modelCatalog = modelCatalog;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<string> ExportAsync(
        IReadOnlyCollection<Guid>? profileIds,
        string filePath,
        bool includeSecretReferences,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ProfileExchangeException("导出路径不能为空。");
        }

        var all = await _profiles.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var selected = profileIds is { Count: > 0 }
            ? all.Where(p => profileIds.Contains(p.Id)).ToList()
            : all.ToList();

        if (selected.Count == 0)
        {
            throw new ProfileExchangeException("没有可导出的配置档案。");
        }

        var document = new ProfileExchangeDocument
        {
            SchemaVersion = ProfileExchangeDocument.CurrentSchemaVersion,
            ExportedUtc = _timeProvider.GetUtcNow(),
            AppVersion = typeof(ProfileExchangeService).Assembly.GetName().Version?.ToString(3),
        };

        var models = await _models.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var modelById = models.ToDictionary(m => m.Id);

        foreach (var profile in selected)
        {
            var options = CloneOptions(profile.Options);
            if (!includeSecretReferences)
            {
                // 默认导出不包含秘密引用；密钥本体从不出导出流程。
                options.ApiKeySecretName = null;
            }

            string? modelPath = options.ModelPath;
            if (modelPath is null && profile.ModelId is { } modelId && modelById.TryGetValue(modelId, out var model))
            {
                modelPath = model.FilePath;
            }

            var entry = new ProfileExchangeEntry
            {
                Id = profile.Id,
                Name = profile.Name,
                Model = new ModelReferenceSnapshot
                {
                    ModelId = profile.ModelId,
                    Name = profile.ModelSnapshot?.Name
                        ?? (profile.ModelId is { } id2 && modelById.TryGetValue(id2, out var m2) ? m2.DisplayName : null),
                    FilePath = modelPath,
                    PathIsRelative = false,
                },
                Installation = profile.InstallationSnapshot ?? new InstallationReferenceSnapshot
                {
                    InstallationId = profile.InstallationId,
                    ExecutablePath = profile.InstallationSnapshot?.ExecutablePath,
                    PathIsRelative = false,
                },
                Options = options,
                ExtraArguments = profile.ExtraArguments,
                AllowExtraArgsOverrideManaged = profile.AllowExtraArgsOverrideManaged,
                EnvironmentVariables = new Dictionary<string, string>(profile.EnvironmentVariables, StringComparer.OrdinalIgnoreCase),
                Notes = profile.Notes,
                IsDefault = profile.IsDefault,
            };

            if (entry.Installation is not null && string.IsNullOrEmpty(entry.Installation.ExecutablePath))
            {
                entry.Installation = null;
            }

            document.Profiles.Add(entry);
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(
                filePath,
                JsonSerializer.Serialize(document, JsonDefaults.Export),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProfileExchangeException($"写入导出文件失败：{ex.Message}", ex);
        }

        _logger.LogInformation("已导出 {Count} 个配置档案到 {Path}（包含秘密引用：{Secrets}）",
            document.Profiles.Count, filePath, includeSecretReferences);
        return Path.GetFullPath(filePath);
    }

    public async Task<ImportPlan> PreviewAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new ProfileExchangeException($"导入文件不存在：{filePath}");
        }

        ProfileExchangeDocument? document;
        try
        {
            var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            document = JsonSerializer.Deserialize<ProfileExchangeDocument>(json, JsonDefaults.Storage);
        }
        catch (JsonException ex)
        {
            throw new ProfileExchangeException($"配置文件不是有效的 JSON：{ex.Message}", ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProfileExchangeException($"读取导入文件失败：{ex.Message}", ex);
        }

        if (document is null)
        {
            throw new ProfileExchangeException("配置文件内容为空。");
        }

        var existing = await _profiles.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var byName = existing
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? AppContext.BaseDirectory;
        var plan = ImportPlanner.CreatePlan(document, directory, byName);
        _logger.LogInformation("导入预览：{File}，条目 {Count}，schema 支持 {Supported}",
            filePath, plan.Items.Count, plan.SchemaSupported);
        return plan;
    }

    public async Task<ImportResult> ApplyAsync(
        ImportPlan plan,
        IReadOnlySet<int>? selectedIndexes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.SchemaSupported)
        {
            throw new ProfileExchangeException(plan.SchemaError ?? "该配置文件不受支持。");
        }

        var result = new ImportResult();
        foreach (var warning in plan.Warnings)
        {
            result.Warnings.Add(warning);
        }

        var now = _timeProvider.GetUtcNow();
        var batch = new List<LaunchProfile>();
        var existingProfiles = (await _profiles.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(p => p.Id);

        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (selectedIndexes is not null && !selectedIndexes.Contains(item.Index))
            {
                result.Skipped++;
                continue;
            }

            if (item.HasErrors || item.Action == ImportItemAction.Invalid)
            {
                result.Skipped++;
                continue;
            }

            Guid? modelId = null;
            if (item.ModelFileExists && !string.IsNullOrEmpty(item.ResolvedModelPath))
            {
                try
                {
                    var model = await _modelCatalog.EnsureRegisteredAsync(item.ResolvedModelPath!, cancellationToken)
                        .ConfigureAwait(false);
                    modelId = model.Id;
                    result.RegisteredModelNames.Add(model.DisplayName);
                }
                catch (Models.ModelCatalogException ex)
                {
                    result.Warnings.Add($"模型登记失败（{item.ResolvedModelPath}）：{ex.Message}");
                }
            }

            Guid? installationId = null;
            if (!string.IsNullOrEmpty(item.ResolvedExecutablePath) && File.Exists(item.ResolvedExecutablePath))
            {
                var installation = await _installations
                    .FindByExecutablePathAsync(item.ResolvedExecutablePath!, cancellationToken)
                    .ConfigureAwait(false);
                installationId = installation?.Id;
            }

            LaunchProfile profile;
            if (item.Action == ImportItemAction.Overwrite
                && item.ExistingProfileId is { } existingId
                && existingProfiles.TryGetValue(existingId, out var existing))
            {
                profile = existing;
                result.Overwritten++;
            }
            else
            {
                profile = new LaunchProfile { Id = Guid.NewGuid(), CreatedUtc = now };
                result.Added++;
            }

            profile.Name = item.Entry.Name.Trim();
            profile.ModelId = modelId;
            profile.ModelSnapshot = item.Entry.Model is null
                ? null
                : new ModelReferenceSnapshot
                {
                    ModelId = modelId,
                    Name = item.Entry.Model.Name,
                    FilePath = item.ResolvedModelPath ?? item.Entry.Model.FilePath,
                    PathIsRelative = false,
                };
            profile.InstallationId = installationId;
            profile.InstallationSnapshot = item.Entry.Installation is null
                ? null
                : new InstallationReferenceSnapshot
                {
                    InstallationId = installationId,
                    VersionLabel = item.Entry.Installation.VersionLabel,
                    ExecutablePath = item.ResolvedExecutablePath ?? item.Entry.Installation.ExecutablePath,
                    PathIsRelative = false,
                };

            var options = CloneOptions(item.Entry.Options);
            options.ModelPath = item.ResolvedModelPath;
            profile.Options = options;
            profile.ExtraArguments = item.Entry.ExtraArguments;
            profile.AllowExtraArgsOverrideManaged = item.Entry.AllowExtraArgsOverrideManaged;
            profile.EnvironmentVariables = new Dictionary<string, string>(
                item.Entry.EnvironmentVariables, StringComparer.OrdinalIgnoreCase);
            profile.Notes = item.Entry.Notes;
            profile.IsDefault = item.Entry.IsDefault;
            profile.UpdatedUtc = now;

            batch.Add(profile);
        }

        if (batch.Count > 0)
        {
            await _profiles.UpsertManyAsync(batch, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("配置导入完成：新增 {Added}，覆盖 {Overwritten}，跳过 {Skipped}",
            result.Added, result.Overwritten, result.Skipped);
        return result;
    }

    private static ServerLaunchOptions CloneOptions(ServerLaunchOptions options)
    {
        var json = JsonSerializer.Serialize(options, JsonDefaults.Storage);
        return JsonSerializer.Deserialize<ServerLaunchOptions>(json, JsonDefaults.Storage)!
            ?? new ServerLaunchOptions();
    }
}
