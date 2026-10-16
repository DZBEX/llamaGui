using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LlamaLauncher.Domain.Workflows;

/// <summary>工作流相关的 JSON 序列化与数据帮助方法（统一规则，避免各处自定义）。</summary>
public static class WorkflowJson
{
    /// <summary>统一序列化选项：camelCase、枚举字符串、忽略 null、不转义非 ASCII。</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        MaxDepth = 64,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>序列化对象为 JSON 字符串。</summary>
    public static string Serialize<T>(T value, bool indented = true)
    {
        if (indented)
        {
            return JsonSerializer.Serialize(value, Options);
        }

        var compact = new JsonSerializerOptions(Options) { WriteIndented = false };
        return JsonSerializer.Serialize(value, compact);
    }

    /// <summary>反序列化 JSON 字符串；失败返回 null。</summary>
    public static T? Deserialize<T>(string json)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>尝试解析 JSON 文本（语法错误返回 null 与错误信息）。</summary>
    public static JsonNode? TryParse(string? text, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(
                text,
                nodeOptions: null,
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 64,
                });
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>深拷贝 JSON 节点（null 安全）。</summary>
    public static JsonNode? Clone(JsonNode? node) => node?.DeepClone();

    /// <summary>紧凑显示文本。</summary>
    public static string Compact(JsonNode? node)
        => node is null
            ? "null"
            : node.ToJsonString(new JsonSerializerOptions(Options) { WriteIndented = false });

    /// <summary>把 JSON 节点转为友好显示文本（字符串显示原文，其余显示紧凑 JSON）。</summary>
    public static string ToDisplayString(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        return node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : node.ToJsonString();
    }

    /// <summary>把类型化配置对象转为节点配置 JsonObject。</summary>
    public static JsonObject ToConfigObject(object? config)
    {
        if (config is null)
        {
            return new JsonObject();
        }

        return JsonSerializer.SerializeToNode(config, Options) as JsonObject ?? new JsonObject();
    }

    /// <summary>从节点配置 JsonObject 读取类型化配置（失败返回 null 并输出错误）。</summary>
    public static T? TryGetConfig<T>(JsonObject? config, out string? error)
        where T : class, new()
    {
        error = null;
        if (config is null)
        {
            return new T();
        }

        try
        {
            return config.Deserialize<T>(Options) ?? new T();
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return null;
        }
    }
}

/// <summary>工作流相关大小与数量限制（安全边界，所有入口共用）。</summary>
public static class WorkflowLimits
{
    /// <summary>工作流定义文件大小上限（字节）。</summary>
    public const int MaxDefinitionFileBytes = 4 * 1024 * 1024;

    /// <summary>测试用例文件大小上限（字节）。</summary>
    public const int MaxTestCaseFileBytes = 4 * 1024 * 1024;

    /// <summary>表达式最大长度（字符）。</summary>
    public const int MaxExpressionLength = 4_000;

    /// <summary>表达式求值最大操作步数。</summary>
    public const int MaxExpressionOperations = 100_000;

    /// <summary>模板最大长度（字符）。</summary>
    public const int MaxTemplateLength = 100_000;

    /// <summary>单节点输入/输出快照默认显示上限（字符）。</summary>
    public const int MaxSnapshotChars = 262_144;

    /// <summary>运行日志单次运行最多保留条数。</summary>
    public const int MaxLogEntriesPerRun = 2_000;

    /// <summary>默认运行历史保留条数。</summary>
    public const int DefaultHistoryRetention = 200;

    /// <summary>函数式调用（如 len/first）对集合处理的最大元素数。</summary>
    public const int MaxCollectionElements = 10_000;
}
