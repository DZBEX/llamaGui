using System.Text.Json.Nodes;
using LlamaLauncher.Domain.Workflows;

namespace LlamaLauncher.Application.Workflows.Executors;

/// <summary>子工作流节点：调用已保存的工作流（输入映射 + 递归/深度保护 + 取消传播）。</summary>
public sealed class SubWorkflowNodeExecutor : INodeExecutor
{
    public string TypeId => NodeTypeCatalog.SubWorkflow;

    public IReadOnlyList<WorkflowIssue> ValidateConfig(WorkflowNode node, WorkflowValidationContext context)
    {
        var issues = new List<WorkflowIssue>();
        var config = WorkflowJson.TryGetConfig<SubWorkflowNodeConfig>(node.Config, out var error);
        if (config is null)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"子工作流节点“{node.Name}”的配置无法解析：{error}", node.Id));
            return issues;
        }

        if (string.IsNullOrWhiteSpace(config.WorkflowId))
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-016",
                $"子工作流节点“{node.Name}”未选择要调用的工作流。",
                node.Id));
        }
        else if (context.WorkflowExists is { } exists && !exists(config.WorkflowId))
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-016",
                $"子工作流节点“{node.Name}”引用的工作流不存在（{config.WorkflowNameSnapshot ?? config.WorkflowId}）。",
                node.Id));
        }
        else if (context.WorkflowExists is null && context.ForRun)
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Info,
                "WF-VAL-016",
                $"子工作流节点“{node.Name}”的引用将在运行时加载校验。",
                node.Id));
        }

        if (config.TimeoutSeconds is { } timeout && (timeout < 1 || timeout > 86_400))
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-013",
                $"子工作流节点“{node.Name}”的超时需在 1–86400 秒之间。",
                node.Id));
        }

        return issues;
    }

    public async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext context)
    {
        if (!ExecutionHelpers.TryGetConfig<SubWorkflowNodeConfig>(context, out var config, out var error))
        {
            return NodeExecutionResult.Fail(error!);
        }

        if (string.IsNullOrWhiteSpace(config!.WorkflowId))
        {
            return NodeExecutionResult.Fail(new NodeExecutionError(
                NodeErrorKind.Failed,
                "子工作流节点未选择要调用的工作流。",
                "SubWorkflowMissing"));
        }

        if (context.Services.ChildRunner is not { } childRunner)
        {
            return NodeExecutionResult.Fail(new NodeExecutionError(
                NodeErrorKind.Failed,
                "子工作流执行通道不可用（运行环境未注入子流程执行器）。",
                "SubWorkflowUnavailable"));
        }

        var loadResult = await context.Services.DefinitionStore
            .LoadAsync(config.WorkflowId, context.CancellationToken)
            .ConfigureAwait(false);
        if (loadResult.Definition is null)
        {
            var detail = string.Join("；", loadResult.Issues.Select(i => i.Message));
            return NodeExecutionResult.Fail(new NodeExecutionError(
                NodeErrorKind.Failed,
                $"无法加载子工作流（{config.WorkflowNameSnapshot ?? config.WorkflowId}）：{detail}",
                "SubWorkflowLoadFailed"));
        }

        var inputs = context.Inputs.DeepClone() as JsonObject ?? new JsonObject();
        var timeoutSeconds = config.TimeoutSeconds
            ?? context.Node.TimeoutSeconds
            ?? (int)Math.Clamp(context.Limits.DefaultNodeTimeout.TotalSeconds, 1, 86_400);

        WorkflowRunResult childResult;
        try
        {
            childResult = await childRunner.RunChildAsync(
                    loadResult.Definition,
                    inputs,
                    context.TestMode,
                    null,
                    context.Meta["defaultInstanceId"]?.GetValue<string>(),
                    Math.Clamp(timeoutSeconds, 1, 86_400),
                    context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            return NodeExecutionResult.Fail(new NodeExecutionError(
                NodeErrorKind.Failed,
                $"子工作流执行失败：{ex.Message}",
                "SubWorkflowRejected"));
        }

        var childOutput = childResult.Output?.DeepClone();
        var outputs = new JsonObject
        {
            ["out"] = childOutput,
            ["childRunId"] = childResult.Record.RunId,
            ["childState"] = childResult.Record.State.ToString(),
        };

        if (childResult.Record.State is WorkflowRunState.Succeeded)
        {
            context.Log(
                WorkflowLogLevel.Info,
                $"子工作流“{childResult.Record.WorkflowNameSnapshot}”执行成功（RunId：{childResult.Record.RunId}）。",
                null);
            return NodeExecutionResult.Ok(outputs);
        }

        return NodeExecutionResult.Fail(new NodeExecutionError(
            NodeErrorKind.Failed,
            $"子工作流“{childResult.Record.WorkflowNameSnapshot}”未成功（{childResult.Record.State}）：{childResult.Record.ErrorSummary ?? "无错误摘要"}",
            "SubWorkflowFailed",
            new JsonObject
            {
                ["childRunId"] = childResult.Record.RunId,
                ["childState"] = childResult.Record.State.ToString(),
            }));
    }
}
