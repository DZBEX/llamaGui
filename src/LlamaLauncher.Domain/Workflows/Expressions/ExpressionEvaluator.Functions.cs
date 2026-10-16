using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LlamaLauncher.Domain.Workflows.Expressions;

/// <summary>白名单函数库（无任意脚本、无反射、无 I/O；集合遍历有上限）。</summary>
internal sealed partial class ExpressionEvaluator
{
    /// <summary>全部可用函数名（错误提示与 UI 帮助共用）。</summary>
    public static readonly IReadOnlyList<string> FunctionNames = new[]
    {
        "len", "count", "first", "last", "isEmpty",
        "lower", "upper", "trim", "substring", "contains", "startsWith", "endsWith", "indexOf", "concat", "matches",
        "toNumber", "toString", "toBool",
        "abs", "min", "max", "round", "floor", "ceil", "sum", "avg",
        "isNull", "isNumber", "isString", "isArray", "isObject", "isBool",
        "coalesce", "get", "keys", "values",
    };

    private object? EvaluateCall(CallNode node, JsonObject scope)
    {
        Step(node.Position);
        var name = node.Name;
        switch (name)
        {
            case "coalesce":
            {
                RequireArity(node, 2, int.MaxValue);
                foreach (var arg in node.Args)
                {
                    var value = Evaluate(arg, scope);
                    if (value is not null)
                    {
                        return value;
                    }
                }

                return null;
            }

            case "len":
            {
                RequireArity(node, 1, 1);
                var value = EvalArg(node, 0, scope);
                return LengthOf(value, node.Position)
                    ?? throw TypeError($"len() 需要字符串、数组或对象，实际为 {WorkflowValueConverter.TypeNameOf(value)}。", node.Position);
            }

            case "count":
            {
                RequireArity(node, 1, 1);
                var value = EvalArg(node, 0, scope);
                return value is null ? 0d : LengthOf(value, node.Position) ?? throw TypeError($"count() 需要字符串、数组或对象，实际为 {WorkflowValueConverter.TypeNameOf(value)}。", node.Position);
            }

            case "first":
            case "last":
            {
                RequireArity(node, 1, 1);
                var value = EvalArg(node, 0, scope);
                if (value is not JsonArray array)
                {
                    throw TypeError($"{name}() 需要数组，实际为 {WorkflowValueConverter.TypeNameOf(value)}。", node.Position);
                }

                EnsureCollectionSize(array.Count, node.Position);
                if (array.Count == 0)
                {
                    return null;
                }

                return WorkflowValueConverter.FromNode(name == "first" ? array[0] : array[^1]);
            }

            case "isEmpty":
            {
                RequireArity(node, 1, 1);
                var value = EvalArg(node, 0, scope);
                return value switch
                {
                    null => true,
                    string s => s.Length == 0,
                    JsonArray a => a.Count == 0,
                    JsonObject o => o.Count == 0,
                    _ => false,
                };
            }

            case "lower":
            case "upper":
            case "trim":
            {
                RequireArity(node, 1, 1);
                var text = RequireString(EvalArg(node, 0, scope), name, node.Position);
                return name switch
                {
                    "lower" => text.ToLowerInvariant(),
                    "upper" => text.ToUpperInvariant(),
                    _ => text.Trim(),
                };
            }

            case "substring":
            {
                RequireArity(node, 2, 3);
                var text = RequireString(EvalArg(node, 0, scope), name, node.Position);
                var start = RequireNumber(EvalArg(node, 1, scope), name, node.Position);
                var startIndex = CheckIndex(start, text.Length, node.Position);
                if (node.Args.Count == 2)
                {
                    return text[startIndex..];
                }

                var length = RequireNumber(EvalArg(node, 2, scope), name, node.Position);
                if (length < 0)
                {
                    throw TypeError("substring() 的长度不能为负数。", node.Position);
                }

                var take = Math.Min((int)Math.Floor(length), text.Length - startIndex);
                return text.Substring(startIndex, take);
            }

            case "contains":
            {
                RequireArity(node, 2, 2);
                var haystack = EvalArg(node, 0, scope);
                var needle = EvalArg(node, 1, scope);
                switch (haystack)
                {
                    case string text:
                        return text.Contains(WorkflowValueConverter.ToDisplayText(needle), StringComparison.Ordinal);
                    case JsonArray array:
                        EnsureCollectionSize(array.Count, node.Position);
                        return array.Any(item => WorkflowValueConverter.LooseEquals(WorkflowValueConverter.FromNode(item), needle));
                    default:
                        throw TypeError($"contains() 的第一个参数需要字符串或数组，实际为 {WorkflowValueConverter.TypeNameOf(haystack)}。", node.Position);
                }
            }

            case "startsWith":
            case "endsWith":
            {
                RequireArity(node, 2, 2);
                var text = RequireString(EvalArg(node, 0, scope), name, node.Position);
                var part = RequireString(EvalArg(node, 1, scope), name, node.Position);
                return name == "startsWith"
                    ? text.StartsWith(part, StringComparison.Ordinal)
                    : text.EndsWith(part, StringComparison.Ordinal);
            }

            case "indexOf":
            {
                RequireArity(node, 2, 2);
                var text = RequireString(EvalArg(node, 0, scope), name, node.Position);
                var part = RequireString(EvalArg(node, 1, scope), name, node.Position);
                return (double)text.IndexOf(part, StringComparison.Ordinal);
            }

            case "concat":
            {
                RequireArity(node, 2, int.MaxValue);
                var builder = new System.Text.StringBuilder();
                for (var i = 0; i < node.Args.Count; i++)
                {
                    builder.Append(WorkflowValueConverter.ToDisplayText(EvalArg(node, i, scope)));
                }

                return builder.ToString();
            }

            case "matches":
            {
                RequireArity(node, 2, 2);
                var text = RequireString(EvalArg(node, 0, scope), name, node.Position);
                var pattern = RequireString(EvalArg(node, 1, scope), name, node.Position);
                if (pattern.Length > 512)
                {
                    throw TypeError("matches() 的正则表达式过长（上限 512 字符）。", node.Position);
                }

                try
                {
                    var regex = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(250));
                    return regex.IsMatch(text);
                }
                catch (ArgumentException ex)
                {
                    throw TypeError($"matches() 的正则表达式无效（不支持回溯类结构）：{ex.Message}", node.Position);
                }
            }

