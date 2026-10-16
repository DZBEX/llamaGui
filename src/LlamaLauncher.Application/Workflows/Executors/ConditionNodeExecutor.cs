using System.Text.Json.Nodes;
using LlamaLauncher.Domain.Workflows;
using LlamaLauncher.Domain.Workflows.Expressions;

namespace LlamaLauncher.Application.Workflows.Executors;

/// <summary>条件分支节点：按顺序求值命名的安全表达式分支，默认分支兜底。</summary>
public sealed class ConditionNodeExecutor : INodeExecutor
{
    public string TypeId => NodeTypeCatalog.Condition;

    public IReadOnlyList<WorkflowIssue> ValidateConfig(WorkflowNode node, WorkflowValidationContext context)
    {
        var issues = new List<WorkflowIssue>();
        var config = WorkflowJson.TryGetConfig<ConditionNodeConfig>(node.Config, out var error);
        if (config is null)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"条件节点“{node.Name}”的配置无法解析：{error}", node.Id));
            return issues;
        }

        if (config.Branches.Count == 0)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"条件节点“{node.Name}”没有分支。", node.Id));
        }

        var defaults = config.Branches.Count(b => b.IsDefault);
        if (defaults != 1)
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-009",
                $"条件节点“{node.Name}”必须恰好有一个默认分支（当前 {defaults} 个）。",
                node.Id,
                hint: "把其中一个分支设为默认，或调整分支配置。"));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var branch in config.Branches)
        {
            if (string.IsNullOrWhiteSpace(branch.Id))
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"条件节点“{node.Name}”存在空分支标识。", node.Id));
                continue;
            }

            if (!ids.Add(branch.Id))
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"条件节点“{node.Name}”的分支“{branch.Id}”重复。", node.Id));
            }

            if (branch.IsDefault)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(branch.Condition))
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF-VAL-010",
                    $"条件节点“{node.Name}”的分支“{branch.Name}”缺少条件表达式。",
                    node.Id));
                continue;
            }

            if (WorkflowExpression.Validate(branch.Condition) is { } conditionError)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF-VAL-011",
                    $"条件节点“{node.Name}”的分支“{branch.Name}”表达式无效：{conditionError}",
                    node.Id));
            }
        }

        return issues;
    }

    public Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext context)
    {
        if (!ExecutionHelpers.TryGetConfig<ConditionNodeConfig>(context, out var config, out var error))
        {
            return Task.FromResult(NodeExecutionResult.Fail(error!));
        }

        ConditionBranch? selected = null;
        var evaluations = new JsonArray();
        foreach (var branch in config!.Branches)
        {
            if (branch.IsDefault)
            {
                continue;
            }

            if (!ExecutionHelpers.TryEvaluateBool(context, branch.Condition, $"分支“{branch.Name}”条件", false, out var result, out var evalError))
            {
                return Task.FromResult(NodeExecutionResult.Fail(evalError!));
            }

            evaluations.Add(new JsonObject
            {
                ["branchId"] = branch.Id,
                ["condition"] = branch.Condition,
                ["result"] = result,
            });

            if (result && selected is null)
            {
                selected = branch;
            }
        }

        selected ??= config.Branches.FirstOrDefault(b => b.IsDefault);
        if (selected is null)
        {
            return Task.FromResult(NodeExecutionResult.Fail(new NodeExecutionError(
                NodeErrorKind.Failed,
                "没有分支命中，且未配置默认分支。",
                "ConditionNoMatch")));
        }

        context.Log(
            WorkflowLogLevel.Info,
            $"条件判定：选择分支“{selected.Name}”（{selected.Id}）。",
            null);

        var outputs = new JsonObject
        {
            ["out"] = new JsonObject
            {
                ["branchId"] = selected.Id,
                ["branchName"] = selected.Name,
                ["evaluations"] = evaluations,
            },
        };
        return Task.FromResult(NodeExecutionResult.Ok(outputs, new[] { selected.Id }));
    }
}
