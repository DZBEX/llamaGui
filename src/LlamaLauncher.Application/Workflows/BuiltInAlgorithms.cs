using System.Text.Json.Nodes;
using LlamaLauncher.Domain.Workflows;

namespace LlamaLauncher.Application.Workflows;

/// <summary>
/// 内置示例求解器：加权排序 Top-K。
/// 输入：{ "items": [{ "key": "a", "score": 1.2 }], "topK": 3 }
/// 输出：{ "ranking": [{ "key", "score", "rank" }], "total": N }
/// 注意：这是可测试的示例算法，用于演示算法节点与扩展点，不是生产求解器。
/// </summary>
public sealed class SampleRankingAlgorithm : IWorkflowAlgorithm
{
    public string Key => "sample.ranking";

    public string DisplayName => "示例：加权排序 Top-K";

    public string Description => "内置示例求解器（非生产求解器）：对 items 按 score 降序取前 topK。";

    public Task<AlgorithmInvocationResult> InvokeAsync(JsonNode? input, CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        if (input is not JsonObject root || root["items"] is not JsonArray items)
        {
            return Task.FromResult(new AlgorithmInvocationResult(
                AlgorithmStatus.InvalidInput,
                null,
                "输入必须是包含 items 数组的对象。",
                started.Elapsed));
        }

        if (items.Count > WorkflowLimits.MaxCollectionElements)
        {
            return Task.FromResult(new AlgorithmInvocationResult(
                AlgorithmStatus.InvalidInput,
                null,
                $"items 数量（{items.Count}）超过上限 {WorkflowLimits.MaxCollectionElements}。",
                started.Elapsed));
        }

        var topK = root["topK"]?.GetValue<int?>() ?? items.Count;
        if (topK < 0)
        {
            return Task.FromResult(new AlgorithmInvocationResult(
                AlgorithmStatus.InvalidInput,
                null,
                "topK 不能为负数。",
                started.Elapsed));
        }

        var entries = new List<(string Key, double Score)>();
        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (items[i] is not JsonObject item)
            {
                return Task.FromResult(new AlgorithmInvocationResult(
                    AlgorithmStatus.InvalidInput,
                    null,
                    $"items[{i}] 必须是对象。",
                    started.Elapsed));
            }

            var key = item["key"]?.GetValue<string>();
            var score = item["score"]?.GetValue<double?>();
            if (string.IsNullOrWhiteSpace(key) || score is null)
            {
                return Task.FromResult(new AlgorithmInvocationResult(
                    AlgorithmStatus.InvalidInput,
                    null,
                    $"items[{i}] 缺少 key 或 score。",
                    started.Elapsed));
            }

            entries.Add((key, score.Value));
        }

        if (entries.Count == 0)
        {
            return Task.FromResult(new AlgorithmInvocationResult(
                AlgorithmStatus.NoSolution,
                new JsonObject { ["ranking"] = new JsonArray(), ["total"] = 0 },
                "输入为空，无可行解。",
                started.Elapsed));
        }

        var ranking = new JsonArray();
        var rank = 1;
        foreach (var entry in entries.OrderByDescending(e => e.Score).ThenBy(e => e.Key, StringComparer.Ordinal).Take(Math.Max(0, topK)))
        {
            ranking.Add(new JsonObject
            {
                ["key"] = entry.Key,
                ["score"] = entry.Score,
                ["rank"] = rank++,
            });
        }

        return Task.FromResult(new AlgorithmInvocationResult(
            AlgorithmStatus.Success,
            new JsonObject { ["ranking"] = ranking, ["total"] = entries.Count },
            null,
            started.Elapsed));
    }
}

/// <summary>默认算法注册表（可扩展：注册自定义 IWorkflowAlgorithm 实现）。</summary>
public sealed class WorkflowAlgorithmRegistry : IWorkflowAlgorithmRegistry
{
    private readonly List<IWorkflowAlgorithm> _algorithms;

    public WorkflowAlgorithmRegistry(IEnumerable<IWorkflowAlgorithm> algorithms)
        => _algorithms = algorithms.ToList();

    public IReadOnlyList<IWorkflowAlgorithm> List()
        => _algorithms.OrderBy(a => a.Key, StringComparer.Ordinal).ToList();

    public IWorkflowAlgorithm? Find(string? key)
        => string.IsNullOrWhiteSpace(key)
            ? null
            : _algorithms.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));
}
