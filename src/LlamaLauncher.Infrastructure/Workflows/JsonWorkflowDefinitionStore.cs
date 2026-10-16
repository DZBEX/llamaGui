using System.Text.Json;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Workflows;
using LlamaLauncher.Domain.Workflows;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.Workflows;

/// <summary>
/// 工作流定义的 JSON 文件存储：<c>workflows/&lt;id&gt;.workflow.json</c>；
/// 版本快照位于 <c>workflows/snapshots/&lt;id&gt;/</c>（不可变）。
/// </summary>
public sealed class JsonWorkflowDefinitionStore : IWorkflowDefinitionStore
{
    private const string FileSuffix = ".workflow.json";

    private readonly string _rootDirectory;
    private readonly string _snapshotsRoot;
    private readonly ILogger<JsonWorkflowDefinitionStore> _logger;

    public JsonWorkflowDefinitionStore(IAppPaths paths, ILogger<JsonWorkflowDefinitionStore> logger)
    {
        _rootDirectory = paths.WorkflowsDirectory;
        _snapshotsRoot = paths.WorkflowSnapshotsDirectory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WorkflowDefinitionSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<WorkflowDefinitionSummary>();
        if (!Directory.Exists(_rootDirectory))
        {
            return results;
        }

        foreach (var file in Directory.EnumerateFiles(_rootDirectory, "*" + FileSuffix))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var text = await WorkflowFileIo.ReadAllTextLimitedAsync(file, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var definition = WorkflowJson.Deserialize<WorkflowDefinition>(text);
                if (definition is null || string.IsNullOrWhiteSpace(definition.Id))
                {
                    continue;
                }

                results.Add(new WorkflowDefinitionSummary(
                    definition.Id,
                    string.IsNullOrWhiteSpace(definition.Name) ? Path.GetFileNameWithoutExtension(file) : definition.Name,
                    definition.Description,
                    definition.Nodes.Count,
                    definition.Edges.Count,
                    definition.UpdatedAt == default ? File.GetLastWriteTimeUtc(file) : definition.UpdatedAt,
                    CountSnapshots(definition.Id)));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
            {
                _logger.LogWarning(ex, "读取工作流摘要失败：{File}", file);
            }
        }

        return results.OrderByDescending(r => r.UpdatedUtc).ToList();
    }

    public async Task<WorkflowLoadResult> LoadAsync(string workflowId, CancellationToken cancellationToken = default)
        => await ReadAndValidateAsync(PathFor(workflowId), cancellationToken).ConfigureAwait(false);

