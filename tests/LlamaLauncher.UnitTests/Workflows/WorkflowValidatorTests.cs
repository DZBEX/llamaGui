using LlamaLauncher.Application.Workflows;
using LlamaLauncher.Domain.Workflows;
using Xunit;

namespace LlamaLauncher.UnitTests.Workflows;

/// <summary>工作流校验器：结构、连线、分支、映射、配置与子流程递归。</summary>
public sealed class WorkflowValidatorTests
{
    private static WorkflowValidator CreateValidator() => new(WorkflowExecutors.CreateDefault());

    private static WorkflowValidationReport Validate(WorkflowDefinition definition, WorkflowValidationContext? context = null)
        => CreateValidator().Validate(definition, context);

    private static WorkflowValidationContext ContextFor(WorkflowDefinition definition)
        => new() { Definition = definition };

    private static WorkflowDefinition ValidWorkflow()
    {
        var wf = WorkflowBuilder.Create("合法流程");
        wf.Inputs.Add(new WorkflowVariable { Name = "message", Required = true });
        var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
        var transform = wf.AddNode(NodeTypeCatalog.Transform, "n2").WithAssign("echo", "input.message");
        var end = wf.AddNode(NodeTypeCatalog.End, "n3").WithConfig(new EndNodeConfig
        {
            OutputMappings = { new WorkflowDataMapping { Target = "echo", Source = "up.n2.out.echo" } },
        });
        wf.Connect(start, transform);
        wf.Connect(transform, end);
        return wf;
    }

    [Fact]
    public void 合法工作流通过校验()
    {
        var wf = ValidWorkflow();
        var report = Validate(wf, ContextFor(wf));
        Assert.False(report.HasErrors, string.Join("；", report.Issues.Select(i => i.Message)));
    }

    [Fact]
    public void 缺少开始节点报错()
    {
        var wf = WorkflowBuilder.Create();
        var end = wf.AddNode(NodeTypeCatalog.End, "n1");
        var report = Validate(wf, ContextFor(wf));
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-001");
    }

    [Fact]
    public void 缺少结束节点报错()
    {
        var wf = WorkflowBuilder.Create();
        wf.AddNode(NodeTypeCatalog.Start, "n1");
        var report = Validate(wf, ContextFor(wf));
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-003");
    }

    [Fact]
    public void 多个开始节点报错()
    {
        var wf = ValidWorkflow();
        var second = wf.AddNode(NodeTypeCatalog.Start, "n9");
        wf.Connect(second, wf.FindNode("n2")!);
        var report = Validate(wf, ContextFor(wf));
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-002");
    }

    [Fact]
    public void 重复节点ID报错()
    {
        var wf = ValidWorkflow();
        wf.Nodes.Add(NodeTypeCatalog.CreateNode(NodeTypeCatalog.Transform, "n2", 0, 100));
        var report = Validate(wf, ContextFor(wf));
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-004");
    }

    [Fact]
    public void 悬空连线与重复连线报错()
    {
        var wf = ValidWorkflow();
        wf.Edges.Add(new WorkflowEdge { Id = "e9", SourceNodeId = "n2", TargetNodeId = "missing" });
        wf.Edges.Add(new WorkflowEdge { Id = "e10", SourceNodeId = "n1", TargetNodeId = "n2" });
        var report = Validate(wf, ContextFor(wf));
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-005");
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-007");
    }

    [Fact]
    public void 孤立与不可达节点给出警告()
    {
        var wf = ValidWorkflow();
        wf.AddNode(NodeTypeCatalog.Transform, "orphan");
        var island1 = wf.AddNode(NodeTypeCatalog.Transform, "n8");
        var island2 = wf.AddNode(NodeTypeCatalog.Transform, "n9");
        wf.Connect(island1, island2);

        var report = Validate(wf, ContextFor(wf));
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-006" && i.NodeId == "orphan");
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-022" && i.NodeId == "n8");
    }

    [Fact]
    public void 未知端口报错()
    {
        var wf = ValidWorkflow();
        wf.Edges.Add(new WorkflowEdge
        {
            Id = "e9",
            SourceNodeId = "n2",
            SourcePort = "noSuchPort",
            TargetNodeId = "n3",
        });
        var report = Validate(wf, ContextFor(wf));
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-008");
    }

    [Fact]
    public void 存在环路时阻止运行()
    {
        var wf = ValidWorkflow();
        var a = wf.AddNode(NodeTypeCatalog.Transform, "n8").WithAssign("x", "1");
        var b = wf.AddNode(NodeTypeCatalog.Transform, "n9").WithAssign("y", "2");
        wf.Connect(a, b);
        wf.Connect(b, a);
        var report = Validate(wf, ContextFor(wf));
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-023");
    }

