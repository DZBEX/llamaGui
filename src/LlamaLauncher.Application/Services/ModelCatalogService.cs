using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Models;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Application.Services;

/// <summary>模型库服务实现。</summary>
public sealed class ModelCatalogService : IModelCatalogService
{
    private const string GgufExtension = ".gguf";

    private readonly IModelRepository _repository;
    private readonly IGgufMetadataReader _metadataReader;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ModelCatalogService> _logger;

    public ModelCatalogService(
        IModelRepository repository,
        IGgufMetadataReader metadataReader,
        TimeProvider timeProvider,
        ILogger<ModelCatalogService> logger)
    {
        _repository = repository;
        _metadataReader = metadataReader;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task<IReadOnlyList<ModelRecord>> GetAllAsync(CancellationToken cancellationToken = default)
        => _repository.GetAllAsync(cancellationToken);

    public Task<ModelRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => _repository.GetByIdAsync(id, cancellationToken);

    public async Task<ModelRecord> AddAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizePath(filePath);
        var existing = await _repository.FindByCanonicalPathAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            throw new ModelCatalogException($"该模型已登记：{existing.DisplayName}");
        }

        var record = await CreateRecordAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (record.Status != ModelFileStatus.Ok)
        {
            throw new ModelCatalogException(DescribeStatus(record.Status));
        }

