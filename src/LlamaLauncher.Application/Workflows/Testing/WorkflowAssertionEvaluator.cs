using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LlamaLauncher.Domain.Workflows;
using LlamaLauncher.Domain.Workflows.Expressions;

namespace LlamaLauncher.Application.Workflows.Testing;

/// <summary>单条断言的评估结果。</summary>
public sealed record AssertionOutcome(
    string AssertionId,
    string Name,
    bool Passed,
    WorkflowIssueSeverity Severity,
    string Message)
{
    /// <summary>是否导致用例失败。</summary>
    public bool IsFailure => !Passed && Severity == WorkflowIssueSeverity.Error;
}

/// <summary>断言报告。</summary>
public sealed class WorkflowAssertionReport
{
    public required IReadOnlyList<AssertionOutcome> Outcomes { get; init; }

    /// <summary>是否全部通过（无 Error 级失败）。</summary>
    public bool Passed => Outcomes.All(o => !o.IsFailure);

    /// <summary>失败（Error 级）数量。</summary>
    public int FailedCount => Outcomes.Count(o => o.IsFailure);

    /// <summary>摘要文本。</summary>
    public string Summary => Outcomes.Count == 0
        ? "没有配置断言。"
        : Passed
            ? $"断言全部通过（{Outcomes.Count} 条）。"
            : $"断言失败：{FailedCount} / {Outcomes.Count} 条。";
}

