using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlamaLauncher.Domain;

/// <summary>项目统一的 JSON 序列化配置。</summary>
public static class JsonDefaults
{
    /// <summary>存储与内部传输用（紧凑）。</summary>
    public static readonly JsonSerializerOptions Storage = Create(indented: false);

    /// <summary>导出与人工查看用（缩进）。</summary>
    public static readonly JsonSerializerOptions Export = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = indented,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) },
    };
}