    public async Task SaveAsync(
        WorkflowDefinition definition,
        string? snapshotLabel = null,
        string? snapshotNote = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!WorkflowFileIo.IsSafeId(definition.Id))
        {
            throw new InvalidOperationException("工作流 ID 无效（只允许字母、数字、连字符与下划线）。");
        }

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            throw new InvalidOperationException("工作流名称不能为空。");
        }

        definition.SchemaVersion = WorkflowDefinition.CurrentSchemaVersion;
        definition.UpdatedAt = DateTimeOffset.UtcNow;
        if (definition.CreatedAt == default)
        {
            definition.CreatedAt = definition.UpdatedAt;
        }

        var json = WorkflowJson.Serialize(definition);
        await WorkflowFileIo.WriteAtomicAsync(PathFor(definition.Id), json, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(snapshotLabel))
        {
            await CreateSnapshotAsync(definition, snapshotLabel!, snapshotNote, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "工作流已保存：{Name}（{Id}），节点 {NodeCount} 个，快照标签：{Label}",
            definition.Name,
            definition.Id,
            definition.Nodes.Count,
            snapshotLabel ?? "无");
    }

    public Task<bool> DeleteAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(workflowId);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        _logger.LogInformation("工作流定义已删除：{Id}", workflowId);
        return Task.FromResult(true);
    }

    public async Task<IReadOnlyList<WorkflowVersionSnapshot>> ListSnapshotsAsync(
        string workflowId,
        CancellationToken cancellationToken = default)
    {
        var directory = SnapshotDirectory(workflowId);
        var results = new List<WorkflowVersionSnapshot>();
        if (!Directory.Exists(directory))
        {
            return results;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.snapshot.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var text = await WorkflowFileIo.ReadAllTextLimitedAsync(file, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var snapshot = WorkflowJson.Deserialize<WorkflowVersionSnapshot>(text);
                if (snapshot is not null)
                {
                    results.Add(snapshot);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
            {
                _logger.LogWarning(ex, "读取快照失败：{File}", file);
            }
        }

        return results.OrderByDescending(s => s.CreatedUtc).ToList();
    }

    public async Task<WorkflowVersionSnapshot?> LoadSnapshotAsync(
        string workflowId,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        if (!WorkflowFileIo.IsSafeId(snapshotId))
        {
            return null;
        }

        var path = Path.Combine(SnapshotDirectory(workflowId), snapshotId + ".snapshot.json");
        if (!File.Exists(path))
        {
            return null;
        }

        var text = await WorkflowFileIo.ReadAllTextLimitedAsync(path, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return WorkflowJson.Deserialize<WorkflowVersionSnapshot>(text);
    }

    public async Task<WorkflowLoadResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
        => await ReadAndValidateAsync(sourcePath, cancellationToken).ConfigureAwait(false);

    public async Task ExportAsync(
        WorkflowDefinition definition,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new InvalidOperationException("导出目标路径无效。");
        }

        var json = WorkflowJson.Serialize(definition);
        await WorkflowFileIo.WriteAtomicAsync(destinationPath, json, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("工作流已导出：{Path}", destinationPath);
    }

    private async Task<WorkflowLoadResult> ReadAndValidateAsync(string path, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = await WorkflowFileIo.ReadAllTextLimitedAsync(path, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new WorkflowLoadResult(null, new[]
            {
                new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-IO-001", $"无法读取工作流文件：{ex.Message}", hint: "请检查文件路径与权限后重试。"),
            });
        }

        WorkflowDefinition? definition;
        try
        {
            definition = JsonSerializer.Deserialize<WorkflowDefinition>(text, WorkflowJson.Options);
        }
        catch (JsonException ex)
        {
            return new WorkflowLoadResult(null, new[]
            {
                new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-IO-002", $"工作流文件不是有效的 JSON：{ex.Message}", hint: "请确认文件内容或选择其他文件。"),
            });
        }

        if (definition is null)
        {
            return new WorkflowLoadResult(null, new[]
            {
                new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-IO-003", "工作流文件为空或格式不正确。"),
            });
        }

        var issues = ValidateStructure(definition);
        return new WorkflowLoadResult(definition, issues);
    }

    /// <summary>存储层结构校验（schema、ID、未知节点类型等；业务校验由 WorkflowValidator 完成）。</summary>
    private static List<WorkflowIssue> ValidateStructure(WorkflowDefinition definition)
    {
        var issues = new List<WorkflowIssue>();
        if (definition.SchemaVersion > WorkflowDefinition.CurrentSchemaVersion)
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-SCHEMA-001",
                $"工作流 schema 版本为 {definition.SchemaVersion}，高于当前支持的 {WorkflowDefinition.CurrentSchemaVersion}。",
                hint: "请升级应用后再打开该工作流。"));
        }

        if (!WorkflowFileIo.IsSafeId(definition.Id))
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-IO-004",
                "工作流 ID 缺失或包含不允许的字符。",
                hint: "ID 只允许字母、数字、连字符与下划线（最长 64 字符）。"));
        }

        foreach (var node in definition.Nodes)
        {
            var descriptor = NodeTypeCatalog.Find(node.TypeId);
            if (descriptor is null)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF-NODE-UNKNOWN",
                    $"节点“{node.Name}”使用了未知类型“{node.TypeId}”，已保留原始定义以便诊断。",
                    node.Id,
                    hint: "安装提供该节点类型的扩展，或替换为受支持的节点后再运行。"));
                continue;
            }

            if (node.TypeVersion > descriptor.Version)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Warning,
                    "WF-NODE-VERSION",
                    $"节点“{node.Name}”的类型版本（{node.TypeVersion}）高于当前支持版本（{descriptor.Version}），可能存在不兼容配置。",
                    node.Id));
            }
        }

        return issues;
    }

    private async Task CreateSnapshotAsync(
        WorkflowDefinition definition,
        string label,
        string? note,
        CancellationToken cancellationToken)
    {
        var snapshotId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + SanitizeSlug(label);
        var snapshot = new WorkflowVersionSnapshot
        {
            SnapshotId = snapshotId,
            WorkflowId = definition.Id,
            Label = label,
            Note = note,
            CreatedUtc = DateTimeOffset.UtcNow,
            Definition = definition.Clone(),
        };

        var path = Path.Combine(SnapshotDirectory(definition.Id), snapshotId + ".snapshot.json");
        await WorkflowFileIo.WriteAtomicAsync(path, WorkflowJson.Serialize(snapshot), cancellationToken)
            .ConfigureAwait(false);
    }

    private static string SanitizeSlug(string label)
    {
        var chars = label.Trim().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var slug = new string(chars).Trim('-');
        return string.IsNullOrEmpty(slug) ? "snapshot" : slug[..Math.Min(32, slug.Length)];
    }

    private int CountSnapshots(string workflowId)
    {
        var directory = SnapshotDirectory(workflowId);
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.snapshot.json").Count() : 0;
    }

    private string PathFor(string workflowId)
    {
        if (!WorkflowFileIo.IsSafeId(workflowId))
        {
            throw new InvalidOperationException("工作流 ID 无效（只允许字母、数字、连字符与下划线）。");
        }

        return Path.Combine(_rootDirectory, workflowId + FileSuffix);
    }

    private string SnapshotDirectory(string workflowId)
    {
        if (!WorkflowFileIo.IsSafeId(workflowId))
        {
            throw new InvalidOperationException("工作流 ID 无效（只允许字母、数字、连字符与下划线）。");
        }

        return Path.Combine(_snapshotsRoot, workflowId);
    }
}
