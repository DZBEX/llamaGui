using System.Text.Json.Nodes;
using LlamaLauncher.Domain.Workflows;
using LlamaLauncher.Domain.Workflows.Expressions;

namespace LlamaLauncher.Application.Workflows.Executors;

/// <summary>
/// 循环节点：对数组逐项执行子工作流或决策模型模板。
/// 有最大迭代、单项超时与取消限制；防止无限循环；逐项结果完整返回。
/// </summary>
public sealed class LoopNodeExecutor : INodeExecutor
{
    public string TypeId => NodeTypeCatalog.Loop;

    public IReadOnlyList<WorkflowIssue> ValidateConfig(WorkflowNode node, WorkflowValidationContext context)
    {
        var issues = new List<WorkflowIssue>();
        var config = WorkflowJson.TryGetConfig<LoopNodeConfig>(node.Config, out var error);
        if (config is null)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"循环节点“{node.Name}”的配置无法解析：{error}", node.Id));
            return issues;
        }

        if (string.IsNullOrWhiteSpace(config.ItemsExpression))
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-013",
                $"循环节点“{node.Name}”未配置数组来源表达式。",
                node.Id));
        }
        else if (WorkflowExpression.Validate(config.ItemsExpression) is { } itemsError)
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-011",
                $"循环节点“{node.Name}”的数组来源表达式无效：{itemsError}",
                node.Id));
        }
        else if (WorkflowExpression.TryParse(config.ItemsExpression, out var parsed, out _))
        {
            WorkflowValidator.CheckPurePath(context.Definition, node, parsed!.PurePath, "循环数组来源", issues);
        }

        if (config.MaxIterations is < 1 or > 1_000)
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-015",
                $"循环节点“{node.Name}”的最大迭代次数需在 1–1000 之间（防止无限循环）。",
                node.Id));
        }

        if (config.ItemTimeoutSeconds is { } itemTimeout && (itemTimeout < 1 || itemTimeout > 86_400))
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-013",
                $"循环节点“{node.Name}”的单项超时需在 1–86400 秒之间。",
                node.Id));
        }

        switch (config.Mode)
        {
            case LoopMode.SubWorkflow:
                if (string.IsNullOrWhiteSpace(config.SubWorkflowId))
                {
                    issues.Add(new WorkflowIssue(
                        WorkflowIssueSeverity.Error,
                        "WF-VAL-016",
                        $"循环节点“{node.Name}”未选择要逐项调用的子工作流。",
                        node.Id));
                }
                else if (context.WorkflowExists is { } exists && !exists(config.SubWorkflowId))
                {
                    issues.Add(new WorkflowIssue(
                        WorkflowIssueSeverity.Error,
                        "WF-VAL-016",
                        $"循环节点“{node.Name}”引用的子工作流不存在（{config.SubWorkflowNameSnapshot ?? config.SubWorkflowId}）。",
                        node.Id));
                }

                break;

            case LoopMode.DecisionModel:
                if (config.DecisionTemplate is null)
                {
                    issues.Add(new WorkflowIssue(
                        WorkflowIssueSeverity.Error,
                        "WF-VAL-013",
                        $"循环节点“{node.Name}”缺少决策模板配置。",
                        node.Id));
                }
                else
                {
                    issues.AddRange(DecisionInvocation.ValidateConfig(
                        node,
                        config.DecisionTemplate,
                        context,
                        $"循环节点“{node.Name}”的决策模板"));
                }

                break;
        }

        return issues;
    }

    public async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext context)
    {
        if (!ExecutionHelpers.TryGetConfig<LoopNodeConfig>(context, out var config, out var error))
        {
            return NodeExecutionResult.Fail(error!);
        }

        if (!ExecutionHelpers.TryEvaluate(context, config!.ItemsExpression, "循环数组来源", true, out var itemsValue, out var itemsError))
        {
            return NodeExecutionResult.Fail(itemsError!);
        }

        if (itemsValue is not JsonArray items)
        {
            return NodeExecutionResult.Fail(new NodeExecutionError(
                NodeErrorKind.Failed,
                "循环数组来源的结果不是数组。",
                "LoopItemsInvalid"));
        }

        if (items.Count > config.MaxIterations)
        {
            return NodeExecutionResult.Fail(new NodeExecutionError(
                NodeErrorKind.Failed,
                $"循环项数（{items.Count}）超过配置的最大迭代次数（{config.MaxIterations}）。",
                "LoopIterationLimit"));
        }

        var results = new JsonArray();
        var succeeded = 0;
        var failed = 0;
        for (var index = 0; index < items.Count; index++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var item = items[index];
            context.Log(WorkflowLogLevel.Debug, $"循环第 {index + 1}/{items.Count} 项开始。", null);

            var outcome = config.Mode == LoopMode.SubWorkflow
                ? await RunSubWorkflowItemAsync(context, config, item, index).ConfigureAwait(false)
                : await RunDecisionItemAsync(context, config, item, index).ConfigureAwait(false);

            if (outcome.Status)
            {
                succeeded++;
            }
            else
            {
                failed++;
            }

            results.Add(new JsonObject
            {
                ["index"] = index,
                ["item"] = item?.DeepClone(),
                ["status"] = outcome.Status ? "succeeded" : "failed",
                ["result"] = outcome.Result?.DeepClone(),
                ["error"] = outcome.Error,
            });

            if (!outcome.Status && !config.ContinueOnItemError)
            {
                return NodeExecutionResult.Fail(new NodeExecutionError(
                    NodeErrorKind.Failed,
                    $"循环第 {index + 1} 项失败：{outcome.Error ?? "未知错误"}（已按配置停止循环）。",
                    "LoopItemFailed",
                    new JsonObject { ["failedIndex"] = index, ["items"] = results.DeepClone() }));
            }
        }

        var outputs = new JsonObject
        {
            ["out"] = new JsonObject
            {
                ["total"] = items.Count,
                ["succeeded"] = succeeded,
                ["failed"] = failed,
                ["items"] = results,
            },
        };
        context.Log(
            WorkflowLogLevel.Info,
            $"循环完成：共 {items.Count} 项，成功 {succeeded}，失败 {failed}。",
            null);
        return NodeExecutionResult.Ok(outputs);
    }

    private static NodeExecutionContext BuildItemContext(NodeExecutionContext context, JsonNode? item, int index)
    {
        var scope = context.Scope.DeepClone() as JsonObject ?? new JsonObject();
        scope["item"] = item?.DeepClone();
        scope["index"] = JsonValue.Create((double)index);
        return new NodeExecutionContext
        {
            Node = context.Node,
            RunId = context.RunId,
            TestMode = context.TestMode,
            Inputs = context.Inputs,
            Scope = scope,
            WorkflowInputs = context.WorkflowInputs,
            Variables = context.Variables,
            Upstream = context.Upstream,
            Meta = context.Meta,
            ErrorContext = context.ErrorContext,
            Services = context.Services,
            Limits = context.Limits,
            Mock = context.Mock,
            Attempt = context.Attempt,
            Log = context.Log,
            CancellationToken = context.CancellationToken,
        };
    }

    private static async Task<(bool Status, JsonNode? Result, string? Error)> RunDecisionItemAsync(
        NodeExecutionContext context,
        LoopNodeConfig config,
        JsonNode? item,
        int index)
    {
        if (config.DecisionTemplate is null)
        {
            return (false, null, "循环缺少决策模板配置。");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        if (config.ItemTimeoutSeconds is { } timeoutSeconds)
        {
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        }

        var itemContext = BuildItemContext(context, item, index);
        var result = await DecisionInvocation.InvokeAsync(itemContext, config.DecisionTemplate, cts.Token)
            .ConfigureAwait(false);
        if (result.Success)
        {
            return (true, result.Outputs["out"]?.DeepClone(), null);
        }

        return (false, null, result.Error?.Message ?? "决策调用失败。");
    }

    private static async Task<(bool Status, JsonNode? Result, string? Error)> RunSubWorkflowItemAsync(
        NodeExecutionContext context,
        LoopNodeConfig config,
        JsonNode? item,
        int index)
    {
        if (context.Services.ChildRunner is not { } childRunner)
        {
            return (false, null, "子工作流执行通道不可用。");
        }

        var loadResult = await context.Services.DefinitionStore
            .LoadAsync(config.SubWorkflowId!, context.CancellationToken)
            .ConfigureAwait(false);
        if (loadResult.Definition is null)
        {
            return (false, null, $"无法加载子工作流：{string.Join("；", loadResult.Issues.Select(i => i.Message))}");
        }

        var inputs = context.Inputs.DeepClone() as JsonObject ?? new JsonObject();
        inputs["item"] = item?.DeepClone();
        inputs["index"] = JsonValue.Create((double)index);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var timeoutSeconds = config.ItemTimeoutSeconds
            ?? (int)Math.Clamp(context.Limits.DefaultNodeTimeout.TotalSeconds, 1, 86_400);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var childResult = await childRunner.RunChildAsync(
                loadResult.Definition,
                inputs,
                context.TestMode,
                null,
                context.Meta["defaultInstanceId"]?.GetValue<string>(),
                Math.Clamp(timeoutSeconds, 1, 86_400),
                cts.Token)
            .ConfigureAwait(false);

        if (childResult.Record.State == WorkflowRunState.Succeeded)
        {
            return (true, childResult.Output?.DeepClone(), null);
        }

        return (false, null, $"子工作流返回 {childResult.Record.State}：{childResult.Record.ErrorSummary ?? "无错误摘要"}");
    }
}
