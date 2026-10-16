using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LlamaLauncher.Domain.Workflows.Expressions;

/// <summary>受限表达式的求值器（带操作预算与清晰错误定位；无动态执行）。</summary>
internal sealed partial class ExpressionEvaluator
{
    private readonly int _budget;
    private int _used;

    public ExpressionEvaluator(int budget = WorkflowLimits.MaxExpressionOperations)
        => _budget = Math.Max(1, budget);

    public object? Evaluate(ExpressionNode node, JsonObject scope)
    {
        Step(node.Position);
        return node switch
        {
            LiteralNode literal => literal.Value,
            PathNode path => EvaluatePath(path, scope),
            UnaryNode unary => EvaluateUnary(unary, scope),
            BinaryNode binary => EvaluateBinary(binary, scope),
            CallNode call => EvaluateCall(call, scope),
            _ => throw new WorkflowExpressionException("不支持的表达式节点。", node.Position, isRuntime: true),
        };
    }

    private void Step(int position)
    {
        _used++;
        if (_used > _budget)
        {
            throw new WorkflowExpressionException(
                "表达式求值超出操作上限（表达式过于复杂或数据集合过大）。",
                position,
                isRuntime: true);
        }
    }

    private object? EvaluatePath(PathNode node, JsonObject scope)
    {
        if (node.Segments.Count == 0 || node.Segments[0] is not PropertySegment rootSegment)
        {
            throw new WorkflowExpressionException("表达式必须以变量名开头（如 input、vars、up、meta）。", node.Position, isRuntime: true);
        }

        if (!scope.TryGetPropertyValue(rootSegment.Name, out var current))
        {
            return null;
        }

        object? value = WorkflowValueConverter.FromNode(current);
        for (var i = 1; i < node.Segments.Count; i++)
        {
            Step(node.Segments[i].Position);
            if (value is null)
            {
                return null;
            }

            switch (node.Segments[i])
            {
                case PropertySegment property when value is JsonObject obj:
                    value = obj.TryGetPropertyValue(property.Name, out var child)
                        ? WorkflowValueConverter.FromNode(child)
                        : null;
                    break;

                case IndexSegment index when value is JsonArray array:
                    value = index.Index < array.Count
                        ? WorkflowValueConverter.FromNode(array[index.Index])
                        : null;
                    break;

                case PropertySegment property:
                    throw new WorkflowExpressionException(
                        $"值为 {WorkflowValueConverter.TypeNameOf(value)}，无法读取字段“{property.Name}”。",
                        property.Position,
                        isRuntime: true);

                case IndexSegment indexSegment:
                    throw new WorkflowExpressionException(
                        $"值为 {WorkflowValueConverter.TypeNameOf(value)}，无法使用索引 [{indexSegment.Index}]。",
                        indexSegment.Position,
                        isRuntime: true);
            }
        }

        return value;
    }

    private object? EvaluateUnary(UnaryNode node, JsonObject scope)
    {
        var operand = Evaluate(node.Operand, scope);
        switch (node.Op)
        {
            case "!":
                if (operand is bool b)
                {
                    return !b;
                }

                throw new WorkflowExpressionException(
                    $"“!” 需要布尔值，实际为 {WorkflowValueConverter.TypeNameOf(operand)}。",
                    node.Position,
                    isRuntime: true);

            case "-":
                if (WorkflowValueConverter.TryGetNumber(operand, out var number) && operand is not bool and not string)
                {
                    return -number;
                }

                throw new WorkflowExpressionException(
                    $"一元“-”需要数值，实际为 {WorkflowValueConverter.TypeNameOf(operand)}。",
                    node.Position,
                    isRuntime: true);

            default:
                throw new WorkflowExpressionException($"不支持的一元运算“{node.Op}”。", node.Position, isRuntime: true);
        }
    }

    private object? EvaluateBinary(BinaryNode node, JsonObject scope)
    {
        switch (node.Op)
        {
            case "&&":
            {
                var left = RequireBool(Evaluate(node.Left, scope), node.Left.Position, "&&");
                if (!left)
                {
                    return false;
                }

                return RequireBool(Evaluate(node.Right, scope), node.Right.Position, "&&");
            }

            case "||":
            {
                var left = RequireBool(Evaluate(node.Left, scope), node.Left.Position, "||");
                if (left)
                {
                    return true;
                }

                return RequireBool(Evaluate(node.Right, scope), node.Right.Position, "||");
            }

            case "??":
            {
                var left = Evaluate(node.Left, scope);
                return left ?? Evaluate(node.Right, scope);
            }
        }

