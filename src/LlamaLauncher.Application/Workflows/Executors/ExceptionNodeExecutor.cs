using System.Text.Json.Nodes;
using LlamaLauncher.Domain.Workflows;

namespace LlamaLauncher.Application.Workflows.Executors;

/// <summary>异常处理节点：接收错误上下文，记录或重新抛出。</summary>
public sealed class ExceptionNodeExecutor : INodeExecutor
{
    public string TypeId => NodeTypeCatalog.Exception;

    public IReadOnlyList<WorkflowIssue> ValidateConfig(WorkflowNode node, WorkflowValidationContext context)
    {
        var issues = new List<WorkflowIssue>();
        var config = WorkflowJson.TryGetConfig<ExceptionNodeConfig>(node.Config, out var error);
        if (config is null)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"异常处理节点“{node.Name}”的配置无法解析：{error}", node.Id));
        }

        return issues;
    }

    public Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext context)
    {
        if (!ExecutionHelpers.TryGetConfig<ExceptionNodeConfig>(context, out var config, out var error))
        {
            return Task.FromResult(NodeExecutionResult.Fail(error!));
        }

        var errorContext = context.ErrorContext;
        if (errorContext is null)
        {
            context.Log(WorkflowLogLevel.Warning, "异常处理节点在非错误路径上被执行（没有错误上下文）。", null);
            errorContext = new JsonObject { ["type"] = "None", ["message"] = "没有错误上下文。" };
        }

        if (config!.RecordLog)
        {
            var type = errorContext["type"]?.GetValue<string>() ?? "Unknown";
            var message = errorContext["message"]?.GetValue<string>() ?? "";
            var sourceNode = errorContext["nodeId"]?.GetValue<string>();
            context.Log(
                WorkflowLogLevel.Error,
                $"异常捕获：{type} — {message}（来源节点：{sourceNode ?? "未知"}）",
                null);
        }

        if (config.Action == ExceptionAction.Rethrow)
        {
            return Task.FromResult(NodeExecutionResult.Fail(new NodeExecutionError(
                NodeErrorKind.Failed,
                $"按配置重新抛出异常：{errorContext["message"]?.GetValue<string>()}",
                errorContext["type"]?.GetValue<string>() ?? "Rethrow",
                errorContext.DeepClone() as JsonObject)));
        }

        var outputs = new JsonObject
        {
            ["out"] = new JsonObject
            {
                ["handled"] = true,
                ["error"] = errorContext.DeepClone(),
            },
        };
        return Task.FromResult(NodeExecutionResult.Ok(outputs));
    }
}
