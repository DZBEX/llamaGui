using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlamaLauncher.Domain.Workflows.Expressions;

/// <summary>工作流值的统一转换（JSON ↔ 运算值），所有节点与表达式共用。</summary>
public static class WorkflowValueConverter
{
    /// <summary>JSON 节点 → 运算值（bool/double/string/JsonNode/null）。</summary>
    public static object? FromNode(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonArray or JsonObject)
        {
            return node;
        }

        if (node is JsonValue value)
        {
            switch (value.GetValueKind())
            {
                case JsonValueKind.True:
                    return true;

                case JsonValueKind.False:
                    return false;

                case JsonValueKind.String:
                    return value.TryGetValue<string>(out var s) ? s : value.GetValue<string>();

                case JsonValueKind.Number:
                    return ExtractNumber(value);
            }
        }

        return node;
    }

    private static object ExtractNumber(JsonValue value)
    {
        if (value.TryGetValue<double>(out var d))
        {
            return d;
        }

        if (value.TryGetValue<int>(out var i))
        {
            return (double)i;
        }

        if (value.TryGetValue<long>(out var l))
        {
            return (double)l;
        }

        if (value.TryGetValue<decimal>(out var m))
        {
            return (double)m;
        }

        if (value.TryGetValue<float>(out var f))
        {
            return (double)f;
        }

        if (value.TryGetValue<JsonElement>(out var element) && element.TryGetDouble(out var elementValue))
        {
            return elementValue;
        }

        return value;
    }

    /// <summary>运算值 → JSON 节点。</summary>
    public static JsonNode? ToNode(object? value)
        => value switch
        {
            null => null,
            JsonNode node => node,
            bool b => JsonValue.Create(b),
            double d => JsonValue.Create(d),
            int i => JsonValue.Create((double)i),
            long l => JsonValue.Create((double)l),
            decimal m => JsonValue.Create((double)m),
            string s => JsonValue.Create(s),
            _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
        };

    /// <summary>运算值 → 显示文本（字符串原样；数值不带多余小数；容器为紧凑 JSON）。</summary>
    public static string ToDisplayText(object? value)
        => value switch
        {
            null => string.Empty,
            string s => s,
            bool b => b ? "true" : "false",
            double d => FormatNumber(d),
            JsonNode node => WorkflowJson.Compact(node),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };

    /// <summary>数值格式化（整数不带小数点）。</summary>
    public static string FormatNumber(double value)
        => Math.Abs(value % 1) < double.Epsilon && Math.Abs(value) < 9.007199254740992E15
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>值的类型名称（错误提示用）。</summary>
    public static string TypeNameOf(object? value)
        => value switch
        {
            null => "null",
            bool => "布尔",
            double => "数值",
            string => "字符串",
            JsonArray => "数组",
            JsonObject => "对象",
            JsonValue => "JSON 值",
            _ => value.GetType().Name,
        };

    /// <summary>宽松相等（null/数值/布尔/字符串/容器深比较；跨类型返回 false）。</summary>
    public static bool LooseEquals(object? left, object? right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (left is double dl && right is double dr)
        {
            return dl.Equals(dr);
        }

        if (left is bool bl && right is bool br)
        {
            return bl == br;
        }

        if (left is string sl && right is string sr)
        {
            return string.Equals(sl, sr, StringComparison.Ordinal);
        }

        if (left is JsonNode nl && right is JsonNode nr)
        {
            return JsonNode.DeepEquals(nl, nr);
        }

        return false;
    }

    /// <summary>尝试转数值（字符串按不变区域解析）。</summary>
    public static bool TryGetNumber(object? value, out double number)
    {
        switch (value)
        {
            case double d:
                number = d;
                return true;
            case bool b:
                number = b ? 1 : 0;
                return true;
            case string s when double.TryParse(
                s.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed):
                number = parsed;
                return true;
            default:
                number = 0;
                return false;
        }
    }

    /// <summary>尝试转布尔（true/false/1/0，字符串不区分大小写）。</summary>
    public static bool TryGetBoolean(object? value, out bool result)
    {
        switch (value)
        {
            case bool b:
                result = b;
                return true;
            case double d when d is 0 or 1:
                result = d == 1;
                return true;
            case string s:
                if (string.Equals(s.Trim(), "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.Trim(), "1", StringComparison.Ordinal))
                {
                    result = true;
                    return true;
                }

                if (string.Equals(s.Trim(), "false", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.Trim(), "0", StringComparison.Ordinal))
                {
                    result = false;
                    return true;
                }

                break;
        }

        result = false;
        return false;
    }

    /// <summary>把显示文本解析为 JSON 值（用于测试用例与配置文本；非法 JSON 返回 null）。</summary>
    public static JsonNode? ParseDisplayText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        try
        {
            return JsonNode.Parse(trimmed);
        }
        catch (JsonException)
        {
            // 非 JSON：按纯字符串处理（用户输入普通文本时的便利行为）。
            return JsonValue.Create(text);
        }
    }
}
