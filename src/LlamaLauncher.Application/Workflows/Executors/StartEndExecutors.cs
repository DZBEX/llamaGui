using System.Text.Json.Nodes;
using LlamaLauncher.Domain.Workflows;

namespace LlamaLauncher.Application.Workflows.Executors;

/// <summary>开始节点：输入已在引擎侧校验；此处输出输入快照。</summary>
public sealed class StartNodeExecutor : INodeExecutor
{
    public string TypeId => NodeTypeCatalog.Start;

    public IReadOnlyList<WorkflowIssue> ValidateConfig(WorkflowNode node, WorkflowValidationContext context)
        => Array.Empty<WorkflowIssue>();

    public Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext context)
    {
        var snapshot = context.WorkflowInputs.DeepClone() as JsonObject ?? new JsonObject();
        var outputs = new JsonObject { ["out"] = snapshot };
        context.Log(WorkflowLogLevel.Info, $"工作流开始，输入字段：{string.Join("、", snapshot.Select(p => p.Key))}", null);
        return Task.FromResult(NodeExecutionResult.Ok(outputs));
    }
}

/// <summary>结束节点：映射最终输出并声明结束状态（成功/业务失败）。</summary>
public sealed class EndNodeExecutor : INodeExecutor
{
    public string TypeId => NodeTypeCatalog.End;

    public IReadOnlyList<WorkflowIssue> ValidateConfig(WorkflowNode node, WorkflowValidationContext context)
    {
        var issues = new List<WorkflowIssue>();
        var config = WorkflowJson.TryGetConfig<EndNodeConfig>(node.Config, out var error);
        if (config is null)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"结束节点“{node.Name}”的配置无法解析：{error}", node.Id));
            return issues;
        }

        if (!string.IsNullOrEmpty(config.FailureMessageTemplate))
        {
            var templateError = WorkflowTemplate.Validate(config.FailureMessageTemplate, out _);
            if (templateError is not null)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF-VAL-012",
                    $"结束节点“{node.Name}”的失败说明模板无效：{templateError}",
                    node.Id));
            }
        }

        return issues;
    }

    public Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext context)
    {
        if (!ExecutionHelpers.TryGetConfig<EndNodeConfig>(context, out var config, out var error))
        {
            return Task.FromResult(NodeExecutionResult.Fail(error!));
        }

        var output = new JsonObject();
        foreach (var mapping in config!.OutputMappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.Target))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(mapping.Source))
            {
                var fallback = mapping.DefaultValueJson is null
                    ? null
                    : WorkflowJson.TryParse(mapping.DefaultValueJson, out _);
                output[mapping.Target] = fallback?.DeepClone();
                continue;
            }

            if (!ExecutionHelpers.TryEvaluate(context, mapping.Source, $"结束节点输出“{mapping.Target}”", false, out var value, out var evalError))
            {
                return Task.FromResult(NodeExecutionResult.Fail(evalError!));
            }

            if (!ExecutionHelpers.TryConvert(value, mapping.Convert, out var converted, out var convertError))
            {
                return Task.FromResult(NodeExecutionResult.Fail(new NodeExecutionError(
                    NodeErrorKind.Failed,
                    $"结束节点输出“{mapping.Target}”转换失败：{convertError}")));
            }

            output[mapping.Target] = converted?.DeepClone();
        }

        string? message = null;
        if (config.Status == WorkflowEndStatus.BusinessFailure)
        {
            if (!ExecutionHelpers.TryRenderTemplate(context, config.FailureMessageTemplate, "业务失败说明", out var rendered, out var templateError))
            {
                return Task.FromResult(NodeExecutionResult.Fail(templateError!));
            }

            message = string.IsNullOrWhiteSpace(rendered) ? "工作流以业务失败结束。" : rendered;
        }

        var outputs = new JsonObject { ["out"] = output.DeepClone() };
        return Task.FromResult(NodeExecutionResult.Ok(
            outputs,
            diagnostics: null,
            endStatus: config.Status,
            endMessage: message));
    }
}
