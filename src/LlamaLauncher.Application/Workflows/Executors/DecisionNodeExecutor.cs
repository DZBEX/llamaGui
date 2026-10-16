using LlamaLauncher.Domain.Workflows;

namespace LlamaLauncher.Application.Workflows.Executors;

/// <summary>决策模型节点（/v1/systemone；复用启动器实例管理）。</summary>
public sealed class DecisionNodeExecutor : INodeExecutor
{
    public string TypeId => NodeTypeCatalog.Decision;

    public IReadOnlyList<WorkflowIssue> ValidateConfig(WorkflowNode node, WorkflowValidationContext context)
    {
        var config = WorkflowJson.TryGetConfig<DecisionNodeConfig>(node.Config, out var error);
        if (config is null)
        {
            return new[]
            {
                new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF-VAL-013",
                    $"决策节点“{node.Name}”的配置无法解析：{error}",
                    node.Id),
            };
        }

        return DecisionInvocation.ValidateConfig(node, config, context, $"决策节点“{node.Name}”");
    }

    public Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext context)
    {
        if (!ExecutionHelpers.TryGetConfig<DecisionNodeConfig>(context, out var config, out var error))
        {
            return Task.FromResult(NodeExecutionResult.Fail(error!));
        }

        return DecisionInvocation.InvokeAsync(context, config!, context.CancellationToken);
    }
}
