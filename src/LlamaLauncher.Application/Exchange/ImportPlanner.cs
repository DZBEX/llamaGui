using LlamaLauncher.Domain;
using LlamaLauncher.Domain.Validation;

namespace LlamaLauncher.Application.Exchange;

/// <summary>导入计划生成（纯逻辑，可单测）。</summary>
public static class ImportPlanner
{
    /// <summary>根据解析出的文档与现有档案生成导入预览。</summary>
    public static ImportPlan CreatePlan(
        ProfileExchangeDocument document,
        string sourceFileDirectory,
        IReadOnlyDictionary<string, Guid> existingProfilesByName)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(existingProfilesByName);

        var plan = new ImportPlan();

        if (document.SchemaVersion <= 0)
        {
            plan.SchemaSupported = false;
            plan.SchemaError = "配置文件缺少有效的 schemaVersion。";
            return plan;
        }

        if (document.SchemaVersion > ProfileExchangeDocument.CurrentSchemaVersion)
        {
            plan.SchemaSupported = false;
            plan.SchemaError =
                $"配置文件的 schemaVersion 为 {document.SchemaVersion}，高于当前支持的版本 " +
                $"{ProfileExchangeDocument.CurrentSchemaVersion}。请升级 LlamaLauncher 后再导入。";
            return plan;
        }

        if (document.Profiles.Count == 0)
        {
            plan.Warnings.Add("配置文件中不包含任何配置档案。");
        }

        if (document.UnknownFields is { Count: > 0 })
        {
            plan.Warnings.Add($"配置文件包含当前版本未识别的字段：{string.Join(", ", document.UnknownFields.Keys)}（已忽略）。");
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < document.Profiles.Count; i++)
        {
            var entry = document.Profiles[i];
            var item = new ImportPlanItem
            {
                Index = i,
                Entry = entry,
                ProfileName = entry.Name ?? string.Empty,
            };
            plan.Items.Add(item);

            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                item.Action = ImportItemAction.Invalid;
                item.Issues.Add(new ValidationIssue(ValidationSeverity.Error, "name", "配置名称不能为空。"));
                continue;
            }

            if (!seenNames.Add(entry.Name.Trim()))
            {
                item.Action = ImportItemAction.Invalid;
                item.Issues.Add(new ValidationIssue(ValidationSeverity.Error, "name", $"配置名称“{entry.Name}”在导入文件中重复。"));
                continue;
            }

            if (entry.Options is null)
            {
                item.Action = ImportItemAction.Invalid;
                item.Issues.Add(new ValidationIssue(ValidationSeverity.Error, "options", "缺少启动参数。"));
                continue;
            }

            if (entry.UnknownFields is { Count: > 0 })
            {
                item.Issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning,
                    "unknown",
                    $"条目包含未识别字段：{string.Join(", ", entry.UnknownFields.Keys)}（已忽略）。"));
            }

            // 模型路径解析：相对路径相对导入文件所在目录；缺失仅警示，不自动搜索磁盘。
            if (entry.Model is not null && !string.IsNullOrWhiteSpace(entry.Model.FilePath))
            {
                var resolved = ResolvePath(entry.Model.FilePath!, entry.Model.PathIsRelative, sourceFileDirectory);
                item.ResolvedModelPath = resolved;
                item.ModelFileExists = resolved is not null && File.Exists(resolved);
                if (!item.ModelFileExists)
                {
                    item.Issues.Add(new ValidationIssue(
                        ValidationSeverity.Warning,
                        "model",
                        $"模型文件不存在：{resolved ?? entry.Model.FilePath}",
                        "导入后请在配置中重新定位模型；启动器不会自动搜索磁盘。"));
                }
            }
            else
            {
                item.Issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning,
                    "model",
                    "配置档案未包含模型引用，导入后需手动选择模型。"));
            }

            // 可执行文件路径解析（仅提示，不阻塞）
            if (entry.Installation is not null && !string.IsNullOrWhiteSpace(entry.Installation.ExecutablePath))
            {
                var resolved = ResolvePath(
                    entry.Installation.ExecutablePath!,
                    entry.Installation.PathIsRelative,
                    sourceFileDirectory);
                item.ResolvedExecutablePath = resolved;
                if (resolved is null || !File.Exists(resolved))
                {
                    item.Issues.Add(new ValidationIssue(
                        ValidationSeverity.Warning,
                        "installation",
                        $"llama.cpp 程序路径不存在：{resolved ?? entry.Installation.ExecutablePath}",
                        "导入后需重新选择 llama-server.exe。"));
                }
            }

            // 参数值域校验
            foreach (var issue in LaunchOptionsValidator.ValidateValues(
                entry.Options,
                new LaunchValidationContext
                {
                    ModelFilePath = item.ResolvedModelPath ?? "（待重新定位）",
                    ExecutablePath = item.ResolvedExecutablePath ?? "（待重新选择）",
                    PortInUse = false,
                    ApiKeyConfigured = true,
                    LanAccessConfirmed = true,
                }))
            {
                // 导入预览阶段：端口占用与安全确认属于运行期问题，此处只保留其他问题。
                if (issue.Field is "ApiKey" or "LanAccess")
                {
                    continue;
                }

                item.Issues.Add(issue);
            }

            if (existingProfilesByName.TryGetValue(entry.Name.Trim(), out var existingId))
            {
                item.Action = ImportItemAction.Overwrite;
                item.ExistingProfileId = existingId;
            }
            else
            {
                item.Action = ImportItemAction.Add;
            }
        }

        return plan;
    }

    private static string? ResolvePath(string path, bool isRelative, string baseDirectory)
    {
        try
        {
            return isRelative
                ? Path.GetFullPath(Path.Combine(baseDirectory, path))
                : Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
