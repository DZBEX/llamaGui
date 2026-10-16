using System.Text.Json.Nodes;
using LlamaLauncher.Domain.Workflows;
using LlamaLauncher.Domain.Workflows.Expressions;

namespace LlamaLauncher.Application.Workflows.Executors;

/// <summary>并行扇出节点：把共享值写向所有分支（实际并发由调度器按最大并发执行）。</summary>
public sealed class ParallelNodeExecutor : INodeExecutor
{
    public string TypeId => NodeTypeCatalog.Parallel;

    public IReadOnlyList<WorkflowIssue> ValidateConfig(WorkflowNode node, WorkflowValidationContext context)
    {
        var issues = new List<WorkflowIssue>();
        var config = WorkflowJson.TryGetConfig<ParallelNodeConfig>(node.Config, out var error);
        if (config is null)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"并行节点“{node.Name}”的配置无法解析：{error}", node.Id));
            return issues;
        }

        if (config.Branches.Count < 1)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"并行节点“{node.Name}”至少需要一个分支。", node.Id));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var branch in config.Branches)
        {
            if (string.IsNullOrWhiteSpace(branch.Id) || !ids.Add(branch.Id))
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"并行节点“{node.Name}”存在空分支 ID 或重复 ID。", node.Id));
            }
        }

        if (config.MaxConcurrency is { } max && (max < 1 || max > 8))
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"并行节点“{node.Name}”的最大并发需在 1–8 之间。", node.Id));
        }

        if (!string.IsNullOrWhiteSpace(config.SharedExpression) && WorkflowExpression.Validate(config.SharedExpression) is { } sharedError)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-011", $"并行节点“{node.Name}”的共享值表达式无效：{sharedError}", node.Id));
        }

        return issues;
    }

    public Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext context)
    {
        if (!ExecutionHelpers.TryGetConfig<ParallelNodeConfig>(context, out var config, out var error))
        {
            return Task.FromResult(NodeExecutionResult.Fail(error!));
        }

        JsonNode? shared = null;
        if (!string.IsNullOrWhiteSpace(config!.SharedExpression))
        {
            if (!ExecutionHelpers.TryEvaluate(context, config.SharedExpression, "并行共享值", false, out shared, out var evalError))
            {
                return Task.FromResult(NodeExecutionResult.Fail(evalError!));
            }
        }

        var branches = config.Branches.Select(b => b.Id).ToList();
        context.Log(WorkflowLogLevel.Info, $"并行扇出 {branches.Count} 个分支：{string.Join("、", branches)}。", null);
        var outputs = new JsonObject { ["out"] = shared?.DeepClone() };
        return Task.FromResult(NodeExecutionResult.Ok(outputs, branches));
    }
}

/// <summary>并行汇合节点：按策略汇总分支结果（FailFast 时任一分支失败即失败）。</summary>
public sealed class JoinNodeExecutor : INodeExecutor
{
    public string TypeId => NodeTypeCatalog.Join;

    public IReadOnlyList<WorkflowIssue> ValidateConfig(WorkflowNode node, WorkflowValidationContext context)
    {
        var issues = new List<WorkflowIssue>();
        var config = WorkflowJson.TryGetConfig<JoinNodeConfig>(node.Config, out var error);
        if (config is null)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"汇合节点“{node.Name}”的配置无法解析：{error}", node.Id));
        }

        return issues;
    }

    public Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext context)
    {
        if (!ExecutionHelpers.TryGetConfig<JoinNodeConfig>(context, out var config, out var error))
        {
            return Task.FromResult(NodeExecutionResult.Fail(error!));
        }

        var branches = new JsonObject();
        var failed = new JsonArray();
        var errors = new JsonObject();
        foreach (var pair in context.Upstream)
        {
            if (pair.Value is not JsonObject upstream)
            {
                continue;
            }

            if (upstream["error"] is JsonObject errorInfo)
            {
                failed.Add(pair.Key);
                errors[pair.Key] = errorInfo.DeepClone();
            }
            else if (upstream["out"] is { } outValue)
            {
                branches[pair.Key] = outValue.DeepClone();
            }
        }

        if (config!.Mode == ParallelJoinMode.FailFast && failed.Count > 0)
        {
            return Task.FromResult(NodeExecutionResult.Fail(new NodeExecutionError(
                NodeErrorKind.Failed,
                $"并行分支失败（FailFast）：{string.Join("、", failed.Select(f => f!.GetValue<string>()))}",
                "ParallelFailFast",
                new JsonObject { ["failedBranches"] = failed.DeepClone(), ["errors"] = errors.DeepClone() })));
        }

        var outputs = new JsonObject
        {
            ["out"] = new JsonObject
            {
                ["branches"] = branches,
                ["failedBranches"] = failed,
                ["errors"] = errors,
            },
        };
        context.Log(
            WorkflowLogLevel.Info,
            $"并行汇合完成：成功 {branches.Count} 个分支，失败 {failed.Count} 个（策略：{config.Mode}）。",
            null);
        return Task.FromResult(NodeExecutionResult.Ok(outputs));
    }
}
