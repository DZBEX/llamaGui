using System.Text.RegularExpressions;

namespace LlamaLauncher.Application.Security;

/// <summary>日志、命令行预览、历史与诊断包统一的秘密脱敏工具。</summary>
public static partial class SecretMasker
{
    public const string Mask = "******";

    [GeneratedRegex(@"(?i)(--api-key(?:\s+|=))(\S+)")]
    private static partial Regex ApiKeyArgPattern();

    [GeneratedRegex(@"(?i)(""api[_-]?key""\s*:\s*"")([^""]*)(?="")")]
    private static partial Regex ApiKeyJsonPattern();

    [GeneratedRegex(@"(?i)(authorization\s*:\s*bearer\s+)(\S+)")]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?i)([?&]api[_-]?key=)([^&\s]+)")]
    private static partial Regex ApiKeyQueryPattern();

    [GeneratedRegex(@"(?i)(""(?:token|access_token|api_key)""\s*:\s*"")([^""]*)(?="")")]
    private static partial Regex TokenJsonPattern();

    /// <summary>对文本脱敏：先替换已知秘密值，再按模式处理常见秘密写法。</summary>
    public static string MaskText(string? text, IEnumerable<string>? knownSecrets = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = text;
        if (knownSecrets is not null)
        {
            foreach (var secret in knownSecrets)
            {
                if (!string.IsNullOrEmpty(secret) && secret.Length >= 4)
                {
                    result = result.Replace(secret, Mask, StringComparison.Ordinal);
                }
            }
        }

        result = ApiKeyArgPattern().Replace(result, m => m.Groups[1].Value + Mask);
        result = ApiKeyJsonPattern().Replace(result, m => m.Groups[1].Value + Mask);
        result = BearerPattern().Replace(result, m => m.Groups[1].Value + Mask);
        result = ApiKeyQueryPattern().Replace(result, m => m.Groups[1].Value + Mask);
        result = TokenJsonPattern().Replace(result, m => m.Groups[1].Value + Mask);
        return result;
    }

    /// <summary>属性名是否可能包含敏感信息（用于日志字段级过滤）。</summary>
    public static bool IsSensitiveName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var lowered = name.ToLowerInvariant();
        return lowered.Contains("apikey", StringComparison.Ordinal)
            || lowered.Contains("api_key", StringComparison.Ordinal)
            || lowered.Contains("api-key", StringComparison.Ordinal)
            || lowered.Contains("authorization", StringComparison.Ordinal)
            || lowered.Contains("token", StringComparison.Ordinal)
            || lowered.Contains("secret", StringComparison.Ordinal)
            || lowered.Contains("password", StringComparison.Ordinal);
    }
}
