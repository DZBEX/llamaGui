using System.Text.Json;
using System.Text.Json.Serialization;
using LlamaLauncher.Domain;
using LlamaLauncher.Domain.Validation;

namespace LlamaLauncher.Application.Exchange;

/// <summary>配置档案 JSON 交换文档（版本化 schema）。</summary>
public sealed class ProfileExchangeDocument
{
    /// <summary>当前支持的 schema 版本。</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public DateTimeOffset ExportedUtc { get; set; }

    public string? AppVersion { get; set; }

    public List<ProfileExchangeEntry> Profiles { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }
}

/// <summary>单条配置档案导出项。</summary>
public sealed class ProfileExchangeEntry
{
    public Guid? Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public ModelReferenceSnapshot? Model { get; set; }

    public InstallationReferenceSnapshot? Installation { get; set; }

    public ServerLaunchOptions Options { get; set; } = new();

    public string? ExtraArguments { get; set; }

    public bool AllowExtraArgsOverrideManaged { get; set; }

    public Dictionary<string, string> EnvironmentVariables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Notes { get; set; }

    public bool IsDefault { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }
}

/// <summary>导入计划中单个条目的处理方式。</summary>
public enum ImportItemAction
{
    Invalid = 0,
    Add = 1,
    Overwrite = 2,
    Skip = 3,
}

/// <summary>导入计划条目。</summary>
public sealed class ImportPlanItem
{
    public int Index { get; init; }

    public required ProfileExchangeEntry Entry { get; init; }

    public string ProfileName { get; init; } = string.Empty;

    public ImportItemAction Action { get; set; } = ImportItemAction.Add;

    public Guid? ExistingProfileId { get; set; }

    public List<ValidationIssue> Issues { get; } = new();

    public string? ResolvedModelPath { get; set; }

    public bool ModelFileExists { get; set; }

    public string? ResolvedExecutablePath { get; set; }

    public bool HasErrors => Issues.Any(i => i.IsBlocking);
}

/// <summary>导入预览计划。</summary>
public sealed class ImportPlan
{
    public bool SchemaSupported { get; set; } = true;

    public string? SchemaError { get; set; }

    public List<string> Warnings { get; } = new();

    public List<ImportPlanItem> Items { get; } = new();

    public bool HasBlockingErrors => !SchemaSupported || Items.Any(i => i.HasErrors);

    /// <summary>是否存在可执行的处理（新增或覆盖且无错误）。</summary>
    public bool HasApplicableItems => SchemaSupported
        && Items.Any(i => !i.HasErrors && i.Action is ImportItemAction.Add or ImportItemAction.Overwrite);
}

/// <summary>导入执行结果。</summary>
public sealed class ImportResult
{
    public int Added { get; set; }

    public int Overwritten { get; set; }

    public int Skipped { get; set; }

    public List<string> RegisteredModelNames { get; } = new();

    public List<string> Warnings { get; } = new();
}