/// <summary>
/// 测试用例断言评估：实际结果与预期比较（格式、必填、类型、枚举/范围、自定义断言）。
/// 评分与通过判定完全基于显式规则，不做主观推断。
/// </summary>
public static class WorkflowAssertionEvaluator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>构建断言作用域：output（工作流输出）、vars（最终变量）、meta、records（节点结果）。</summary>
    public static JsonObject BuildScope(WorkflowRunResult run)
    {
        var records = new JsonObject();
        foreach (var node in run.Record.Nodes)
        {
            records[node.NodeId] = new JsonObject
            {
                ["state"] = node.State.ToString(),
                ["output"] = string.IsNullOrWhiteSpace(node.OutputJson)
                    ? null
                    : WorkflowJson.TryParse(node.OutputJson, out _),
                ["error"] = node.ErrorMessage,
                ["branch"] = node.BranchTaken,
                ["retries"] = node.Retries,
                ["durationMs"] = node.DurationMs,
            };
        }

        return new JsonObject
        {
            ["output"] = run.Output?.DeepClone(),
            ["vars"] = string.IsNullOrWhiteSpace(run.Record.FinalVariablesJson)
                ? new JsonObject()
                : WorkflowJson.TryParse(run.Record.FinalVariablesJson, out _) ?? new JsonObject(),
            ["meta"] = new JsonObject
            {
                ["runId"] = run.Record.RunId,
                ["state"] = run.Record.State.ToString(),
                ["testMode"] = run.Record.TestMode.ToString(),
                ["durationMs"] = run.Record.DurationMs,
            },
            ["records"] = records,
        };
    }

    /// <summary>评估测试用例的全部断言。</summary>
    public static WorkflowAssertionReport Evaluate(WorkflowTestCase testCase, WorkflowRunResult run)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(run);
        var scope = BuildScope(run);
        var outcomes = new List<AssertionOutcome>();
        foreach (var assertion in testCase.Assertions)
        {
            outcomes.Add(EvaluateSingle(assertion, scope));
        }

        return new WorkflowAssertionReport { Outcomes = outcomes };
    }

    /// <summary>评估单条断言。</summary>
    public static AssertionOutcome EvaluateSingle(WorkflowAssertion assertion, JsonObject scope)
    {
        var name = string.IsNullOrWhiteSpace(assertion.Name) ? assertion.Id : assertion.Name;
        if (!WorkflowExpression.TryParse(assertion.Target, out var expression, out var parseError))
        {
            return new AssertionOutcome(
                assertion.Id,
                name,
                false,
                assertion.Severity,
                $"目标表达式无效：{parseError}");
        }

        if (!expression!.TryEvaluateToJson(scope, out var actual, out var evalError))
        {
            return new AssertionOutcome(
                assertion.Id,
                name,
                false,
                assertion.Severity,
                $"目标表达式求值失败：{evalError}");
        }

        JsonNode? expected = null;
        if (!string.IsNullOrWhiteSpace(assertion.ExpectedJson))
        {
            expected = WorkflowJson.TryParse(assertion.ExpectedJson, out var expectedError);
            if (expected is null && assertion.Operator != AssertionOperator.Exists && assertion.Operator != AssertionOperator.NotExists)
            {
                return new AssertionOutcome(
                    assertion.Id,
                    name,
                    false,
                    assertion.Severity,
                    $"期望值不是有效 JSON：{expectedError}");
            }
        }

        try
        {
            var (passed, detail) = EvaluateOperator(assertion, actual, expected);
            return new AssertionOutcome(
                assertion.Id,
                name,
                passed,
                assertion.Severity,
                passed ? "通过。" : detail);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new AssertionOutcome(assertion.Id, name, false, assertion.Severity, $"断言执行失败：{ex.Message}");
        }
    }

    private static (bool Passed, string Detail) EvaluateOperator(
        WorkflowAssertion assertion,
        JsonNode? actual,
        JsonNode? expected)
    {
        var actualValue = WorkflowValueConverter.FromNode(actual);
        var expectedValue = WorkflowValueConverter.FromNode(expected);
        switch (assertion.Operator)
        {
            case AssertionOperator.Equals:
                return WorkflowValueConverter.LooseEquals(actualValue, expectedValue)
                    ? (true, string.Empty)
                    : (false, $"期望 {Describe(expected)}，实际 {Describe(actual)}");

            case AssertionOperator.NotEquals:
                return !WorkflowValueConverter.LooseEquals(actualValue, expectedValue)
                    ? (true, string.Empty)
                    : (false, $"值不应等于 {Describe(expected)}");

            case AssertionOperator.Contains:
                return Contains(actual, expected)
                    ? (true, string.Empty)
                    : (false, $"实际值不包含 {Describe(expected)}（实际 {Describe(actual)}）");

            case AssertionOperator.NotContains:
                return !Contains(actual, expected)
                    ? (true, string.Empty)
                    : (false, $"实际值不应包含 {Describe(expected)}");

            case AssertionOperator.GreaterThan:
            case AssertionOperator.GreaterThanOrEqual:
            case AssertionOperator.LessThan:
            case AssertionOperator.LessThanOrEqual:
                return CompareOrdered(assertion.Operator, actualValue, expectedValue, actual, expected);

            case AssertionOperator.Exists:
                return actual is null
                    ? (false, "期望存在，但实际为空。")
                    : (true, string.Empty);

            case AssertionOperator.NotExists:
                return actual is null
                    ? (true, string.Empty)
                    : (false, $"期望不存在，但实际为 {Describe(actual)}");

            case AssertionOperator.IsTrue:
                return actualValue is true ? (true, string.Empty) : (false, $"期望 true，实际 {Describe(actual)}");

            case AssertionOperator.IsFalse:
                return actualValue is false ? (true, string.Empty) : (false, $"期望 false，实际 {Describe(actual)}");

            case AssertionOperator.RegexMatch:
            {
                if (WorkflowValueConverter.ToDisplayText(actualValue) is not { } text)
                {
                    return (false, "实际值无法转换为文本。");
                }

                var pattern = WorkflowValueConverter.ToDisplayText(expectedValue);
                if (string.IsNullOrEmpty(pattern))
                {
                    return (false, "正则表达式为空。");
                }

                if (pattern.Length > 512)
                {
                    return (false, "正则表达式过长（上限 512 字符）。");
                }

                var regex = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, RegexTimeout);
                return regex.IsMatch(text)
                    ? (true, string.Empty)
                    : (false, $"文本不匹配正则“{pattern}”（实际 {text}）");
            }

            case AssertionOperator.ApproxEquals:
            {
                if (!WorkflowValueConverter.TryGetNumber(actualValue, out var actualNumber)
                    || !WorkflowValueConverter.TryGetNumber(expectedValue, out var expectedNumber))
                {
                    return (false, $"近似比较需要数值（实际 {Describe(actual)}，期望 {Describe(expected)}）");
                }

                var tolerance = assertion.Tolerance ?? 1e-6;
                return Math.Abs(actualNumber - expectedNumber) <= tolerance
                    ? (true, string.Empty)
                    : (false, $"差值 {Math.Abs(actualNumber - expectedNumber)} 超过容差 {tolerance}");
            }

            case AssertionOperator.JsonEquals:
                return actual is not null && expected is not null && JsonNode.DeepEquals(actual, expected)
                    ? (true, string.Empty)
                    : (false, $"JSON 结构不一致（期望 {Describe(expected)}，实际 {Describe(actual)}）");

            default:
                return (false, $"不支持的运算符“{assertion.Operator}”。");
        }
    }

    private static (bool, string) CompareOrdered(
        AssertionOperator op,
        object? actualValue,
        object? expectedValue,
        JsonNode? actual,
        JsonNode? expected)
    {
        int comparison;
        if (actualValue is double dl && expectedValue is double dr)
        {
            comparison = dl.CompareTo(dr);
        }
        else if (actualValue is string sl && expectedValue is string sr)
        {
            comparison = string.CompareOrdinal(sl, sr);
        }
        else
        {
            return (false, $"比较需要同为数值或同为字符串（实际 {Describe(actual)}，期望 {Describe(expected)}）");
        }

        var passed = op switch
        {
            AssertionOperator.GreaterThan => comparison > 0,
            AssertionOperator.GreaterThanOrEqual => comparison >= 0,
            AssertionOperator.LessThan => comparison < 0,
            _ => comparison <= 0,
        };
        return passed ? (true, string.Empty) : (false, $"{Describe(actual)} 不满足 {op} {Describe(expected)}");
    }

    private static bool Contains(JsonNode? actual, JsonNode? expected)
    {
        var needle = WorkflowValueConverter.ToDisplayText(WorkflowValueConverter.FromNode(expected));
        return actual switch
        {
            JsonArray array => array.Any(item =>
                WorkflowValueConverter.LooseEquals(WorkflowValueConverter.FromNode(item), WorkflowValueConverter.FromNode(expected))),
            JsonValue value when value.TryGetValue<string>(out var text) => text.Contains(needle, StringComparison.Ordinal),
            JsonObject obj => obj.ContainsKey(needle),
            _ => false,
        };
    }

    private static string Describe(JsonNode? node)
        => node is null ? "null" : WorkflowJson.Compact(node);
}