    [Fact]
    public void 条件节点必须恰好一个默认分支()
    {
        var wf = WorkflowBuilder.Create();
        var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
        var condition = wf.AddNode(NodeTypeCatalog.Condition, "n2").WithConfig(new ConditionNodeConfig
        {
            Branches =
            {
                new ConditionBranch { Id = "a", Name = "A", Condition = "true" },
                new ConditionBranch { Id = "b", Name = "B", Condition = "false" },
            },
        });
        var end = wf.AddNode(NodeTypeCatalog.End, "n3");
        wf.Connect(start, condition);
        wf.Connect(condition, end, "a");
        wf.Connect(condition, end, "b");

        var report = Validate(wf, ContextFor(wf));
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-009");
    }

    [Fact]
    public void 条件表达式与模板错误被指出()
    {
        var wf = WorkflowBuilder.Create();
        var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
        var condition = wf.AddNode(NodeTypeCatalog.Condition, "n2").WithConfig(new ConditionNodeConfig
        {
            Branches =
            {
                new ConditionBranch { Id = "a", Name = "A", Condition = "input.x >" },
                new ConditionBranch { Id = "default", Name = "默认", IsDefault = true },
            },
        });
        var ruleNode = wf.AddNode(NodeTypeCatalog.Rules, "n4").WithConfig(new RulesNodeConfig
        {
            Rules =
            {
                new RuleDefinition
                {
                    Id = "r1",
                    Name = "规则",
                    Condition = "true",
                    MessageTemplate = "{{ input.message",
                },
            },
        });
        var end = wf.AddNode(NodeTypeCatalog.End, "n3");
        wf.Connect(start, condition);
        wf.Connect(condition, ruleNode, "a");
        wf.Connect(condition, end, "default");
        wf.Connect(ruleNode, end);

        var report = Validate(wf, ContextFor(wf));
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-011");
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-012");
    }

    [Fact]
    public void 分支节点必须标注连线分支()
    {
        var wf = WorkflowBuilder.Create();
        var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
        var parallel = wf.AddNode(NodeTypeCatalog.Parallel, "n2");
        var t1 = wf.AddNode(NodeTypeCatalog.Transform, "n3").WithAssign("x", "1");
        var end = wf.AddNode(NodeTypeCatalog.End, "n4");
        wf.Connect(start, parallel);
        wf.Connect(parallel, t1); // 未标注分支
        wf.Connect(t1, end);

        var report = Validate(wf, ContextFor(wf));
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-019");
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-018");
    }

    [Fact]
    public void 错误分支策略需要错误出口连线()
    {
        var wf = ValidWorkflow();
        wf.FindNode("n2")!.FailurePolicy = NodeFailurePolicy.ErrorBranch;
        var report = Validate(wf, ContextFor(wf));
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-025");
    }

    [Fact]
    public void 映射引用不存在的输入报错()
    {
        var wf = ValidWorkflow();
        wf.FindNode("n2")!.InputMappings.Add(new WorkflowDataMapping
        {
            Target = "data",
            Source = "input.notDeclared",
        });
        var report = Validate(wf, ContextFor(wf));
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-020");
    }

    [Fact]
    public void 映射必填缺失与错误转换被指出()
    {
        var wf = ValidWorkflow();
        var node = wf.FindNode("n2")!;
        node.InputMappings.Add(new WorkflowDataMapping { Target = "a", Required = true });
        node.InputMappings.Add(new WorkflowDataMapping
        {
            Target = "b",
            Source = "input.message",
            Convert = "datetime",
        });
        var report = Validate(wf, ContextFor(wf));
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-049");
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-050");
    }

    [Fact]
    public void 决策节点配置检查()
    {
        var wf = WorkflowBuilder.Create();
        wf.Inputs.Add(new WorkflowVariable { Name = "message" });
        var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
        var decision = wf.AddNode(NodeTypeCatalog.Decision, "n2").WithConfig(new DecisionNodeConfig
        {
            StateTemplate = "{{ input.message }}",
            InstanceId = "missing-instance",
            Questions =
            {
                new DecisionQuestion { Id = "q1", Type = "choice", Instructions = "选一个", Options = { new DecisionOption { Key = "only" } } },
                new DecisionQuestion { Id = "q1", Type = "unknown", Instructions = "x" },
                new DecisionQuestion { Id = "q3", Type = "score", Instructions = "打分", Levels = { "one" } },
            },
        });
        var end = wf.AddNode(NodeTypeCatalog.End, "n3");
        wf.Connect(start, decision);
        wf.Connect(decision, end);

        var context = new WorkflowValidationContext
        {
            Definition = wf,
            Models = new[]
            {
                new WorkflowModelTarget("another", "实例", "http://127.0.0.1:1", true, null, new System.Text.Json.Nodes.JsonObject()),
            },
        };
        var report = Validate(wf, context);
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-013"); // 选项不足/重复问题 ID/非法类型/等级不足
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-014"); // 实例不存在
    }

