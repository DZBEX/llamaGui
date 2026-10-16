using System.Text.Json.Nodes;
using LlamaLauncher.Domain.Workflows;

namespace LlamaLauncher.Application.Workflows.Executors;

/// <summary>人工确认节点：暂停运行等待用户批准/拒绝（本地交互式；无交互通道时明确失败）。</summary>
public sealed class ConfirmNodeExecutor : INodeExecutor
{
    public string TypeId => NodeTypeCatalog.Confirm;

    public IReadOnlyList<WorkflowIssue> ValidateConfig(WorkflowNode node, WorkflowValidationContext context)
    {
        var issues = new List<WorkflowIssue>();
        var config = WorkflowJson.TryGetConfig<ManualConfirmNodeConfig>(node.Config, out var error);
        if (config is null)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"人工确认节点“{node.Name}”的配置无法解析：{error}", node.Id));
            return issues;
        }

        if (string.IsNullOrWhiteSpace(config.Title))
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Warning, "WF-VAL-013", $"人工确认节点“{node.Name}”没有标题。", node.Id));
        }

        if (!string.IsNullOrEmpty(config.MessageTemplate)
            && WorkflowTemplate.Validate(config.MessageTemplate, out _) is { } templateError)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-012", $"人工确认节点“{node.Name}”的说明模板无效：{templateError}", node.Id));
        }

        var fieldIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in config.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Id) || !fieldIds.Add(field.Id))
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"人工确认节点“{node.Name}”存在空字段 ID 或重复 ID。", node.Id));
            }
        }

        return issues;
    }

    public async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext context)
    {
        if (!ExecutionHelpers.TryGetConfig<ManualConfirmNodeConfig>(context, out var config, out var error))
        {
            return NodeExecutionResult.Fail(error!);
        }

        if (!ExecutionHelpers.TryRenderTemplate(context, config!.MessageTemplate, "人工确认说明", out var message, out var templateError))
        {
            return NodeExecutionResult.Fail(templateError!);
        }

        var broker = context.Services.Interactions;
        if (broker is null)
        {
            return NodeExecutionResult.Fail(new NodeExecutionError(
                NodeErrorKind.Failed,
                "工作流包含人工确认节点，但当前运行环境未配置交互通道（请在实验室界面中运行，或移除该节点）。",
                "InteractionUnavailable"));
        }

        var prompt = new WorkflowUserPrompt(
            context.RunId,
            context.Node.Id,
            string.IsNullOrWhiteSpace(config.Title) ? "请确认" : config.Title,
            message,
            config.Fields,
            config.ApproveLabel,
            config.RejectLabel);
        context.Log(WorkflowLogLevel.Info, $"等待人工确认：{prompt.Title}", null);

        WorkflowUserDecision decision;
        try
        {
            decision = await broker.RequestAsync(prompt, context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return NodeExecutionResult.Fail(new NodeExecutionError(
                NodeErrorKind.Failed,
                $"人工确认失败：{ex.Message}",
                "InteractionFailed"));
        }

        var kind = decision.Kind == ManualDecisionKind.Approved ? "approved" : "rejected";
        context.Log(
            WorkflowLogLevel.Info,
            $"人工确认结果：{(decision.Kind == ManualDecisionKind.Approved ? config.ApproveLabel : config.RejectLabel)}" +
            (decision.Fields.Count > 0 ? $"（填写字段：{string.Join("、", decision.Fields.Select(f => f.Key))}）" : string.Empty),
            null);

        var outputs = new JsonObject
        {
            ["out"] = new JsonObject
            {
                ["decision"] = kind,
                ["fields"] = decision.Fields.DeepClone(),
                ["decidedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            },
        };
        return NodeExecutionResult.Ok(outputs, new[] { kind });
    }
}