            case "toNumber":
            {
                RequireArity(node, 1, 1);
                var value = EvalArg(node, 0, scope);
                if (WorkflowValueConverter.TryGetNumber(value, out var number))
                {
                    return number;
                }

                throw TypeError($"toNumber() 无法把 {WorkflowValueConverter.TypeNameOf(value)}（{WorkflowValueConverter.ToDisplayText(value)}）转换为数值。", node.Position);
            }

            case "toString":
            {
                RequireArity(node, 1, 1);
                return WorkflowValueConverter.ToDisplayText(EvalArg(node, 0, scope));
            }

            case "toBool":
            {
                RequireArity(node, 1, 1);
                var value = EvalArg(node, 0, scope);
                if (WorkflowValueConverter.TryGetBoolean(value, out var result))
                {
                    return result;
                }

                throw TypeError($"toBool() 无法把 {WorkflowValueConverter.TypeNameOf(value)}（{WorkflowValueConverter.ToDisplayText(value)}）转换为布尔值。", node.Position);
            }

            case "abs":
            {
                RequireArity(node, 1, 1);
                return Math.Abs(RequireNumber(EvalArg(node, 0, scope), name, node.Position));
            }

            case "min":
            case "max":
            {
                RequireArity(node, 2, int.MaxValue);
                var best = RequireNumber(EvalArg(node, 0, scope), name, node.Position);
                for (var i = 1; i < node.Args.Count; i++)
                {
                    var candidate = RequireNumber(EvalArg(node, i, scope), name, node.Position);
                    best = name == "min" ? Math.Min(best, candidate) : Math.Max(best, candidate);
                }

                return best;
            }

            case "round":
            {
                RequireArity(node, 1, 2);
                var value = RequireNumber(EvalArg(node, 0, scope), name, node.Position);
                var digits = node.Args.Count == 2
                    ? RequireNumber(EvalArg(node, 1, scope), name, node.Position)
                    : 0;
                if (digits is < 0 or > 15)
                {
                    throw TypeError("round() 的小数位需在 0–15 之间。", node.Position);
                }

                return Math.Round(value, (int)digits, MidpointRounding.AwayFromZero);
            }

            case "floor":
            {
                RequireArity(node, 1, 1);
                return Math.Floor(RequireNumber(EvalArg(node, 0, scope), name, node.Position));
            }

            case "ceil":
            {
                RequireArity(node, 1, 1);
                return Math.Ceiling(RequireNumber(EvalArg(node, 0, scope), name, node.Position));
            }

