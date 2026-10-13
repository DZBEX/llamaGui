using System.Text.RegularExpressions;
using LlamaLauncher.Domain;

namespace LlamaLauncher.Application.Capabilities;

/// <summary>
/// 解析 llama-server --help 输出为受支持参数列表。
/// 设计目标：容忍不同版本的排版变化；无法识别的行只忽略，不抛异常。
/// </summary>
public static partial class HelpTextParser
{
    [GeneratedRegex(@"^\s*((?:-{1,2}[^\s,=]+)(?:\s*,\s*-{1,2}[^\s,=]+)*)\s*(.*)$")]
    private static partial Regex OptionLinePattern();

    public static List<SupportedOption> ParseOptions(string? helpText)
    {
        var results = new List<SupportedOption>();
        if (string.IsNullOrWhiteSpace(helpText))
        {
            return results;
        }

        var byCanonical = new Dictionary<string, SupportedOption>(StringComparer.OrdinalIgnoreCase);
        SupportedOption? current = null;

        foreach (var rawLine in helpText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                current = null;
                continue;
            }

            // 分节标题（如 "----- common params -----"）不参与解析
            if (line.TrimStart().StartsWith("---", StringComparison.Ordinal))
            {
                current = null;
                continue;
            }

            var match = OptionLinePattern().Match(line);
            if (match.Success && match.Groups[1].Value.Contains('-'))
            {
                var flags = match.Groups[1].Value
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Where(f => f.StartsWith("-", StringComparison.Ordinal))
                    .ToList();
                if (flags.Count == 0)
                {
                    continue;
                }

                var remainder = match.Groups[2].Value.Trim();
                var parsed = ExtractFlagsValuesAndDescription(flags, remainder);
                flags = parsed.Flags;
                var valueKind = parsed.Kind;
                var valueHint = parsed.Hint;
                var description = parsed.Description;

                var definition = flags
                    .Select(LlamaOptionCatalog.FindByFlag)
                    .FirstOrDefault(d => d is not null);
                var canonical = definition?.CanonicalName
                    ?? NormalizeUnknownFlag(flags);

                if (!byCanonical.TryGetValue(canonical, out var option))
                {
                    option = new SupportedOption { CanonicalName = canonical };
                    byCanonical[canonical] = option;
                    results.Add(option);
                }

                foreach (var flag in flags)
                {
                    if (!option.Flags.Contains(flag, StringComparer.Ordinal))
                    {
                        option.Flags.Add(flag);
                    }
                }

                if (option.ValueKind == OptionValueKind.None)
                {
                    option.ValueKind = valueKind;
                }

                if (valueKind != OptionValueKind.None)
                {
                    option.ValueHint ??= valueHint;
                }

                if (!string.IsNullOrEmpty(description))
                {
                    option.Description = CombineDescription(option.Description, description);
                }

                current = option;
            }
            else if (current is not null && IsLikelyContinuationLine(line))
            {
                current.Description = CombineDescription(current.Description, line.Trim());
            }
            else
            {
                current = null;
            }
        }

        return results;
    }

    /// <summary>从版本输出中提取版本标签（优先 "version: N"，否则取首个非空行）。</summary>
    public static string? ExtractVersionLabel(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText))
        {
            return null;
        }

        var match = Regex.Match(versionText, @"version:\s*([0-9]+)");
        if (match.Success)
        {
            return "b" + match.Groups[1].Value;
        }

        var firstLine = versionText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrEmpty(firstLine))
        {
            return null;
        }

        return firstLine.Length <= 80 ? firstLine : firstLine[..80];
    }

    /// <summary>
    /// 兼容两种常见排版：
    /// "  -c, --ctx-size &lt;N&gt;   description"（flag 集中在前）与
    /// "  -c N, --ctx-size N      description"（flag 与占位符交错）。
    /// </summary>
    private static (List<string> Flags, OptionValueKind Kind, string? Hint, string? Description)
        ExtractFlagsValuesAndDescription(List<string> initialFlags, string remainder)
    {
        var flags = new List<string>(initialFlags);
        if (string.IsNullOrEmpty(remainder))
        {
            return (flags, OptionValueKind.None, null, null);
        }

        string? hint = null;
        var kind = OptionValueKind.None;
        var lastWasFlag = false;
        var index = 0;

        while (index < remainder.Length)
        {
            while (index < remainder.Length && remainder[index] == ' ')
            {
                index++;
            }

            var wordStart = index;
            while (index < remainder.Length && remainder[index] != ' ')
            {
                index++;
            }

            if (wordStart == index)
            {
                break;
            }

            var word = remainder[wordStart..index];
            if (IsFlagWord(word))
            {
                if (!flags.Contains(word, StringComparer.Ordinal))
                {
                    flags.Add(word);
                }

                lastWasFlag = true;
                continue;
            }

            if (IsPlaceholderWord(word) && (lastWasFlag || hint is null))
            {
                hint ??= word;
                if (kind == OptionValueKind.None)
                {
                    kind = word.StartsWith("[", StringComparison.Ordinal)
                        ? OptionValueKind.Optional
                        : OptionValueKind.Required;
                }

                lastWasFlag = false;
                continue;
            }

            index = wordStart;
            break;
        }

        var description = remainder[index..].Trim();
        return (flags, kind, hint, description.Length == 0 ? null : description);
    }

    private static bool IsFlagWord(string word)
        => word.Length > 1 && word[0] == '-' && !char.IsDigit(word[1]);

    private static bool IsPlaceholderWord(string word)
    {
        if (word.Length is 0 or > 32)
        {
            return false;
        }

        if (word.StartsWith("<", StringComparison.Ordinal) && word.Contains('>'))
        {
            return true;
        }

        if (word.StartsWith("[", StringComparison.Ordinal) && word.Contains(']'))
        {
            return true;
        }

        if (word.StartsWith("{", StringComparison.Ordinal) && word.Contains('}'))
        {
            return true;
        }

        return Regex.IsMatch(word, @"^[A-Z][A-Z0-9_,\-/]*$");
    }

    private static string NormalizeUnknownFlag(List<string> flags)
    {
        var preferred = flags.FirstOrDefault(f => f.StartsWith("--", StringComparison.Ordinal)) ?? flags[0];
        return preferred.TrimStart('-');
    }

    private static bool IsLikelyContinuationLine(string line)
    {
        if (line.TrimStart().StartsWith("-", StringComparison.Ordinal))
        {
            return false;
        }

        // 缩进明显的续行（描述换行）
        return line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith("\t", StringComparison.Ordinal);
    }

    private static string CombineDescription(string? existing, string addition)
        => existing is null ? addition : existing + " " + addition;
}
