using System.Text.Json.Nodes;

namespace LlamaLauncher.Domain.Workflows.Expressions;

/// <summary>
/// 已解析的安全表达式。语法与求值均受限于白名单（比较/逻辑/数值/字符串/空值/白名单函数），
/// 不执行任意 C#、脚本或系统命令。
/// </summary>
public sealed class WorkflowExpression
{
    private readonly ExpressionNode _root;

    private WorkflowExpression(string source, ExpressionNode root, IReadOnlyList<string>? purePath)
    {
        Source = source;
        _root = root;
        PurePath = purePath;
    }

    /// <summary>表达式原文。</summary>
    public string Source { get; }

    /// <summary>
    /// 当表达式是纯路径（如 <c>input.message</c>、<c>up.n1.out.confidence</c>、<c>vars.items[0]</c>）时，
    /// 返回按段拆分的路径（段：字段名或数字索引字符串）；否则为 null。
    /// </summary>
    public IReadOnlyList<string>? PurePath { get; }

    /// <summary>可用函数名列表（UI 帮助与错误提示共用）。</summary>
    public static IReadOnlyList<string> FunctionNames => ExpressionEvaluator.FunctionNames;

    /// <summary>解析表达式；语法错误抛出 <see cref="WorkflowExpressionException"/>。</summary>
    public static WorkflowExpression Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new WorkflowExpressionException("表达式不能为空。", 0);
        }

        if (text.Length > WorkflowLimits.MaxExpressionLength)
        {
            throw new WorkflowExpressionException($"表达式过长（上限 {WorkflowLimits.MaxExpressionLength} 字符）。", 0);
        }

        var root = ExpressionParser.Parse(text);
        return new WorkflowExpression(text, root, ExtractPurePath(root));
    }

    /// <summary>尝试解析表达式。</summary>
    public static bool TryParse(string? text, out WorkflowExpression? expression, out string? error)
    {
        expression = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "表达式不能为空。";
            return false;
        }

        try
        {
            expression = Parse(text);
            return true;
        }
        catch (WorkflowExpressionException ex)
        {
            error = ex.ToUserMessage();
            return false;
        }
    }

    /// <summary>校验表达式语法；返回 null 表示有效。</summary>
    public static string? Validate(string? text)
        => TryParse(text, out _, out var error) ? null : error;

    /// <summary>求值为 JSON 值；运行期错误抛出异常。</summary>
    public JsonNode? EvaluateToJson(JsonObject scope)
    {
        try
        {
            return WorkflowValueConverter.ToNode(EvaluateRaw(scope));
        }
        catch (WorkflowExpressionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new WorkflowExpressionException($"表达式求值失败：{ex.Message}", _root.Position, isRuntime: true);
        }
    }

    /// <summary>尝试求值为 JSON 值；返回 false 时给出用户可读错误。</summary>
    public bool TryEvaluateToJson(JsonObject scope, out JsonNode? value, out string? error)
    {
        try
        {
            value = EvaluateToJson(scope);
            error = null;
            return true;
        }
        catch (WorkflowExpressionException ex)
        {
            value = null;
            error = ex.ToUserMessage();
            return false;
        }
    }

    /// <summary>求值为布尔（条件判断用）；非布尔结果是错误。</summary>
    public bool EvaluateBool(JsonObject scope, string purpose = "条件")
    {
        var value = EvaluateRaw(scope);
        if (value is bool b)
        {
            return b;
        }

        throw new WorkflowExpressionException(
            $"{purpose}需要布尔值，实际为 {WorkflowValueConverter.TypeNameOf(value)}（{WorkflowValueConverter.ToDisplayText(value)}）。",
            _root.Position,
            isRuntime: true);
    }

    /// <summary>尝试求值为布尔。</summary>
    public bool TryEvaluateBool(JsonObject scope, out bool value, out string? error)
    {
        try
        {
            value = EvaluateBool(scope);
            error = null;
            return true;
        }
        catch (WorkflowExpressionException ex)
        {
            value = false;
            error = ex.ToUserMessage();
            return false;
        }
    }

    /// <summary>内部求值（运算值）。</summary>
    internal object? EvaluateRaw(JsonObject scope)
    {
        var evaluator = new ExpressionEvaluator();
        return evaluator.Evaluate(_root, scope);
    }

    private static IReadOnlyList<string>? ExtractPurePath(ExpressionNode root)
    {
        if (root is not PathNode path)
        {
            return null;
        }

        var segments = new List<string>(path.Segments.Count);
        foreach (var segment in path.Segments)
        {
            switch (segment)
            {
                case PropertySegment property:
                    segments.Add(property.Name);
                    break;
                case IndexSegment index:
                    segments.Add(index.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                default:
                    return null;
            }
        }

        return segments;
    }
}