        var l = Evaluate(node.Left, scope);
        var r = Evaluate(node.Right, scope);
        switch (node.Op)
        {
            case "==":
                return WorkflowValueConverter.LooseEquals(l, r);

            case "!=":
                return !WorkflowValueConverter.LooseEquals(l, r);

            case "in":
                return EvaluateIn(l, r, node.Position);

            case "<":
            case "<=":
            case ">":
            case ">=":
                return EvaluateComparison(node.Op, l, r, node.Position);

            case "+":
                if (l is string || r is string)
                {
                    return WorkflowValueConverter.ToDisplayText(l) + WorkflowValueConverter.ToDisplayText(r);
                }

                if (l is double ln && r is double rn)
                {
                    return ln + rn;
                }

                throw new WorkflowExpressionException(
                    $"“+” 需要两个数值，或至少一侧为字符串；实际为 {WorkflowValueConverter.TypeNameOf(l)} 与 {WorkflowValueConverter.TypeNameOf(r)}。",
                    node.Position,
                    isRuntime: true);

            case "-":
            case "*":
            case "/":
            case "%":
                return EvaluateArithmetic(node.Op, l, r, node.Position);

            default:
                throw new WorkflowExpressionException($"不支持的运算“{node.Op}”。", node.Position, isRuntime: true);
        }
    }

    private static bool RequireBool(object? value, int position, string op)
    {
        if (value is bool b)
        {
            return b;
        }

        throw new WorkflowExpressionException(
            $"“{op}” 需要布尔值，实际为 {WorkflowValueConverter.TypeNameOf(value)}。",
            position,
            isRuntime: true);
    }

    private static object EvaluateIn(object? left, object? right, int position)
    {
        switch (right)
        {
            case JsonArray array:
                for (var i = 0; i < array.Count && i < WorkflowLimits.MaxCollectionElements; i++)
                {
                    if (WorkflowValueConverter.LooseEquals(left, WorkflowValueConverter.FromNode(array[i])))
                    {
                        return true;
                    }
                }

                return false;

            case string text when left is string needle:
                return text.Contains(needle, StringComparison.Ordinal);

            case JsonObject obj when left is string key:
                return obj.ContainsKey(key);

            default:
                throw new WorkflowExpressionException(
                    $"“in” 右侧需要数组、字符串或对象，实际为 {WorkflowValueConverter.TypeNameOf(right)}。",
                    position,
                    isRuntime: true);
        }
    }

    private static object EvaluateComparison(string op, object? left, object? right, int position)
    {
        int comparison;
        if (left is double dl && right is double dr)
        {
            comparison = dl.CompareTo(dr);
        }
        else if (left is string sl && right is string sr)
        {
            comparison = string.CompareOrdinal(sl, sr);
        }
        else
        {
            throw new WorkflowExpressionException(
                $"比较“{op}”需要同为数值或同为字符串；实际为 {WorkflowValueConverter.TypeNameOf(left)} 与 {WorkflowValueConverter.TypeNameOf(right)}。",
                position,
                isRuntime: true);
        }

        return op switch
        {
            "<" => comparison < 0,
            "<=" => comparison <= 0,
            ">" => comparison > 0,
            ">=" => comparison >= 0,
            _ => false,
        };
    }

    private static object EvaluateArithmetic(string op, object? left, object? right, int position)
    {
        if (left is not double l || right is not double r)
        {
            throw new WorkflowExpressionException(
                $"“{op}” 需要两个数值；实际为 {WorkflowValueConverter.TypeNameOf(left)} 与 {WorkflowValueConverter.TypeNameOf(right)}。",
                position,
                isRuntime: true);
        }

        return op switch
        {
            "-" => l - r,
            "*" => l * r,
            "/" => r == 0
                ? throw new WorkflowExpressionException("除数为 0。", position, isRuntime: true)
                : l / r,
            "%" => r == 0
                ? throw new WorkflowExpressionException("取余运算的除数为 0。", position, isRuntime: true)
                : l % r,
            _ => throw new WorkflowExpressionException($"不支持的运算“{op}”。", position, isRuntime: true),
        };
    }
}
