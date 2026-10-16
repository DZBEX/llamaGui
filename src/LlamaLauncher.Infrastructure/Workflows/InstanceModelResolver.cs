using System.Text.Json.Nodes;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Instances;
using LlamaLauncher.Application.Workflows;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.Workflows;

/// <summary>
/// 工作流模型解析：复用启动器实例管理（不新起进程），把实例转化为可调用的决策服务目标，
/// 并提供非敏感的上下文快照（实例名、端口、模型文件名等）用于运行记录。
/// </summary>
public sealed class InstanceModelResolver : IWorkflowModelResolver
{
    private readonly IInstanceManager _instances;
    private readonly IModelRepository _models;
    private readonly ILogger<InstanceModelResolver> _logger;

    public InstanceModelResolver(
        IInstanceManager instances,
        IModelRepository models,
        ILogger<InstanceModelResolver> logger)
    {
        _instances = instances;
        _models = models;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WorkflowModelTarget>> ListAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<WorkflowModelTarget>();
        foreach (var instance in _instances.GetInstances())
        {
            results.Add(await ToTargetAsync(instance, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public async Task<WorkflowModelTarget?> ResolveAsync(
        string? instanceId,
        CancellationToken cancellationToken = default)
    {
        var instances = _instances.GetInstances();
        ManagedInstance? instance;
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            instance = instances.FirstOrDefault(i => i.State == InstanceState.Ready) ?? instances.FirstOrDefault();
        }
        else
        {
            instance = instances.FirstOrDefault(i =>
                string.Equals(i.Id.ToString("D"), instanceId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(i.Id.ToString("N"), instanceId, StringComparison.OrdinalIgnoreCase));
        }

        return instance is null ? null : await ToTargetAsync(instance, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetApiKeyAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(instanceId, out var id))
        {
            return null;
        }

        try
        {
            return await _instances.GetInstanceApiKeyAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取实例 API Key 失败（InstanceId={InstanceId}）。", instanceId);
            return null;
        }
    }

    private async Task<WorkflowModelTarget> ToTargetAsync(ManagedInstance instance, CancellationToken cancellationToken)
    {
        string? modelFile = null;
        if (instance.Record.ModelId is { } modelId)
        {
            try
            {
                var model = await _models.GetByIdAsync(modelId, cancellationToken).ConfigureAwait(false);
                modelFile = model?.FilePath is { } path ? Path.GetFileName(path) : null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "读取实例模型信息失败（InstanceId={InstanceId}）。", instance.Id);
            }
        }

        var ready = instance.State == InstanceState.Ready;
        var reason = ready ? null : $"当前状态：{instance.State}";
        var snapshot = new JsonObject
        {
            ["instanceId"] = instance.Id.ToString("D"),
            ["instanceName"] = instance.Name,
            ["port"] = instance.Record.Port,
            ["baseUrl"] = instance.LocalBaseUrl,
            ["modelFile"] = modelFile,
            ["state"] = instance.State.ToString(),
        };
        return new WorkflowModelTarget(
            instance.Id.ToString("D"),
            instance.Name,
            instance.LocalBaseUrl,
            ready,
            reason,
            snapshot);
    }
}
