using System.Text.Json.Nodes;
using LlamaLauncher.Domain.Workflows;

namespace LlamaLauncher.Application.Workflows.Executors;

/// <summary>
/// 算法求解节点：调用注册的求解器（含内置示例求解器；接口可扩展）。
/// 无解/无可行解等是业务结果（写入输出交后续节点校验），只有配置或调用错误才使节点失败。
/// </summary>
public sealed class AlgorithmNodeExecutor : INodeExecutor
{
    public string TypeId => NodeTypeCatalog.Algorithm;

    public IReadOnlyList<WorkflowIssue> ValidateConfig(WorkflowNode node, WorkflowValidationContext context)
    {
        var issues = new List<WorkflowIssue>();
        var config = WorkflowJson.TryGetConfig<AlgorithmNodeConfig>(node.Config, out var error);
        if (config is null)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"算法节点“{node.Name}”的配置无法解析：{error}", node.Id));
            return issues;
        }

        if (string.IsNullOrWhiteSpace(config.SolverKey))
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-013",
                $"算法节点“{node.Name}”未选择求解器。",
                node.Id));
        }
        else if (context.AlgorithmKeys is { } keys && !keys.Contains(config.SolverKey, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-057",
                $"算法节点“{node.Name}”引用的求解器“{config.SolverKey}”未注册。",
                node.Id,
                hint: "可用求解器：" + (keys.Count == 0 ? "（无）" : string.Join("、", keys))));
        }

        if (!string.IsNullOrWhiteSpace(config.InputJsonTemplate)
            && WorkflowTemplate.Validate(config.InputJsonTemplate, out _) is { } templateError)
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-012",
                $"算法节点“{node.Name}”的输入模板无效：{templateError}",
                node.Id));
        }

        if (config.TimeoutSeconds is < 1 or > 3_600)
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-013",
                $"算法节点“{node.Name}”的超时需在 1–3600 秒之间。",
                node.Id));
        }

        return issues;
    }

    public async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext context)
    {
        if (!ExecutionHelpers.TryGetConfig<AlgorithmNodeConfig>(context, out var config, out var error))
        {
            return NodeExecutionResult.Fail(error!);
        }

        var algorithm = context.Services.Algorithms.Find(config!.SolverKey);
        if (algorithm is null)
        {
            var available = context.Services.Algorithms.List().Select(a => a.Key).ToList();
            return NodeExecutionResult.Fail(new NodeExecutionError(
                NodeErrorKind.Failed,
                $"求解器“{config.SolverKey}”未注册。可用求解器：" +
                (available.Count == 0 ? "（无）" : string.Join("、", available)),
                "AlgorithmNotFound"));
        }

        JsonNode? input;
        if (!string.IsNullOrWhiteSpace(config.InputJsonTemplate))
        {
            if (!ExecutionHelpers.TryRenderTemplate(context, config.InputJsonTemplate, "算法输入模板", out var rendered, out error))
            {
                return NodeExecutionResult.Fail(error!);
            }

            input = WorkflowJson.TryParse(rendered, out var parseError);
            if (input is null && !string.IsNullOrWhiteSpace(rendered))
            {
                return NodeExecutionResult.Fail(new NodeExecutionError(
                    NodeErrorKind.Failed,
                    $"算法输入模板渲染结果不是有效 JSON：{parseError}",
                    "AlgorithmInputInvalid"));
            }
        }
        else
        {
            input = context.Inputs.TryGetPropertyValue("input", out var mapped) && context.Inputs.Count == 1
                ? mapped?.DeepClone()
                : context.Inputs.DeepClone();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 1, 3600)));
        AlgorithmInvocationResult result;
        try
        {
            result = await algorithm.InvokeAsync(input, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
            result = new AlgorithmInvocationResult(
                AlgorithmStatus.Timeout,
                null,
                $"求解超时（{config.TimeoutSeconds} 秒）。",
                TimeSpan.FromSeconds(config.TimeoutSeconds));
        }

        var outputs = new JsonObject
        {
            ["out"] = new JsonObject
            {
                ["status"] = result.Status.ToString(),
                ["solver"] = algorithm.Key,
                ["result"] = result.Result?.DeepClone(),
                ["message"] = result.Message,
            },
        };
        var diagnostics = new JsonObject
        {
            ["elapsedMs"] = Math.Round(result.Elapsed.TotalMilliseconds, 1),
            ["solver"] = algorithm.Key,
            ["status"] = result.Status.ToString(),
        };

        var level = result.Status == AlgorithmStatus.Success ? WorkflowLogLevel.Info : WorkflowLogLevel.Warning;
        context.Log(
            level,
            $"算法“{algorithm.DisplayName}”执行完成：{result.Status}" +
            (string.IsNullOrWhiteSpace(result.Message) ? "。" : $"（{result.Message}）。"),
            null);
        return NodeExecutionResult.Ok(outputs, diagnostics: diagnostics);
    }
}
