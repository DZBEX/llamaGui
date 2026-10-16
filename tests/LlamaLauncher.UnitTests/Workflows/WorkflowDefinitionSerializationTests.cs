using System.Text.Json.Nodes;
using LlamaLauncher.Domain.Workflows;
using Xunit;

namespace LlamaLauncher.UnitTests.Workflows;

/// <summary>工作流定义序列化/克隆与节点目录基础行为。</summary>
public sealed class WorkflowDefinitionSerializationTests
{
    private static WorkflowDefinition BuildSample()
    {
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "示例工作流",
            Description = "测试用",
            CreatedAt = DateTimeOffset.Parse("2026-10-17T00:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-10-17T01:00:00Z"),
            Inputs =
            {
                new WorkflowVariable { Name = "message", Type = WorkflowValueType.String, Required = true },
            },
            Tags = { "测试", "demo" },
        };

        var start = NodeTypeCatalog.CreateNode(NodeTypeCatalog.Start, "n1", 40, 40, "开始");
        var decision = NodeTypeCatalog.CreateNode(NodeTypeCatalog.Decision, "n2", 260, 40);
        var decisionConfig = new DecisionNodeConfig
        {
            StateTemplate = "{{ input.message }}",
            Questions =
            {
                new DecisionQuestion
                {
                    Id = "route",
                    Type = "choice",
                    Instructions = "Which team?",
                    Options =
                    {
                        new DecisionOption { Key = "billing", Description = "账单" },
                        new DecisionOption { Key = "other" },
                    },
                },
            },
        };
        decision.Config = WorkflowJson.ToConfigObject(decisionConfig);
        decision.Retry = new RetryPolicy { MaxAttempts = 2, DelayMilliseconds = 100, Condition = RetryCondition.OnTransient };
        decision.InputMappings.Add(new WorkflowDataMapping { Target = "state", Source = "input.message", Required = true });

        var end = NodeTypeCatalog.CreateNode(NodeTypeCatalog.End, "n3", 500, 40);
        var endConfig = new EndNodeConfig
        {
            OutputMappings =
            {
                new WorkflowDataMapping { Target = "route", Source = "up.n2.out.route.choice" },
            },
        };
        end.Config = WorkflowJson.ToConfigObject(endConfig);

        workflow.Nodes.AddRange(new[] { start, decision, end });
        workflow.Edges.Add(new WorkflowEdge { Id = "e1", SourceNodeId = "n1", TargetNodeId = "n2" });
        workflow.Edges.Add(new WorkflowEdge { Id = "e2", SourceNodeId = "n2", TargetNodeId = "n3" });
        return workflow;
    }

    [Fact]
    public void JSON往返保持结构()
    {
        var workflow = BuildSample();
        var json = WorkflowJson.Serialize(workflow);
        var parsed = WorkflowJson.Deserialize<WorkflowDefinition>(json);

        Assert.NotNull(parsed);
        Assert.Equal(workflow.Id, parsed!.Id);
        Assert.Equal(workflow.Name, parsed.Name);
        Assert.Equal(3, parsed.Nodes.Count);
        Assert.Equal(2, parsed.Edges.Count);
        Assert.Equal("billing", parsed.Nodes[1].Config!["questions"]![0]!["options"]![0]!["key"]!.GetValue<string>());
        Assert.Equal(RetryCondition.OnTransient, parsed.Nodes[1].Retry.Condition);
        Assert.Equal(NodeTypeCatalog.Decision, parsed.Nodes[1].TypeId);
    }

    [Fact]
    public void 克隆为深拷贝()
    {
        var workflow = BuildSample();
        var clone = workflow.Clone();

        clone.Nodes[1].Name = "改名";
        clone.Nodes[1].Config!["stateTemplate"] = "changed";
        clone.Edges.RemoveAt(0);

        Assert.NotEqual("改名", workflow.Nodes[1].Name);
        Assert.Equal("{{ input.message }}", workflow.Nodes[1].Config!["stateTemplate"]!.GetValue<string>());
        Assert.Equal(2, workflow.Edges.Count);
    }

    [Fact]
    public void 类型化配置读写()
    {
        var node = NodeTypeCatalog.CreateNode(NodeTypeCatalog.Decision, "n2", 0, 0);
        var config = WorkflowJson.TryGetConfig<DecisionNodeConfig>(node.Config, out var error);
        Assert.Null(error);
        Assert.NotNull(config);
        Assert.Empty(config!.Questions);

        var condition = NodeTypeCatalog.CreateNode(NodeTypeCatalog.Condition, "n5", 0, 0);
        var conditionConfig = WorkflowJson.TryGetConfig<ConditionNodeConfig>(condition.Config, out _);
        Assert.NotNull(conditionConfig);
        Assert.Equal(2, conditionConfig!.Branches.Count);
        Assert.True(conditionConfig.Branches[1].IsDefault);
    }

    [Fact]
    public void 节点目录完整且ID稳定()
    {
        Assert.Equal(13, NodeTypeCatalog.All.Count);
        Assert.All(NodeTypeCatalog.All, d => Assert.False(string.IsNullOrWhiteSpace(d.TypeId)));
        Assert.Equal(NodeTypeCatalog.All.Count, NodeTypeCatalog.All.Select(d => d.TypeId).Distinct().Count());
        Assert.NotNull(NodeTypeCatalog.Find(NodeTypeCatalog.Decision));
        Assert.Null(NodeTypeCatalog.Find("no.such.type"));
    }
}
