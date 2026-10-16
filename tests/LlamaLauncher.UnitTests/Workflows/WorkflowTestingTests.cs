using System.Text.Json.Nodes;
using LlamaLauncher.Application.Workflows;
using LlamaLauncher.Application.Workflows.Testing;
using LlamaLauncher.Domain.Workflows;
using Xunit;

namespace LlamaLauncher.UnitTests.Workflows;

/// <summary>断言评估与 JSON 差异比较。</summary>
public sealed class WorkflowTestingTests
{
    private static WorkflowRunResult CreateRun(JsonObject? output = null, params (string NodeId, NodeRunState State)[] nodes)
    {
        var record = new WorkflowRunRecord
        {
            RunId = "run-1",
            WorkflowId = "wf",
            WorkflowNameSnapshot = "测试",
            StartedUtc = DateTimeOffset.UtcNow,
            EndedUtc = DateTimeOffset.UtcNow,
            DurationMs = 100,
            State = WorkflowRunState.Succeeded,
            OutputJson = output is null ? null : WorkflowJson.Serialize(output, indented: false),
            FinalVariablesJson = """{"answer":42}""",
        };
        foreach (var (nodeId, state) in nodes)
        {
            record.Nodes.Add(new NodeRunRecord
            {
                NodeId = nodeId,
                Name = nodeId,
                State = state,
                OutputJson = """{"out":{"value":1}}""",
            });
        }

        return new WorkflowRunResult { Record = record, Output = output };
    }

    private static WorkflowTestCase Case(params WorkflowAssertion[] assertions)
        => new()
        {
            Id = "tc",
            Name = "用例",
            WorkflowId = "wf",
            Assertions = assertions.ToList(),
        };

    private static WorkflowAssertion MakeAssertion(string target, AssertionOperator op, string? expected = null, double? tolerance = null)
        => new()
        {
            Id = "a-" + Guid.NewGuid().ToString("N")[..6],
            Name = target,
            Target = target,
            Operator = op,
            ExpectedJson = expected,
            Tolerance = tolerance,
        };

    [Fact]
    public void 各运算符行为()
    {
        var output = new JsonObject
        {
            ["route"] = "billing",
            ["confidence"] = 0.82,
            ["tags"] = new JsonArray("a", "b"),
            ["nested"] = new JsonObject { ["flag"] = true },
        };
        var run = CreateRun(output);

        var testCase = Case(
            MakeAssertion("output.route", AssertionOperator.Equals, "\"billing\""),
            MakeAssertion("output.route", AssertionOperator.NotEquals, "\"technical\""),
            MakeAssertion("output.confidence", AssertionOperator.GreaterThan, "0.5"),
            MakeAssertion("output.confidence", AssertionOperator.LessThanOrEqual, "0.9"),
            MakeAssertion("output.confidence", AssertionOperator.ApproxEquals, "0.83", 0.02),
            MakeAssertion("output.tags", AssertionOperator.Contains, "\"b\""),
            MakeAssertion("output.tags", AssertionOperator.NotContains, "\"z\""),
            MakeAssertion("output.nested.flag", AssertionOperator.IsTrue),
            MakeAssertion("output.missing", AssertionOperator.NotExists),
            MakeAssertion("output.route", AssertionOperator.Exists),
            MakeAssertion("output.route", AssertionOperator.RegexMatch, "\"^bil\""),
            MakeAssertion("output.nested", AssertionOperator.JsonEquals, "{\"flag\":true}"),
            MakeAssertion("vars.answer", AssertionOperator.Equals, "42"));

        var report = WorkflowAssertionEvaluator.Evaluate(testCase, run);
        Assert.True(report.Passed, string.Join("；", report.Outcomes.Where(o => !o.Passed).Select(o => o.Message)));
        Assert.Equal(13, report.Outcomes.Count);
    }

