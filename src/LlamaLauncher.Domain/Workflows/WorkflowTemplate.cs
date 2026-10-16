using System.Text;
using System.Text.Json.Nodes;
using LlamaLauncher.Domain.Workflows.Expressions;

namespace LlamaLauncher.Domain.Workflows;

/// <summary>
/// 受限文本模板：把 <c>{{ 路径 }}</c> 替换为作用域中的值（仅支持纯字段路径，不支持运算与函数）。
/// 用 <c>\{{</c> 表示字面量。
/// </summary>
public static class WorkflowTemplate
{
    private const string OpenToken = "{{";
    private const string CloseToken = "}}";

    /// <summary>校验模板语法；返回 null 表示有效，并输出全部占位符路径（如 input.message）。</summary>
    public static string? Validate(string? text, out IReadOnlyList<string> placeholderPaths)
    {
        placeholderPaths = Array.Empty<string>();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.Length > WorkflowLimits.MaxTemplateLength)
        {
            return $"模板过长（上限 {WorkflowLimits.MaxTemplateLength} 字符）。";
        }

        var paths = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf(OpenToken, i, StringComparison.Ordinal);
            if (open < 0)
            {
                break;
            }

            if (IsEscaped(text, open))
            {
                i = open + OpenToken.Length;
                continue;
            }

            var close = text.IndexOf(CloseToken, open + OpenToken.Length, StringComparison.Ordinal);
            if (close < 0)
            {
                return $"模板占位符缺少“{CloseToken}”。";
            }

            var content = text[(open + OpenToken.Length)..close].Trim();
            if (content.Length == 0)
            {
                return "模板占位符内容为空。";
            }

            if (!WorkflowExpression.TryParse(content, out var expression, out var parseError))
            {
                return $"模板占位符“{{{{ {content} }}}}”无效：{parseError}";
            }

            if (expression!.PurePath is null)
            {
                return $"模板占位符“{{{{ {content} }}}}”只支持字段路径（如 input.message），不支持运算或函数。";
            }

            paths.Add(content);
            i = close + CloseToken.Length;
        }

        placeholderPaths = paths;
        return null;
    }

    /// <summary>提取模板占位符路径（按出现顺序）。</summary>
    public static IReadOnlyList<string> ExtractReferences(string? text)
        => Validate(text, out var paths) is null ? paths : Array.Empty<string>();

    /// <summary>渲染模板；求值为 null 的占位符输出为空并在 missing 中记录其路径。</summary>
    public static string Render(string? text, JsonObject scope, out IReadOnlyList<string> missing)
    {
        missing = Array.Empty<string>();
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var missingList = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf(OpenToken, i, StringComparison.Ordinal);
            if (open < 0)
            {
                builder.Append(text, i, text.Length - i);
                break;
            }

            if (IsEscaped(text, open))
            {
                builder.Append(text, i, open - 1 - i);
                builder.Append(OpenToken);
                i = open + OpenToken.Length;
                continue;
            }

            builder.Append(text, i, open - i);
            var close = text.IndexOf(CloseToken, open + OpenToken.Length, StringComparison.Ordinal);
            if (close < 0)
            {
                throw new WorkflowExpressionException($"模板占位符缺少“{CloseToken}”。");
            }

            var content = text[(open + OpenToken.Length)..close].Trim();
            if (!WorkflowExpression.TryParse(content, out var expression, out var parseError) || expression!.PurePath is null)
            {
                throw new WorkflowExpressionException(
                    $"模板占位符“{{{{ {content} }}}}”无效：{parseError ?? "仅支持字段路径"}。");
            }

            if (expression.TryEvaluateToJson(scope, out var value, out var evalError))
            {
                if (value is null)
                {
                    missingList.Add(content);
                }
                else
                {
                    builder.Append(WorkflowJson.ToDisplayString(value));
                }
            }
            else
            {
                throw new WorkflowExpressionException($"模板占位符“{{{{ {content} }}}}”求值失败：{evalError}");
            }

            i = close + CloseToken.Length;
        }

        missing = missingList;
        return builder.ToString();
    }

    private static bool IsEscaped(string text, int openIndex)
        => openIndex > 0 && text[openIndex - 1] == '\\';
}
