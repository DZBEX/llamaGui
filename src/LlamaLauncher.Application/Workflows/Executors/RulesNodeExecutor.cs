using System.Text.Json.Nodes;
using LlamaLauncher.Domain.Workflows;
using LlamaLauncher.Domain.Workflows.Expressions;

namespace LlamaLauncher.Application.Workflows.Executors;

/// <summary>规则校验节点：多条规则、错误/警告分级、按配置停止/分支/继续。</summary>
public sealed class RulesNodeExecutor : INodeExecutor
{
    public string TypeId => NodeTypeCatalog.Rules;

    public IReadOnlyList<WorkflowIssue> ValidateConfig(WorkflowNode node, WorkflowValidationContext context)
    {
        var issues = new List<WorkflowIssue>();
        var config = WorkflowJson.TryGetConfig<RulesNodeConfig>(node.Config, out var error);
        if (config is null)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"规则节点“{node.Name}”的配置无法解析：{error}", node.Id));
            return issues;
        }

        if (config.Rules.Count == 0)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"规则节点“{node.Name}”没有任何规则。", node.Id));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in config.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id) || !ids.Add(rule.Id))
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"规则节点“{node.Name}”存在空规则 ID 或重复 ID。", node.Id));
            }

            if (!string.IsNullOrWhiteSpace(rule.Condition) && WorkflowExpression.Validate(rule.Condition) is { } conditionError)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF-VAL-011",
                    $"规则“{rule.Name}”的条件表达式无效：{conditionError}",
                    node.Id));
            }

            if (!string.IsNullOrEmpty(rule.MessageTemplate)
                && WorkflowTemplate.Validate(rule.MessageTemplate, out _) is { } templateError)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF-VAL-012",
                    $"规则“{rule.Name}”的消息模板无效：{templateError}",
                    node.Id));
            }
        }

        return issues;
    }

    public Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext context)
    {
        if (!ExecutionHelpers.TryGetConfig<RulesNodeConfig>(context, out var config, out var error))
        {
            return Task.FromResult(NodeExecutionResult.Fail(error!));
        }

        var hits = new JsonArray();
        var errors = new JsonArray();
        var warnings = new JsonArray();
        foreach (var rule in config!.Rules)
        {
            bool hit;
            if (string.IsNullOrWhiteSpace(rule.Condition))
            {
                hit = true;
            }
            else if (!ExecutionHelpers.TryEvaluateBool(context, rule.Condition, $"规则“{rule.Name}”条件", false, out hit, out var evalError))
            {
                return Task.FromResult(NodeExecutionResult.Fail(evalError!));
            }

            if (!hit)
            {
                continue;
            }

            if (!ExecutionHelpers.TryRenderTemplate(context, rule.MessageTemplate, $"规则“{rule.Name}”消息", out var message, out var templateError))
            {
                return Task.FromResult(NodeExecutionResult.Fail(templateError!));
            }

            var entry = new JsonObject
            {
                ["ruleId"] = rule.Id,
                ["name"] = rule.Name,
                ["severity"] = rule.Severity.ToString(),
                ["message"] = message,
            };
            hits.Add(entry.DeepClone());
            if (rule.Severity == WorkflowIssueSeverity.Error)
            {
                errors.Add(entry.DeepClone());
            }
            else if (rule.Severity == WorkflowIssueSeverity.Warning)
            {
                warnings.Add(entry.DeepClone());
            }
        }

        var passed = errors.Count == 0;
        var outputs = new JsonObject
        {
            ["out"] = new JsonObject
            {
                ["passed"] = passed,
                ["hits"] = hits,
                ["errors"] = errors,
                ["warnings"] = warnings,
            },
        };

        foreach (var entry in hits)
        {
            var level = entry!["severity"]!.GetValue<string>() == nameof(WorkflowIssueSeverity.Error)
                ? WorkflowLogLevel.Error
                : WorkflowLogLevel.Warning;
            context.Log(level, $"规则命中：{entry["name"]!.GetValue<string>()} — {entry["message"]!.GetValue<string>()}", null);
        }

        if (passed)
        {
            context.Log(WorkflowLogLevel.Info, $"规则校验通过（{hits.Count} 条命中，均为提示性）。", null);
            return Task.FromResult(NodeExecutionResult.Ok(
                outputs,
                config.OnFailure == RuleFailureAction.Branch ? new[] { "pass" } : null));
        }

        var summary = string.Join(
            "；",
            errors.OfType<JsonObject>().Select(e => e["message"]?.GetValue<string>()).Where(m => !string.IsNullOrEmpty(m)));
        switch (config.OnFailure)
        {
            case RuleFailureAction.Branch:
                return Task.FromResult(NodeExecutionResult.Ok(outputs, new[] { "fail" }));

            case RuleFailureAction.Continue:
                context.Log(WorkflowLogLevel.Warning, $"规则校验失败但按配置继续：{summary}", null);
                return Task.FromResult(NodeExecutionResult.Ok(outputs));

            default:
                return Task.FromResult(NodeExecutionResult.Fail(new NodeExecutionError(
                    NodeErrorKind.Failed,
                    $"规则校验失败：{summary}",
                    "RuleCheckFailed",
                    new JsonObject { ["errors"] = errors.DeepClone() })));
        }
    }
}
