using System.Text.Json.Nodes;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Workflows;
using LlamaLauncher.Domain.Workflows;
using Xunit;

namespace LlamaLauncher.UnitTests.Workflows;

/// <summary>执行引擎：顺序、分支、失败策略、重试、超时、取消、单步、断点、并行、循环、子流程、模拟模式。</summary>
public sealed class WorkflowEngineTests
{
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        return condition();
    }

    private static WorkflowDefinition BuildDecisionWorkflow(WorkflowTestEnvironment env, Func<WorkflowNode, WorkflowNode>? configure = null)
    {
        var wf = WorkflowBuilder.Create();
        wf.Inputs.Add(new WorkflowVariable { Name = "message", Required = true });
        var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
        var decision = wf.AddNode(NodeTypeCatalog.Decision, "n2");
        decision.WithDecision();
        if (configure is not null)
        {
            configure(decision);
        }

        var end = wf.AddNode(NodeTypeCatalog.End, "n3").WithConfig(new EndNodeConfig
        {
            OutputMappings =
            {
                new WorkflowDataMapping { Target = "route", Source = "up.n2.out.route.choice" },
            },
        });
        wf.Connect(start, decision);
        wf.Connect(decision, end);
        return wf;
    }

    [Fact]
    public async Task 顺序执行并传递输出()
    {
        var env = new WorkflowTestEnvironment();
        var wf = WorkflowBuilder.Create();
        wf.Inputs.Add(new WorkflowVariable { Name = "message", Required = true });
        var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
        var transform = wf.AddNode(NodeTypeCatalog.Transform, "n2").WithAssign("echo", "input.message");
        var end = wf.AddNode(NodeTypeCatalog.End, "n3").WithConfig(new EndNodeConfig
        {
            OutputMappings = { new WorkflowDataMapping { Target = "echo", Source = "up.n2.out.echo" } },
        });
        wf.Connect(start, transform);
        wf.Connect(transform, end);

        var result = await env.RunAsync(wf, new JsonObject { ["message"] = "hello" });

        Assert.Equal(WorkflowRunState.Succeeded, result.State);
        Assert.Equal("hello", result.Output!["echo"]!.GetValue<string>());
        Assert.Equal(3, result.Record.Nodes.Count);
        Assert.All(result.Record.Nodes, n => Assert.Equal(NodeRunState.Succeeded, n.State));
        Assert.True(result.Record.Nodes[0].Sequence < result.Record.Nodes[1].Sequence);
        Assert.True(result.Record.Nodes[1].Sequence < result.Record.Nodes[2].Sequence);
        Assert.NotNull(result.Record.DefinitionSnapshotJson);
        Assert.Contains(result.Record.Logs, l => l.Message.Contains("工作流开始运行"));
    }

    [Fact]
    public async Task 变量写入与读取()
    {
        var env = new WorkflowTestEnvironment();
        var wf = WorkflowBuilder.Create();
        var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
        var first = wf.AddNode(NodeTypeCatalog.Transform, "n2").WithAssign("value", "42");
        first.OutputVariable = "firstResult";
        var second = wf.AddNode(NodeTypeCatalog.Transform, "n3").WithAssign("copied", "vars.firstResult.value");
        var end = wf.AddNode(NodeTypeCatalog.End, "n4").WithConfig(new EndNodeConfig
        {
            OutputMappings = { new WorkflowDataMapping { Target = "copied", Source = "up.n3.out.copied" } },
        });
        wf.Connect(start, first);
        wf.Connect(first, second);
        wf.Connect(second, end);

        var result = await env.RunAsync(wf);

        Assert.Equal(WorkflowRunState.Succeeded, result.State);
        Assert.Equal(42, result.Output!["copied"]!.GetValue<double>());
    }

    [Fact]
    public async Task 条件分支只执行选中路径()
    {
        var env = new WorkflowTestEnvironment();

        WorkflowDefinition Build()
        {
            var wf = WorkflowBuilder.Create();
            wf.Inputs.Add(new WorkflowVariable { Name = "flag", Required = true, Type = WorkflowValueType.Boolean });
            var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
            var condition = wf.AddNode(NodeTypeCatalog.Condition, "n2").WithConfig(new ConditionNodeConfig
            {
                Branches =
                {
                    new ConditionBranch { Id = "yes", Name = "是", Condition = "input.flag == true" },
                    new ConditionBranch { Id = "no", Name = "否", IsDefault = true },
                },
            });
            var yes = wf.AddNode(NodeTypeCatalog.Transform, "n3").WithAssign("route", "\"yes\"");
            var no = wf.AddNode(NodeTypeCatalog.Transform, "n4").WithAssign("route", "\"no\"");
            var end = wf.AddNode(NodeTypeCatalog.End, "n5").WithConfig(new EndNodeConfig
            {
                OutputMappings = { new WorkflowDataMapping { Target = "route", Source = "coalesce(up.n3.out.route, up.n4.out.route)" } },
            });
            wf.Connect(start, condition);
            wf.Connect(condition, yes, "yes");
            wf.Connect(condition, no, "no");
            wf.Connect(yes, end);
            wf.Connect(no, end);
            return wf;
        }

        var yesRun = await env.RunAsync(Build(), new JsonObject { ["flag"] = true });
        Assert.Equal(WorkflowRunState.Succeeded, yesRun.State);
        Assert.Equal("yes", yesRun.Output!["route"]!.GetValue<string>());
        Assert.Equal(NodeRunState.Succeeded, yesRun.Record.Nodes.Single(n => n.NodeId == "n3").State);
        Assert.Equal(NodeRunState.Skipped, yesRun.Record.Nodes.Single(n => n.NodeId == "n4").State);
        Assert.Equal("yes", yesRun.Record.Nodes.Single(n => n.NodeId == "n2").BranchTaken);

        var env2 = new WorkflowTestEnvironment();
        var noRun = await env2.RunAsync(Build(), new JsonObject { ["flag"] = false });
        Assert.Equal(WorkflowRunState.Succeeded, noRun.State);
        Assert.Equal("no", noRun.Output!["route"]!.GetValue<string>());
        Assert.Equal(NodeRunState.Skipped, noRun.Record.Nodes.Single(n => n.NodeId == "n3").State);
        Assert.Equal(NodeRunState.Succeeded, noRun.Record.Nodes.Single(n => n.NodeId == "n4").State);
    }

    [Fact]
    public async Task 节点失败默认终止运行()
    {
        var env = new WorkflowTestEnvironment();
        env.Models.Targets.Clear();
        var wf = BuildDecisionWorkflow(env);

        var result = await env.RunAsync(wf, new JsonObject { ["message"] = "hi" });

        Assert.Equal(WorkflowRunState.Failed, result.State);
        Assert.Contains("没有可用的模型服务实例", result.Record.ErrorSummary!);
        Assert.Equal(NodeRunState.Failed, result.Record.Nodes.Single(n => n.NodeId == "n2").State);
        Assert.Equal(NodeRunState.Skipped, result.Record.Nodes.Single(n => n.NodeId == "n3").State);
        Assert.Empty(env.Decisions.Calls);
    }

    [Fact]
    public async Task 失败继续策略让下游继续并携带错误上下文()
    {
        var env = new WorkflowTestEnvironment();
        var wf = WorkflowBuilder.Create();
        wf.Inputs.Add(new WorkflowVariable { Name = "flag", Required = true, Type = WorkflowValueType.Boolean });
        var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
        var broken = wf.AddNode(NodeTypeCatalog.Rules, "n2").WithConfig(new RulesNodeConfig
        {
            Rules =
            {
                new RuleDefinition
                {
                    Id = "r1",
                    Name = "必须通过",
                    Condition = "input.flag == true",
                    Severity = WorkflowIssueSeverity.Error,
                    MessageTemplate = "flag 不能为真",
                },
            },
            OnFailure = RuleFailureAction.Stop,
        });
        broken.FailurePolicy = NodeFailurePolicy.Continue;
        var probe = wf.AddNode(NodeTypeCatalog.Transform, "n3").WithConfig(new TransformNodeConfig
        {
            Operations =
            {
                new TransformOperation
                {
                    Id = "op1",
                    Kind = TransformOperationKind.Assign,
                    Target = "hasError",
                    Source = "!isNull(up.n2.error)",
                },
            },
        });
        var end = wf.AddNode(NodeTypeCatalog.End, "n4").WithConfig(new EndNodeConfig
        {
            OutputMappings = { new WorkflowDataMapping { Target = "hasError", Source = "up.n3.out.hasError" } },
        });
        wf.Connect(start, broken);
        wf.Connect(broken, probe);
        wf.Connect(probe, end);

        var result = await env.RunAsync(wf, new JsonObject { ["flag"] = true });

        Assert.Equal(WorkflowRunState.Succeeded, result.State);
        Assert.Equal(NodeRunState.Failed, result.Record.Nodes.Single(n => n.NodeId == "n2").State);
        Assert.Equal(NodeRunState.Succeeded, result.Record.Nodes.Single(n => n.NodeId == "n3").State);
        Assert.True(result.Output!["hasError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task 错误分支策略路由到异常处理节点()
    {
        var env = new WorkflowTestEnvironment();
        var wf = WorkflowBuilder.Create();
        wf.Inputs.Add(new WorkflowVariable { Name = "flag", Required = true, Type = WorkflowValueType.Boolean });
        var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
        var broken = wf.AddNode(NodeTypeCatalog.Rules, "n2").WithConfig(new RulesNodeConfig
        {
            Rules =
            {
                new RuleDefinition
                {
                    Id = "r1",
                    Name = "必须通过",
                    Condition = "input.flag == true",
                    Severity = WorkflowIssueSeverity.Error,
                },
            },
            OnFailure = RuleFailureAction.Stop,
        });
        broken.FailurePolicy = NodeFailurePolicy.ErrorBranch;
        var handler = wf.AddNode(NodeTypeCatalog.Exception, "n3");
        var end = wf.AddNode(NodeTypeCatalog.End, "n4").WithConfig(new EndNodeConfig
        {
            OutputMappings =
            {
                new WorkflowDataMapping { Target = "handled", Source = "up.n3.out.handled" },
                new WorkflowDataMapping { Target = "errorType", Source = "up.n3.out.error.type" },
            },
        });
        wf.Connect(start, broken);
        wf.Connect(broken, handler, branchId: null, sourcePort: NodeTypeCatalog.ErrorPort, targetPort: NodeTypeCatalog.ErrorPort);
        wf.Connect(handler, end);

        var result = await env.RunAsync(wf, new JsonObject { ["flag"] = true });

        Assert.Equal(WorkflowRunState.Succeeded, result.State);
        Assert.Equal(NodeRunState.Failed, result.Record.Nodes.Single(n => n.NodeId == "n2").State);
        Assert.True(result.Output!["handled"]!.GetValue<bool>());
        Assert.Equal("RuleCheckFailed", result.Output["errorType"]!.GetValue<string>());
    }

    [Fact]
    public async Task 瞬时错误按策略重试()
    {
        var env = new WorkflowTestEnvironment();
        var calls = 0;
        env.Decisions.Handler = (request, _) =>
        {
            calls++;
            return calls == 1
                ? Task.FromResult(DecisionCallResult.Fail(
                    new DecisionCallError(DecisionErrorKind.ConnectionFailed, "连接被拒绝"),
                    TimeSpan.FromMilliseconds(1)))
                : Task.FromResult(env.Decisions.DefaultResult(request));
        };

        var wf = BuildDecisionWorkflow(env, node =>
        {
            node.Retry = new RetryPolicy { MaxAttempts = 3, DelayMilliseconds = 1, Condition = RetryCondition.OnTransient };
            return node;
        });

        var result = await env.RunAsync(wf, new JsonObject { ["message"] = "hi" });

        Assert.Equal(WorkflowRunState.Succeeded, result.State);
        Assert.Equal(2, calls);
        var record = result.Record.Nodes.Single(n => n.NodeId == "n2");
        Assert.Equal(1, record.Retries);
        Assert.Equal(NodeRunState.Succeeded, record.State);
    }

    [Fact]
    public async Task 节点超时标记为超时()
    {
        var env = new WorkflowTestEnvironment();
        env.Decisions.Handler = async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return null!;
        };

        var wf = BuildDecisionWorkflow(env, node =>
        {
            node.TimeoutSeconds = 1;
            return node;
        });

        var result = await env.RunAsync(
            wf,
            new JsonObject { ["message"] = "hi" },
            limits: new WorkflowExecutionLimits { DefaultNodeTimeout = TimeSpan.FromSeconds(1) });

        Assert.Equal(WorkflowRunState.Failed, result.State);
        var record = result.Record.Nodes.Single(n => n.NodeId == "n2");
        Assert.Equal(NodeRunState.TimedOut, record.State);
        Assert.Contains("超时", record.ErrorMessage!);
    }

    [Fact]
    public async Task 取消后不再调度后续节点()
    {
        var env = new WorkflowTestEnvironment();
        var started = new TaskCompletionSource();
        env.Decisions.Handler = async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return null!;
        };

        var wf = BuildDecisionWorkflow(env);
        var after = wf.AddNode(NodeTypeCatalog.Transform, "n4").WithAssign("extra", "1");
        var end = wf.FindNode("n3")!;
        wf.Edges.RemoveAll(e => e.TargetNodeId == end.Id);
        wf.Connect(wf.FindNode("n2")!, after);
        wf.Connect(after, end);

        var engine = env.CreateEngine();
        var handle = engine.Start(new WorkflowRunRequest
        {
            Definition = wf,
            Services = env.CreateServices(),
            Inputs = new JsonObject { ["message"] = "hi" },
            TestMode = WorkflowTestMode.LocalModel,
            Limits = new WorkflowExecutionLimits(),
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handle.Cancel();
        var result = await handle.Completion;

        Assert.Equal(WorkflowRunState.Canceled, result.State);
        Assert.Equal(NodeRunState.Canceled, result.Record.Nodes.Single(n => n.NodeId == "n2").State);
        var afterRecord = result.Record.Nodes.Single(n => n.NodeId == "n4");
        Assert.NotEqual(NodeRunState.Succeeded, afterRecord.State);
        Assert.DoesNotContain(result.Record.Logs, l => l.NodeId == "n4" && l.Message.Contains("执行成功"));
    }

    [Fact]
    public async Task 单步执行在每个节点后暂停()
    {
        var env = new WorkflowTestEnvironment();
        var wf = WorkflowBuilder.Create();
        var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
        var t1 = wf.AddNode(NodeTypeCatalog.Transform, "n2").WithAssign("a", "1");
        var t2 = wf.AddNode(NodeTypeCatalog.Transform, "n3").WithAssign("b", "2");
        var end = wf.AddNode(NodeTypeCatalog.End, "n4");
        wf.Connect(start, t1);
        wf.Connect(t1, t2);
        wf.Connect(t2, end);

        var engine = env.CreateEngine();
        var handle = engine.Start(new WorkflowRunRequest
        {
            Definition = wf,
            Services = env.CreateServices(),
            TestMode = WorkflowTestMode.Simulated,
            StartInStepMode = true,
            Limits = new WorkflowExecutionLimits(),
            Observer = env.Observer,
        });

        Assert.True(await WaitUntilAsync(() => handle.State == WorkflowRunState.Paused));
        Assert.Equal(WorkflowPauseReason.StepCompleted, handle.PauseReason);
        Assert.DoesNotContain(env.Observer.NodeUpdates, r => r.NodeId == "n1" && r.State == NodeRunState.Succeeded);

        handle.Step();
        Assert.True(await WaitUntilAsync(() =>
            handle.State == WorkflowRunState.Paused
            && env.Observer.NodeUpdates.Any(r => r.NodeId == "n1" && r.State == NodeRunState.Succeeded)));
        Assert.DoesNotContain(env.Observer.NodeUpdates, r => r.NodeId == "n2" && r.State == NodeRunState.Succeeded);

        handle.Step();
        Assert.True(await WaitUntilAsync(() =>
            handle.State == WorkflowRunState.Paused
            && env.Observer.NodeUpdates.Any(r => r.NodeId == "n2" && r.State == NodeRunState.Succeeded)));

        handle.Resume();
        var result = await handle.Completion;
        Assert.Equal(WorkflowRunState.Succeeded, result.State);
    }

    [Fact]
    public async Task 断点在节点执行前暂停并在继续后完成()
    {
        var env = new WorkflowTestEnvironment();
        var wf = WorkflowBuilder.Create();
        var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
        var t1 = wf.AddNode(NodeTypeCatalog.Transform, "n2").WithAssign("a", "1");
        var t2 = wf.AddNode(NodeTypeCatalog.Transform, "n3").WithAssign("b", "2");
        t2.IsBreakpoint = true;
        var end = wf.AddNode(NodeTypeCatalog.End, "n4");
        wf.Connect(start, t1);
        wf.Connect(t1, t2);
        wf.Connect(t2, end);

        var engine = env.CreateEngine();
        var handle = engine.Start(new WorkflowRunRequest
        {
            Definition = wf,
            Services = env.CreateServices(),
            TestMode = WorkflowTestMode.Simulated,
            Limits = new WorkflowExecutionLimits(),
            Observer = env.Observer,
        });

        Assert.True(await WaitUntilAsync(() =>
            handle.State == WorkflowRunState.Paused && handle.PauseReason == WorkflowPauseReason.Breakpoint));
        Assert.Contains(env.Observer.NodeUpdates, r => r.NodeId == "n3" && r.State == NodeRunState.PausedAtBreakpoint);
        Assert.DoesNotContain(env.Observer.NodeUpdates, r => r.NodeId == "n3" && r.State == NodeRunState.Succeeded);

        handle.Resume();
        var result = await handle.Completion;
        Assert.Equal(WorkflowRunState.Succeeded, result.State);
        Assert.Equal(NodeRunState.Succeeded, result.Record.Nodes.Single(n => n.NodeId == "n3").State);
    }

    [Fact]
    public async Task 并行扇出与汇合()
    {
        var env = new WorkflowTestEnvironment();
        var wf = WorkflowBuilder.Create();
        var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
        var parallel = wf.AddNode(NodeTypeCatalog.Parallel, "n2").WithConfig(new ParallelNodeConfig
        {
            Branches =
            {
                new ParallelBranch { Id = "b1", Name = "分支 1" },
                new ParallelBranch { Id = "b2", Name = "分支 2" },
            },
        });
        var t1 = wf.AddNode(NodeTypeCatalog.Transform, "n3").WithAssign("value", "\"one\"");
        var t2 = wf.AddNode(NodeTypeCatalog.Transform, "n4").WithAssign("value", "\"two\"");
        var join = wf.AddNode(NodeTypeCatalog.Join, "n5");
        var end = wf.AddNode(NodeTypeCatalog.End, "n6").WithConfig(new EndNodeConfig
        {
            OutputMappings =
            {
                new WorkflowDataMapping { Target = "first", Source = "up.n5.out.branches.n3.value" },
                new WorkflowDataMapping { Target = "second", Source = "up.n5.out.branches.n4.value" },
            },
        });
        wf.Connect(start, parallel);
        wf.Connect(parallel, t1, "b1");
        wf.Connect(parallel, t2, "b2");
        wf.Connect(t1, join);
        wf.Connect(t2, join);
        wf.Connect(join, end);

        var result = await env.RunAsync(wf);

        Assert.Equal(WorkflowRunState.Succeeded, result.State);
        Assert.Equal("one", result.Output!["first"]!.GetValue<string>());
        Assert.Equal("two", result.Output["second"]!.GetValue<string>());
        var records = result.Record.Nodes.ToDictionary(n => n.NodeId);
        Assert.Equal(NodeRunState.Succeeded, records["n3"].State);
        Assert.Equal(NodeRunState.Succeeded, records["n4"].State);
        Assert.True(records["n5"].Sequence > records["n3"].Sequence);
        Assert.True(records["n5"].Sequence > records["n4"].Sequence);
    }

    [Fact]
    public async Task 循环超过最大迭代限制时报错()
    {
        var env = new WorkflowTestEnvironment();
        var wf = WorkflowBuilder.Create();
        wf.Inputs.Add(new WorkflowVariable { Name = "items", Required = true, Type = WorkflowValueType.Array });
        var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
        var loop = wf.AddNode(NodeTypeCatalog.Loop, "n2").WithConfig(new LoopNodeConfig
        {
            Mode = LoopMode.DecisionModel,
            ItemsExpression = "input.items",
            MaxIterations = 2,
            DecisionTemplate = new DecisionNodeConfig
            {
                StateTemplate = "{{ item }}",
                Questions =
                {
                    new DecisionQuestion
                    {
                        Id = "q1",
                        Type = "noul",
                        Instructions = "ok?",
                    },
                },
            },
        });
        var end = wf.AddNode(NodeTypeCatalog.End, "n3");
        wf.Connect(start, loop);
        wf.Connect(loop, end);

        var result = await env.RunAsync(wf, new JsonObject { ["items"] = new JsonArray(1, 2, 3) });

        Assert.Equal(WorkflowRunState.Failed, result.State);
        var record = result.Record.Nodes.Single(n => n.NodeId == "n2");
        Assert.Equal("LoopIterationLimit", record.ErrorType);
    }

    [Fact]
    public async Task 循环对数组逐项调用子工作流()
    {
        var env = new WorkflowTestEnvironment();
        var child = WorkflowBuilder.Create("子流程");
        child.Inputs.Add(new WorkflowVariable { Name = "item" });
        child.Inputs.Add(new WorkflowVariable { Name = "index", Type = WorkflowValueType.Number });
        var cStart = child.AddNode(NodeTypeCatalog.Start, "c1");
        var cTransform = child.AddNode(NodeTypeCatalog.Transform, "c2").WithAssign("echo", "input.item");
        var cEnd = child.AddNode(NodeTypeCatalog.End, "c3").WithConfig(new EndNodeConfig
        {
            OutputMappings = { new WorkflowDataMapping { Target = "echo", Source = "up.c2.out.echo" } },
        });
        child.Connect(cStart, cTransform);
        child.Connect(cTransform, cEnd);
        env.Definitions.Add(child);

        var wf = WorkflowBuilder.Create();
        wf.Inputs.Add(new WorkflowVariable { Name = "items", Required = true, Type = WorkflowValueType.Array });
        var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
        var loop = wf.AddNode(NodeTypeCatalog.Loop, "n2").WithConfig(new LoopNodeConfig
        {
            Mode = LoopMode.SubWorkflow,
            ItemsExpression = "input.items",
            MaxIterations = 10,
            SubWorkflowId = child.Id,
        });
        var end = wf.AddNode(NodeTypeCatalog.End, "n3").WithConfig(new EndNodeConfig
        {
            OutputMappings =
            {
                new WorkflowDataMapping { Target = "total", Source = "up.n2.out.total" },
                new WorkflowDataMapping { Target = "first", Source = "up.n2.out.items[0].result.echo" },
                new WorkflowDataMapping { Target = "second", Source = "up.n2.out.items[1].result.echo" },
            },
        });
        wf.Connect(start, loop);
        wf.Connect(loop, end);

        var result = await env.RunAsync(wf, new JsonObject { ["items"] = new JsonArray("a", "b") });

        Assert.Equal(WorkflowRunState.Succeeded, result.State);
        Assert.Equal(2d, result.Output!["total"]!.GetValue<double>());
        Assert.Equal("a", result.Output["first"]!.GetValue<string>());
        Assert.Equal("b", result.Output["second"]!.GetValue<string>());
    }

    [Fact]
    public async Task 子工作流递归被深度保护阻止()
    {
        var env = new WorkflowTestEnvironment();
        var wf = WorkflowBuilder.Create("自引用");
        var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
        var sub = wf.AddNode(NodeTypeCatalog.SubWorkflow, "n2").WithConfig(new SubWorkflowNodeConfig
        {
            WorkflowId = wf.Id,
        });
        var end = wf.AddNode(NodeTypeCatalog.End, "n3");
        wf.Connect(start, sub);
        wf.Connect(sub, end);
        env.Definitions.Add(wf);

        var result = await env.RunAsync(
            wf,
            limits: new WorkflowExecutionLimits { MaxSubWorkflowDepth = 2 });

        Assert.Equal(WorkflowRunState.Failed, result.State);
        Assert.Contains("嵌套", result.Record.ErrorSummary!);
    }

    [Fact]
    public async Task 模拟模式不调用真实模型()
    {
        var env = new WorkflowTestEnvironment();
        var wf = BuildDecisionWorkflow(env);
        var mocks = new Dictionary<string, WorkflowMockResponse>
        {
            ["n2"] = new WorkflowMockResponse
            {
                Output = new JsonObject
                {
                    ["route"] = new JsonObject { ["choice"] = "billing" },
                },
            },
        };

        var result = await env.RunAsync(wf, new JsonObject { ["message"] = "hi" }, WorkflowTestMode.Simulated, mocks);

        Assert.Equal(WorkflowRunState.Succeeded, result.State);
        Assert.Equal("billing", result.Output!["route"]!.GetValue<string>());
        Assert.Empty(env.Decisions.Calls);
    }

    [Fact]
    public async Task 模拟模式缺少响应时失败并说明原因()
    {
        var env = new WorkflowTestEnvironment();
        var wf = BuildDecisionWorkflow(env);

        var result = await env.RunAsync(wf, new JsonObject { ["message"] = "hi" }, WorkflowTestMode.Simulated);

        Assert.Equal(WorkflowRunState.Failed, result.State);
        Assert.Contains("模拟模式", result.Record.Nodes.Single(n => n.NodeId == "n2").ErrorMessage!);
    }

    [Fact]
    public async Task 必填输入缺失时运行前失败()
    {
        var env = new WorkflowTestEnvironment();
        var wf = BuildDecisionWorkflow(env);

        var result = await env.RunAsync(wf, new JsonObject());

        Assert.Equal(WorkflowRunState.Failed, result.State);
        Assert.Contains("必填", result.Record.ErrorSummary!);
        Assert.Empty(result.Record.Nodes);
    }

    [Fact]
    public async Task 输入默认值在运行时生效()
    {
        var env = new WorkflowTestEnvironment();
        var wf = WorkflowBuilder.Create();
        wf.Inputs.Add(new WorkflowVariable
        {
            Name = "count",
            Type = WorkflowValueType.Number,
            DefaultValueJson = "5",
        });
        var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
        var transform = wf.AddNode(NodeTypeCatalog.Transform, "n2").WithAssign("value", "input.count");
        var end = wf.AddNode(NodeTypeCatalog.End, "n3").WithConfig(new EndNodeConfig
        {
            OutputMappings = { new WorkflowDataMapping { Target = "value", Source = "up.n2.out.value" } },
        });
        wf.Connect(start, transform);
        wf.Connect(transform, end);

        var result = await env.RunAsync(wf);

        Assert.Equal(WorkflowRunState.Succeeded, result.State);
        Assert.Equal(5, result.Output!["value"]!.GetValue<double>());
    }

    [Fact]
    public async Task 人工确认节点使用交互通道并输出分支()
    {
        var env = new WorkflowTestEnvironment();
        env.Interactions.Decision = ManualDecisionKind.Rejected;
        var wf = WorkflowBuilder.Create();
        var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
        var confirm = wf.AddNode(NodeTypeCatalog.Confirm, "n2").WithConfig(new ManualConfirmNodeConfig
        {
            Title = "请确认退款",
            MessageTemplate = "客户：{{ input.message }}",
        });
        var approved = wf.AddNode(NodeTypeCatalog.Transform, "n3").WithAssign("result", "\"approved\"");
        var rejected = wf.AddNode(NodeTypeCatalog.Transform, "n4").WithAssign("result", "\"rejected\"");
        var end = wf.AddNode(NodeTypeCatalog.End, "n5").WithConfig(new EndNodeConfig
        {
            OutputMappings = { new WorkflowDataMapping { Target = "decision", Source = "up.n2.out.decision" } },
        });
        wf.Connect(start, confirm);
        wf.Connect(confirm, approved, "approved");
        wf.Connect(confirm, rejected, "rejected");
        wf.Connect(approved, end);
        wf.Connect(rejected, end);

        var result = await env.RunAsync(wf, new JsonObject { ["message"] = "refund" });

        Assert.Equal(WorkflowRunState.Succeeded, result.State);
        Assert.Equal("rejected", result.Output!["decision"]!.GetValue<string>());
        Assert.Single(env.Interactions.Prompts);
        Assert.Contains("refund", env.Interactions.Prompts.First().Message);
        Assert.Equal(NodeRunState.Skipped, result.Record.Nodes.Single(n => n.NodeId == "n3").State);
        Assert.Equal(NodeRunState.Succeeded, result.Record.Nodes.Single(n => n.NodeId == "n4").State);
    }

    [Fact]
    public async Task 算法节点输出业务状态()
    {
        var env = new WorkflowTestEnvironment();
        var wf = WorkflowBuilder.Create();
        var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
        var algorithm = wf.AddNode(NodeTypeCatalog.Algorithm, "n2").WithConfig(new AlgorithmNodeConfig
        {
            SolverKey = "sample.ranking",
            InputJsonTemplate = "{\"items\":[{\"key\":\"a\",\"score\":1},{\"key\":\"b\",\"score\":3}],\"topK\":2}",
        });
        var end = wf.AddNode(NodeTypeCatalog.End, "n3").WithConfig(new EndNodeConfig
        {
            OutputMappings =
            {
                new WorkflowDataMapping { Target = "status", Source = "up.n2.out.status" },
                new WorkflowDataMapping { Target = "top", Source = "up.n2.out.result.ranking[0].key" },
            },
        });
        wf.Connect(start, algorithm);
        wf.Connect(algorithm, end);

        var result = await env.RunAsync(wf);

        Assert.Equal(WorkflowRunState.Succeeded, result.State);
        Assert.Equal("Success", result.Output!["status"]!.GetValue<string>());
        Assert.Equal("b", result.Output["top"]!.GetValue<string>());
    }

    [Fact]
    public async Task 结束节点业务失败映射为运行失败()
    {
        var env = new WorkflowTestEnvironment();
        var wf = WorkflowBuilder.Create();
        var start = wf.AddNode(NodeTypeCatalog.Start, "n1");
        var end = wf.AddNode(NodeTypeCatalog.End, "n2").WithConfig(new EndNodeConfig
        {
            Status = WorkflowEndStatus.BusinessFailure,
            FailureMessageTemplate = "校验未通过：{{ input.reason }}",
        });
        wf.Inputs.Add(new WorkflowVariable { Name = "reason", Required = true });
        wf.Connect(start, end);

        var result = await env.RunAsync(wf, new JsonObject { ["reason"] = "规则不满足" });

        Assert.Equal(WorkflowRunState.Failed, result.State);
        Assert.Contains("规则不满足", result.Record.ErrorSummary!);
        Assert.Equal(NodeRunState.Succeeded, result.Record.Nodes.Single(n => n.NodeId == "n2").State);
    }
}
