using System.Text.Json.Nodes;
using LlamaLauncher.Domain.Workflows;
using LlamaLauncher.Domain.Workflows.Expressions;

namespace LlamaLauncher.Application.Workflows;

/// <summary>执行器共用的表达式/模板/类型帮助。</summary>
internal static class ExecutionHelpers
{
    /// <summary>求值 JSON 表达式；失败时给出带用途说明的错误。</summary>
    public static bool TryEvaluate(
        NodeExecutionContext context,
        string? expression,
        string purpose,
        bool requireValue,
        out JsonNode? value,
        out NodeExecutionError? error)
    {
        value = null;
        error = null;
        if (string.IsNullOrWhiteSpace(expression))
        {
            if (requireValue)
            {
                error = new NodeExecutionError(NodeErrorKind.Failed, $"{purpose}未配置表达式。");
                return false;
            }

            return true;
        }

        if (!WorkflowExpression.TryParse(expression, out var parsed, out var parseError))
        {
            error = new NodeExecutionError(NodeErrorKind.Failed, $"{purpose}表达式无效：{parseError}", "ExpressionSyntax");
            return false;
        }

        if (parsed!.TryEvaluateToJson(context.Scope, out value, out var evalError))
        {
            if (requireValue && value is null)
            {
                error = new NodeExecutionError(
                    NodeErrorKind.Failed,
                    $"{purpose}求值为空（表达式：{expression}）。请检查上游输出或提供默认值。",
                    "ExpressionEmpty");
                return false;
            }

            return true;
        }

        error = new NodeExecutionError(NodeErrorKind.Failed, $"{purpose}求值失败：{evalError}", "ExpressionRuntime");
        return false;
    }

    /// <summary>求值布尔表达式。</summary>
    public static bool TryEvaluateBool(
        NodeExecutionContext context,
        string? expression,
        string purpose,
        bool defaultWhenEmpty,
        out bool value,
        out NodeExecutionError? error)
    {
        value = defaultWhenEmpty;
        error = null;
        if (string.IsNullOrWhiteSpace(expression))
        {
            return true;
        }

        if (!WorkflowExpression.TryParse(expression, out var parsed, out var parseError))
        {
            error = new NodeExecutionError(NodeErrorKind.Failed, $"{purpose}表达式无效：{parseError}", "ExpressionSyntax");
            return false;
        }

        if (parsed!.TryEvaluateBool(context.Scope, out value, out var evalError))
        {
            return true;
        }

        error = new NodeExecutionError(NodeErrorKind.Failed, $"{purpose}求值失败：{evalError}", "ExpressionRuntime");
        return false;
    }

    /// <summary>渲染模板；失败给出错误。</summary>
    public static bool TryRenderTemplate(
        NodeExecutionContext context,
        string? template,
        string purpose,
        out string text,
        out NodeExecutionError? error)
    {
        text = string.Empty;
        error = null;
        if (string.IsNullOrEmpty(template))
        {
            return true;
        }

        try
        {
            text = WorkflowTemplate.Render(template, context.Scope, out var missing);
            if (missing.Count > 0)
            {
                context.Log(
                    WorkflowLogLevel.Warning,
                    $"{purpose}中的占位符未取到值（已按空字符串处理）：{string.Join("、", missing)}",
                    null);
            }

            return true;
        }
        catch (WorkflowExpressionException ex)
        {
            error = new NodeExecutionError(NodeErrorKind.Failed, $"{purpose}渲染失败：{ex.Message}", "TemplateRender");
            return false;
        }
    }

    /// <summary>读取节点类型化配置；失败给出错误。</summary>
    public static bool TryGetConfig<T>(
        NodeExecutionContext context,
        out T? config,
        out NodeExecutionError? error)
        where T : class, new()
    {
        config = WorkflowJson.TryGetConfig<T>(context.Node.Config, out var message);
        if (config is null)
        {
            error = new NodeExecutionError(NodeErrorKind.Failed, $"节点配置无法解析：{message}", "ConfigInvalid");
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>按转换名转换值。</summary>
    public static bool TryConvert(JsonNode? value, string? convert, out JsonNode? result, out string? error)
    {
        result = value;
        error = null;
        if (string.IsNullOrWhiteSpace(convert))
        {
            return true;
        }

        var raw = WorkflowValueConverter.FromNode(value);
        switch (convert.Trim().ToLowerInvariant())
        {
            case "number":
                if (WorkflowValueConverter.TryGetNumber(raw, out var number))
                {
                    result = JsonValue.Create(number);
                    return true;
                }

                error = $"无法把 {WorkflowValueConverter.TypeNameOf(raw)} 转换为 number。";
                return false;

            case "string":
                result = JsonValue.Create(WorkflowValueConverter.ToDisplayText(raw));
                return true;

            case "boolean":
                if (WorkflowValueConverter.TryGetBoolean(raw, out var boolean))
                {
                    result = JsonValue.Create(boolean);
                    return true;
                }

                error = $"无法把 {WorkflowValueConverter.TypeNameOf(raw)} 转换为 boolean。";
                return false;

            case "json":
                if (value is JsonObject or JsonArray)
                {
                    return true;
                }

                if (raw is string text)
                {
                    var parsed = WorkflowJson.TryParse(text, out var parseError);
                    if (parsed is not null)
                    {
                        result = parsed;
                        return true;
                    }

                    error = $"字符串内容不是有效 JSON：{parseError}";
                    return false;
                }

                error = $"无法把 {WorkflowValueConverter.TypeNameOf(raw)} 转换为 json。";
                return false;

            default:
                error = $"不支持的转换“{convert}”（可用：number、string、boolean、json）。";
                return false;
        }
    }
}