            case "sum":
            case "avg":
            {
                RequireArity(node, 1, 1);
                var value = EvalArg(node, 0, scope);
                if (value is not JsonArray array)
                {
                    throw TypeError($"{name}() 需要数组，实际为 {WorkflowValueConverter.TypeNameOf(value)}。", node.Position);
                }

                EnsureCollectionSize(array.Count, node.Position);
                var total = 0d;
                foreach (var item in array)
                {
                    total += RequireNumber(WorkflowValueConverter.FromNode(item), name, node.Position);
                }

                if (name == "sum")
                {
                    return total;
                }

                if (array.Count == 0)
                {
                    throw TypeError("avg() 无法对空数组求平均。", node.Position);
                }

                return total / array.Count;
            }

            case "isNull":
            case "isNumber":
            case "isString":
            case "isArray":
            case "isObject":
            case "isBool":
            {
                RequireArity(node, 1, 1);
                var value = EvalArg(node, 0, scope);
                return name switch
                {
                    "isNull" => value is null,
                    "isNumber" => value is double,
                    "isString" => value is string,
                    "isArray" => value is JsonArray,
                    "isObject" => value is JsonObject,
                    _ => value is bool,
                };
            }

            case "get":
            {
                RequireArity(node, 2, 3);
                var target = EvalArg(node, 0, scope);
                var key = WorkflowValueConverter.ToDisplayText(EvalArg(node, 1, scope));
                if (target is JsonObject obj)
                {
                    if (obj.TryGetPropertyValue(key, out var found))
                    {
                        return WorkflowValueConverter.FromNode(found);
                    }
                }
                else if (target is JsonArray array && int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                {
                    if (index >= 0 && index < array.Count)
                    {
                        return WorkflowValueConverter.FromNode(array[index]);
                    }
                }
                else if (target is not null)
                {
                    throw TypeError($"get() 的第一个参数需要对象或数组，实际为 {WorkflowValueConverter.TypeNameOf(target)}。", node.Position);
                }

                return node.Args.Count == 3 ? EvalArg(node, 2, scope) : null;
            }

            case "keys":
            case "values":
            {
                RequireArity(node, 1, 1);
                var value = EvalArg(node, 0, scope);
                if (value is not JsonObject sourceObject)
                {
                    throw TypeError($"{name}() 需要对象，实际为 {WorkflowValueConverter.TypeNameOf(value)}。", node.Position);
                }

                EnsureCollectionSize(sourceObject.Count, node.Position);
                var result = new JsonArray();
                foreach (var pair in sourceObject)
                {
                    result.Add(name == "keys" ? JsonValue.Create(pair.Key) : pair.Value?.DeepClone());
                }

                return result;
            }

            default:
                throw TypeError(
                    $"不支持的函数“{name}”。可用函数：{string.Join("、", FunctionNames)}。",
                    node.Position);
        }
    }

    private object? EvalArg(CallNode node, int index, JsonObject scope)
        => Evaluate(node.Args[index], scope);

    private static void RequireArity(CallNode node, int min, int max)
    {
        if (node.Args.Count < min || node.Args.Count > max)
        {
            var expected = max == int.MaxValue
                ? $"至少 {min} 个"
                : min == max ? $"{min} 个" : $"{min}–{max} 个";
            throw TypeError($"{node.Name}() 需要{expected}参数，实际为 {node.Args.Count} 个。", node.Position);
        }
    }

    private static WorkflowExpressionException TypeError(string message, int position)
        => new(message, position, isRuntime: true);

    private static double? LengthOf(object? value, int position) => value switch
    {
        string s => s.Length,
        JsonArray a => CheckedCount(a.Count, position),
        JsonObject o => CheckedCount(o.Count, position),
        _ => null,
    };

    private static double CheckedCount(int count, int position)
    {
        EnsureCollectionSize(count, position);
        return count;
    }

    private static bool EnsureCollectionSize(int count, int position)
    {
        if (count > WorkflowLimits.MaxCollectionElements)
        {
            throw TypeError($"集合元素数量 {count} 超过上限 {WorkflowLimits.MaxCollectionElements}。", position);
        }

        return true;
    }

    private static string RequireString(object? value, string function, int position)
        => value is string text
            ? text
            : throw TypeError($"{function}() 需要字符串参数，实际为 {WorkflowValueConverter.TypeNameOf(value)}。", position);

    private static double RequireNumber(object? value, string function, int position)
        => value is double number
            ? number
            : throw TypeError($"{function}() 需要数值参数，实际为 {WorkflowValueConverter.TypeNameOf(value)}。", position);

    private static int CheckIndex(double value, int textLength, int position)
    {
        if (value < 0 || value != Math.Floor(value) || value > textLength)
        {
            throw TypeError($"substring() 的起始位置 {value} 无效（字符串长度 {textLength}）。", position);
        }

        return (int)value;
    }
}
