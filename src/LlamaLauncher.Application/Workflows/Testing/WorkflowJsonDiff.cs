using System.Text.Json.Nodes;
using LlamaLauncher.Domain.Workflows;

namespace LlamaLauncher.Application.Workflows.Testing;

/// <summary>字段级差异类型。</summary>
public enum JsonDiffKind
{
    Added = 0,
    Removed = 1,
    Changed = 2,
}

/// <summary>单条字段级差异。</summary>
public sealed record JsonFieldChange(string Path, JsonDiffKind Kind, string? OldValue, string? NewValue);

/// <summary>两个运行结果的比较（输出差异 + 节点状态差异 + 耗时差异）。</summary>
public sealed record WorkflowRunComparison(
    string LeftRunId,
    string RightRunId,
    string LeftState,
    string RightState,
    string TimingSummary,
    IReadOnlyList<JsonFieldChange> OutputChanges,
    IReadOnlyList<string> NodeStateChanges);

/// <summary>
/// JSON 结构差异比较：递归对象字段与数组元素，输出新增/删除/变化字段（供回归测试与运行比较）。
/// </summary>
public static class WorkflowJsonDiff
{
    /// <summary>比较两个 JSON 值；忽略路径支持精确匹配或前缀匹配（a.b 会忽略 a.b 及其子路径）。</summary>
    public static IReadOnlyList<JsonFieldChange> Compare(
        JsonNode? left,
        JsonNode? right,
        IReadOnlyCollection<string>? ignorePaths = null)
    {
        var changes = new List<JsonFieldChange>();
        var ignores = (ignorePaths ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToList();
        Walk(left, right, string.Empty, ignores, changes);
        return changes;
    }

    private static void Walk(
        JsonNode? left,
        JsonNode? right,
        string path,
        List<string> ignores,
        List<JsonFieldChange> changes)
    {
        if (IsIgnored(path, ignores))
        {
            return;
        }

        if (left is JsonObject leftObject || right is JsonObject)
        {
            var leftObj = left as JsonObject ?? new JsonObject();
            var rightObj = right as JsonObject ?? new JsonObject();
            var keys = leftObj.Select(p => p.Key).Concat(rightObj.Select(p => p.Key)).Distinct(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var childPath = string.IsNullOrEmpty(path) ? key : path + "." + key;
                var hasLeft = leftObj.TryGetPropertyValue(key, out var leftChild);
                var hasRight = rightObj.TryGetPropertyValue(key, out var rightChild);
                if (hasLeft && !hasRight)
                {
                    if (!IsIgnored(childPath, ignores))
                    {
                        changes.Add(new JsonFieldChange(childPath, JsonDiffKind.Removed, WorkflowJson.Compact(leftChild), null));
                    }

                    continue;
                }

                if (!hasLeft && hasRight)
                {
                    if (!IsIgnored(childPath, ignores))
                    {
                        changes.Add(new JsonFieldChange(childPath, JsonDiffKind.Added, null, WorkflowJson.Compact(rightChild)));
                    }

                    continue;
                }

                Walk(leftChild, rightChild, childPath, ignores, changes);
            }

            return;
        }

        if (left is JsonArray leftArray || right is JsonArray)
        {
            var leftArr = left as JsonArray ?? new JsonArray();
            var rightArr = right as JsonArray ?? new JsonArray();
            var count = Math.Max(leftArr.Count, rightArr.Count);
            for (var i = 0; i < count; i++)
            {
                var childPath = $"{path}[{i}]";
                if (i >= leftArr.Count)
                {
                    if (!IsIgnored(childPath, ignores))
                    {
                        changes.Add(new JsonFieldChange(childPath, JsonDiffKind.Added, null, WorkflowJson.Compact(rightArr[i])));
                    }

                    continue;
                }

                if (i >= rightArr.Count)
                {
                    if (!IsIgnored(childPath, ignores))
                    {
                        changes.Add(new JsonFieldChange(childPath, JsonDiffKind.Removed, WorkflowJson.Compact(leftArr[i]), null));
                    }

                    continue;
                }

                Walk(leftArr[i], rightArr[i], childPath, ignores, changes);
            }

            return;
        }

        if (!JsonNode.DeepEquals(left, right) && !IsIgnored(path, ignores))
        {
            changes.Add(new JsonFieldChange(
                string.IsNullOrEmpty(path) ? "$" : path,
                JsonDiffKind.Changed,
                WorkflowJson.Compact(left),
                WorkflowJson.Compact(right)));
        }
    }

    private static bool IsIgnored(string path, List<string> ignores)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var ignore in ignores)
        {
            if (string.Equals(path, ignore, StringComparison.Ordinal)
                || path.StartsWith(ignore + ".", StringComparison.Ordinal)
                || path.StartsWith(ignore + "[", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>两次运行记录的比较（状态、输出字段差异与耗时对比）。</summary>
public static class WorkflowRunComparer
{
    /// <summary>比较两次运行（忽略字段用于排除随机/时间等内容）。</summary>
    public static WorkflowRunComparison Compare(
        WorkflowRunRecord left,
        WorkflowRunRecord right,
        IReadOnlyCollection<string>? ignorePaths = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var leftOutput = string.IsNullOrWhiteSpace(left.OutputJson) ? null : WorkflowJson.TryParse(left.OutputJson, out _);
        var rightOutput = string.IsNullOrWhiteSpace(right.OutputJson) ? null : WorkflowJson.TryParse(right.OutputJson, out _);
        var outputChanges = WorkflowJsonDiff.Compare(leftOutput, rightOutput, ignorePaths);

        var nodeStateChanges = new List<string>();
        var leftNodes = left.Nodes.ToDictionary(n => n.NodeId, StringComparer.Ordinal);
        foreach (var rightNode in right.Nodes)
        {
            if (leftNodes.TryGetValue(rightNode.NodeId, out var leftNode) && leftNode.State != rightNode.State)
            {
                nodeStateChanges.Add($"{rightNode.Name}：{leftNode.State} → {rightNode.State}");
            }
        }

        var timing = BuildTimingSummary(left, right);
        return new WorkflowRunComparison(
            left.RunId,
            right.RunId,
            left.State.ToString(),
            right.State.ToString(),
            timing,
            outputChanges,
            nodeStateChanges);
    }

    private static string BuildTimingSummary(WorkflowRunRecord left, WorkflowRunRecord right)
    {
        var parts = new List<string>
        {
            $"总耗时：{Format(left.DurationMs)} → {Format(right.DurationMs)}",
        };
        var leftNodes = left.Nodes.ToDictionary(n => n.NodeId, StringComparer.Ordinal);
        var changed = new List<string>();
        foreach (var rightNode in right.Nodes)
        {
            if (!leftNodes.TryGetValue(rightNode.NodeId, out var leftNode))
            {
                continue;
            }

            if (leftNode.DurationMs is { } a && rightNode.DurationMs is { } b && Math.Abs(a - b) > 1)
            {
                changed.Add($"{rightNode.Name} {Format(a)} → {Format(b)}");
            }
        }

        if (changed.Count > 0)
        {
            parts.Add("节点耗时变化：" + string.Join("；", changed.Take(6)));
        }

        return string.Join("。", parts) + "。";
    }

    private static string Format(double? ms)
        => ms is null ? "不可用" : $"{ms:0} ms";
}