        await _repository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("已登记模型 {Name}（{Path}），状态 {Status}", record.Name, record.FilePath, record.Status);
        return record;
    }

    public async Task<ModelRecord> EnsureRegisteredAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizePath(filePath);
        var existing = await _repository.FindByCanonicalPathAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var record = await CreateRecordAsync(normalized, cancellationToken).ConfigureAwait(false);
        await _repository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<IReadOnlyList<ModelRecord>> ScanDirectoryAsync(
        string directory,
        bool includeSubdirectories,
        IProgress<ModelScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new ModelCatalogException($"目录不存在：{directory}");
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = includeSubdirectories,
        };

        var files = Directory.EnumerateFiles(directory, "*" + GgufExtension, options).ToList();
        List<ModelRecord> added = new();
        List<ModelRecord> existing = new();
        var examined = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            examined++;
            progress?.Report(new ModelScanProgress(files.Count, examined, file));

            try
            {
                var normalized = NormalizePath(file);
                var already = await _repository.FindByCanonicalPathAsync(normalized, cancellationToken).ConfigureAwait(false);
                if (already is not null)
                {
                    existing.Add(already);
                    continue;
                }

                var record = await CreateRecordAsync(normalized, cancellationToken).ConfigureAwait(false);
                if (record.Status != ModelFileStatus.Ok)
                {
                    _logger.LogWarning("扫描跳过无效模型文件 {Path}：{Status}", file, record.Status);
                    continue;
                }

                await _repository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
                added.Add(record);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "扫描模型文件失败：{Path}", file);
            }
        }

        _logger.LogInformation("目录扫描完成：{Directory}，发现 {Total} 个文件，新增 {Added}，跳过已登记 {Existing}",
            directory, files.Count, added.Count, existing.Count);
        return added;
    }

    public async Task<ModelRecord> RevalidateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var record = await _repository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new ModelCatalogException("模型记录不存在。");

        var now = _timeProvider.GetUtcNow();
        record.LastCheckedUtc = now;

        if (!File.Exists(record.CanonicalPath))
        {
            record.Status = ModelFileStatus.Missing;
            record.UpdatedUtc = now;
            await _repository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
            return record;
        }

        var info = new FileInfo(record.CanonicalPath);
        var sizeChanged = record.FileSizeBytes != info.Length;
        var writeChanged = record.FileLastWriteUtc != new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        record.FileSizeBytes = info.Length;
        record.FileLastWriteUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

        if (sizeChanged || writeChanged || record.MetadataStatus != MetadataParseStatus.Parsed)
        {
            await RefreshMetadataAsync(record, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            record.Status = ModelFileStatus.Ok;
        }

        record.UpdatedUtc = now;
        await _repository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<IReadOnlyList<ModelRecord>> RevalidateAllAsync(CancellationToken cancellationToken = default)
    {
        var all = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ModelRecord>(all.Count);
        foreach (var model in all)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RevalidateAsync(model.Id, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
        => _repository.DeleteAsync(id, cancellationToken);

    public async Task UpdateDetailsAsync(
        Guid id,
        string? alias,
        IReadOnlyList<string>? tags,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        var record = await _repository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new ModelCatalogException("模型记录不存在。");

        record.Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        if (tags is not null)
        {
            record.Tags = tags
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        record.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        record.UpdatedUtc = _timeProvider.GetUtcNow();
        await _repository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ModelRecord> CreateRecordAsync(string canonicalPath, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var record = new ModelRecord
        {
            Id = Guid.NewGuid(),
            FilePath = canonicalPath,
            CanonicalPath = canonicalPath,
            Name = Path.GetFileNameWithoutExtension(canonicalPath),
            CreatedUtc = now,
            UpdatedUtc = now,
            LastCheckedUtc = now,
        };

        var basic = CheckBasicFile(canonicalPath);
        if (basic != ModelFileStatus.Ok)
        {
            record.Status = basic;
            record.MetadataStatus = MetadataParseStatus.NotParsed;
            record.MetadataError = DescribeStatus(basic);
            return record;
        }

        var fileInfo = new FileInfo(canonicalPath);
        record.FileSizeBytes = fileInfo.Length;
        record.FileLastWriteUtc = new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero);

        await RefreshMetadataAsync(record, cancellationToken).ConfigureAwait(false);
        return record;
    }

    private async Task RefreshMetadataAsync(ModelRecord record, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _metadataReader.ReadAsync(record.CanonicalPath, cancellationToken).ConfigureAwait(false);
            if (result.Success && result.Info is not null)
            {
                record.Metadata = result.Info;
                record.MetadataStatus = MetadataParseStatus.Parsed;
                record.MetadataError = null;
                record.Status = ModelFileStatus.Ok;
                if (!string.IsNullOrWhiteSpace(result.Info.Name))
                {
                    record.Name = result.Info.Name!;
                }
            }
            else if (!result.IsGguf)
            {
                record.Metadata = null;
                record.MetadataStatus = MetadataParseStatus.Failed;
                record.MetadataError = result.Error;
                record.Status = ModelFileStatus.InvalidFormat;
            }
            else
            {
                record.Metadata = null;
                record.MetadataStatus = MetadataParseStatus.Failed;
                record.MetadataError = result.Error;
                record.Status = ModelFileStatus.Unreadable;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            record.Metadata = null;
            record.MetadataStatus = MetadataParseStatus.Failed;
            record.MetadataError = ex.Message;
            record.Status = ModelFileStatus.Unreadable;
        }
    }

    private static ModelFileStatus CheckBasicFile(string path)
    {
        if (!File.Exists(path))
        {
            return ModelFileStatus.Missing;
        }

        if (!path.EndsWith(GgufExtension, StringComparison.OrdinalIgnoreCase))
        {
            return ModelFileStatus.InvalidExtension;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length == 0)
            {
                return ModelFileStatus.Empty;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> magic = stackalloc byte[4];
            var read = stream.Read(magic);
            if (read < 4
                || magic[0] != (byte)'G'
                || magic[1] != (byte)'G'
                || magic[2] != (byte)'U'
                || magic[3] != (byte)'F')
            {
                return ModelFileStatus.InvalidFormat;
            }

            return ModelFileStatus.Ok;
        }
        catch (UnauthorizedAccessException)
        {
            return ModelFileStatus.Unreadable;
        }
        catch (IOException)
        {
            return ModelFileStatus.Unreadable;
        }
    }

    private static string DescribeStatus(ModelFileStatus status) => status switch
    {
        ModelFileStatus.Missing => "文件不存在。",
        ModelFileStatus.InvalidExtension => "扩展名不是 .gguf。",
        ModelFileStatus.Empty => "文件为空。",
        ModelFileStatus.Unreadable => "文件无法读取（可能被占用或权限不足）。",
        ModelFileStatus.InvalidFormat => "文件头不是 GGUF 格式。",
        _ => "文件状态未知。",
    };

    private static string NormalizePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ModelCatalogException("文件路径不能为空。");
        }

        var full = Path.GetFullPath(filePath.Trim());
        return full;
    }
}