    [Fact]
    public void 循环节点迭代与子工作流检查()
    {
        var wf = WorkflowBuilder.Create();
        wf.Inputs.Add(new WorkflowVariable { Name = "items", Type = WorkflowValueType.Array });
        var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
        var loop = wf.AddNode(NodeTypeCatalog.Loop, "n2").WithConfig(new LoopNodeConfig
        {
            Mode = LoopMode.SubWorkflow,
            ItemsExpression = "input.items",
            MaxIterations = 5000,
            SubWorkflowId = "missing-workflow",
        });
        var end = wf.AddNode(NodeTypeCatalog.End, "n3");
        wf.Connect(start, loop);
        wf.Connect(loop, end);

        var context = new WorkflowValidationContext
        {
            Definition = wf,
            WorkflowExists = id => id != "missing-workflow",
        };
        var report = Validate(wf, context);
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-015");
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-016");
    }

    [Fact]
    public void 子工作流递归引用被阻止()
    {
        var parent = WorkflowBuilder.Create("父流程");
        parent.Id = "wf-parent";
        var start = parent.AddNode(NodeTypeCatalog.Start, "n1");
        var sub = parent.AddNode(NodeTypeCatalog.SubWorkflow, "n2").WithConfig(new SubWorkflowNodeConfig
        {
            WorkflowId = "wf-child",
        });
        var end = parent.AddNode(NodeTypeCatalog.End, "n3");
        parent.Connect(start, sub);
        parent.Connect(sub, end);

        var child = WorkflowBuilder.Create("子流程");
        child.Id = "wf-child";
        var cStart = child.AddNode(NodeTypeCatalog.Start, "c1");
        var cSub = child.AddNode(NodeTypeCatalog.SubWorkflow, "c2").WithConfig(new SubWorkflowNodeConfig
        {
            WorkflowId = "wf-parent",
        });
        var cEnd = child.AddNode(NodeTypeCatalog.End, "c3");
        child.Connect(cStart, cSub);
        child.Connect(cSub, cEnd);

        var definitions = new Dictionary<string, WorkflowDefinition>
        {
            ["wf-parent"] = parent,
            ["wf-child"] = child,
        };
        var context = new WorkflowValidationContext
        {
            Definition = parent,
            WorkflowExists = id => definitions.ContainsKey(id),
            WorkflowLoader = id => definitions.GetValueOrDefault(id),
        };

        var report = Validate(parent, context);
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-017");
    }

    [Fact]
    public void 未知节点类型保留并报告()
    {
        var wf = ValidWorkflow();
        wf.Nodes.Add(new WorkflowNode { Id = "n7", TypeId = "custom.extension", Name = "扩展节点" });
        var report = Validate(wf, ContextFor(wf));
        Assert.Contains(report.Issues, i => i.Code == "WF-NODE-UNKNOWN" && i.NodeId == "n7");
    }

    [Fact]
    public void 重试与超时越界报错()
    {
        var wf = ValidWorkflow();
        var node = wf.FindNode("n2")!;
        node.TimeoutSeconds = 0;
        node.Retry = new RetryPolicy { MaxAttempts = 99, DelayMilliseconds = 999_999 };
        var report = Validate(wf, ContextFor(wf));
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-028");
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-029");
    }

    [Fact]
    public void 算法求解器未注册报错()
    {
        var wf = WorkflowBuilder.Create();
        var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
        var algorithm = wf.AddNode(NodeTypeCatalog.Algorithm, "n2").WithConfig(new AlgorithmNodeConfig
        {
            SolverKey = "not.registered",
        });
        var end = wf.AddNode(NodeTypeCatalog.End, "n3");
        wf.Connect(start, algorithm);
        wf.Connect(algorithm, end);

        var context = new WorkflowValidationContext
        {
            Definition = wf,
            AlgorithmKeys = new[] { "sample.ranking" },
        };
        var report = Validate(wf, context);
        Assert.Contains(report.Issues, i => i.Code == "WF-VAL-057");
    }
}