    [Fact]
    public void 失败断言给出实际与期望()
    {
        var run = CreateRun(new JsonObject { ["route"] = "technical" });
        var testCase = Case(MakeAssertion("output.route", AssertionOperator.Equals, "\"billing\""));
        var report = WorkflowAssertionEvaluator.Evaluate(testCase, run);
        Assert.False(report.Passed);
        Assert.Contains("technical", report.Outcomes[0].Message);
        Assert.Contains("billing", report.Outcomes[0].Message);
    }

    [Fact]
    public void 警告级断言失败不判失败()
    {
        var run = CreateRun(new JsonObject { ["route"] = "technical" });
        var assertion = MakeAssertion("output.route", AssertionOperator.Equals, "\"billing\"");
        assertion.Severity = WorkflowIssueSeverity.Warning;
        var report = WorkflowAssertionEvaluator.Evaluate(Case(assertion), run);
        Assert.True(report.Passed);
        Assert.Equal(0, report.FailedCount);
    }

    [Fact]
    public void 节点状态可通过记录查询()
    {
        var run = CreateRun(new JsonObject(), ("n2", NodeRunState.Failed));
        var testCase = Case(MakeAssertion("records.n2.state", AssertionOperator.Equals, "\"Failed\""));
        var report = WorkflowAssertionEvaluator.Evaluate(testCase, run);
        Assert.True(report.Passed);
    }

    [Fact]
    public void 非法表达式与正则不会崩溃()
    {
        var run = CreateRun(new JsonObject());
        var testCase = Case(
            MakeAssertion("output.route +", AssertionOperator.Equals, "\"x\""),
            MakeAssertion("output.route", AssertionOperator.RegexMatch, "\"[\""));
        var report = WorkflowAssertionEvaluator.Evaluate(testCase, run);
        Assert.False(report.Passed);
        Assert.All(report.Outcomes, o => Assert.False(string.IsNullOrWhiteSpace(o.Message)));
    }

    [Fact]
    public void JSON差异识别新增删除与变化()
    {
        var left = WorkflowJson.TryParse("""{"a":1,"b":{"c":"x","d":[1,2]},"e":"old"}""", out _);
        var right = WorkflowJson.TryParse("""{"a":2,"b":{"c":"x","d":[1,3,4]},"f":"new"}""", out _);

        var changes = WorkflowJsonDiff.Compare(left, right);

        Assert.Contains(changes, c => c.Path == "a" && c.Kind == JsonDiffKind.Changed);
        Assert.Contains(changes, c => c.Path == "e" && c.Kind == JsonDiffKind.Removed);
        Assert.Contains(changes, c => c.Path == "f" && c.Kind == JsonDiffKind.Added);
        Assert.Contains(changes, c => c.Path == "b.d[1]" && c.Kind == JsonDiffKind.Changed);
        Assert.Contains(changes, c => c.Path == "b.d[2]" && c.Kind == JsonDiffKind.Added);
        Assert.DoesNotContain(changes, c => c.Path == "b.c");
    }

    [Fact]
    public void JSON差异支持忽略字段()
    {
        var left = WorkflowJson.TryParse("""{"a":1,"time":"t1"}""", out _);
        var right = WorkflowJson.TryParse("""{"a":1,"time":"t2"}""", out _);
        var changes = WorkflowJsonDiff.Compare(left, right, new[] { "time" });
        Assert.Empty(changes);
    }

    [Fact]
    public void 运行比较给出状态耗时与输出差异()
    {
        var left = CreateRun(new JsonObject { ["route"] = "billing" }, ("n2", NodeRunState.Succeeded));
        var right = CreateRun(new JsonObject { ["route"] = "technical" }, ("n2", NodeRunState.Failed));

        var comparison = WorkflowRunComparer.Compare(left.Record, right.Record);

        Assert.Contains(comparison.OutputChanges, c => c.Path == "route");
        Assert.Contains(comparison.NodeStateChanges, s => s.Contains("Succeeded → Failed"));
        Assert.Contains("总耗时", comparison.TimingSummary);
    }
}
