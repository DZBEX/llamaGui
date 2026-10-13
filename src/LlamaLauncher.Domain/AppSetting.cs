namespace LlamaLauncher.Domain;

/// <summary>版本化设置的键值记录（JSON 文本值）。</summary>
public sealed class AppSetting
{
    public string Key { get; set; } = string.Empty;

    public string ValueJson { get; set; } = "null";

    public DateTimeOffset UpdatedUtc { get; set; }
}
