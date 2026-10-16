using System.Text.Json.Nodes;

namespace LlamaLauncher.Application.Workflows;

/// <summary>受限 JSON 字段路径工具（点路径 + [n] 索引）；用于数据处理节点的目标字段读写。</summary>
internal static class JsonPathTools
{
    /// <summary>路径段：字段名或数组索引（二者其一）。</summary>
    public readonly record struct PathStep(string? Name, int? Index);

    /// <summary>路径是否合法。</summary>
    public static bool IsValid(string? path) => TryParse(path, out _, out _);

    /// <summary>按路径取值；不存在返回 null。</summary>
    public static JsonNode? Get(JsonNode? root, string path)
    {
        if (!TryParse(path, out var steps, out _))
        {
            return null;
        }

        var current = root;
        foreach (var step in steps)
        {
            if (current is null)
            {
                return null;
            }

            if (step.Name is { } name)
            {
                if (current is not JsonObject obj || !obj.TryGetPropertyValue(name, out current))
                {
                    return null;
                }
            }
            else if (step.Index is { } index)
            {
                if (current is not JsonArray array || index < 0 || index >= array.Count)
                {
                    return null;
                }

                current = array[index];
            }
        }

        return current;
    }

    /// <summary>写入路径（自动创建中间对象/数组）；失败返回 false 并给出原因。</summary>
    public static bool TrySet(JsonNode? root, string path, JsonNode? value, out string? error)
    {
        error = null;
        if (!TryParse(path, out var steps, out var parseError))
        {
            error = parseError;
            return false;
        }

        if (root is null || steps.Count == 0)
        {
            error = "目标文档为空或路径为空。";
            return false;
        }

        JsonNode current = root;
        for (var i = 0; i < steps.Count - 1; i++)
        {
            var step = steps[i];
            var next = steps[i + 1];
            if (step.Name is { } name)
            {
                if (current is not JsonObject obj)
                {
                    error = $"路径“{path}”的第 {i + 1} 段“{name}”所在值不是对象。";
                    return false;
                }

                if (obj[name] is null)
                {
                    obj[name] = next.Name is not null ? new JsonObject() : new JsonArray();
                }

                current = obj[name]!;
            }
            else if (step.Index is { } index)
            {
                if (current is not JsonArray array)
                {
                    error = $"路径“{path}”的第 {i + 1} 段索引所在值不是数组。";
                    return false;
                }

                if (index < 0 || index > array.Count)
                {
                    error = $"路径“{path}”的索引 [{index}] 越界。";
                    return false;
                }

                if (index == array.Count)
                {
                    array.Add(next.Name is not null ? new JsonObject() : new JsonArray());
                }

                if (array[index] is null)
                {
                    array[index] = next.Name is not null ? new JsonObject() : new JsonArray();
                }

                current = array[index]!;
            }
        }

        var last = steps[^1];
        if (last.Name is { } lastName)
        {
            if (current is not JsonObject obj)
            {
                error = $"路径“{path}”的父级不是对象。";
                return false;
            }

            obj[lastName] = value?.DeepClone();
            return true;
        }

        if (last.Index is { } lastIndex)
        {
            if (current is not JsonArray array)
            {
                error = $"路径“{path}”的父级不是数组。";
                return false;
            }

            if (lastIndex == array.Count)
            {
                array.Add(value?.DeepClone());
                return true;
            }

            if (lastIndex < 0 || lastIndex > array.Count)
            {
                error = $"路径“{path}”的索引 [{lastIndex}] 越界。";
                return false;
            }

            array[lastIndex] = value?.DeepClone();
            return true;
        }

        error = "路径无效。";
        return false;
    }

    /// <summary>删除路径上的字段（不存在时返回 false，不视为错误）。</summary>
    public static bool Remove(JsonNode? root, string path)
    {
        if (!TryParse(path, out var steps, out _) || steps.Count == 0)
        {
            return false;
        }

        JsonNode? parent = root;
        for (var i = 0; i < steps.Count - 1; i++)
        {
            if (parent is null)
            {
                return false;
            }

            if (steps[i].Name is { } name)
            {
                if (parent is not JsonObject obj || !obj.TryGetPropertyValue(name, out parent))
                {
                    return false;
                }
            }
            else if (steps[i].Index is { } index)
            {
                if (parent is not JsonArray array || index < 0 || index >= array.Count)
                {
                    return false;
                }

                parent = array[index];
            }
        }

        var last = steps[^1];
        if (last.Name is { } lastName && parent is JsonObject lastObj)
        {
            return lastObj.Remove(lastName);
        }

        if (last.Index is { } lastIndex && parent is JsonArray lastArray && lastIndex >= 0 && lastIndex < lastArray.Count)
        {
            lastArray.RemoveAt(lastIndex);
            return true;
        }

        return false;
    }

    /// <summary>解析路径（a.b[0].c）。</summary>
    public static bool TryParse(string? path, out List<PathStep> steps, out string? error)
    {
        steps = new List<PathStep>();
        error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "路径不能为空。";
            return false;
        }

        var i = 0;
        var expectName = true;
        while (i < path.Length)
        {
            var ch = path[i];
            if (ch == '.')
            {
                i++;
                expectName = true;
                continue;
            }

            if (ch == '[')
            {
                if (expectName)
                {
                    error = $"路径“{path}”在位置 {i + 1} 处缺少字段名。";
                    return false;
                }

                var close = path.IndexOf(']', i);
                if (close < 0)
                {
                    error = $"路径“{path}”的 [ 缺少对应的 ]。";
                    return false;
                }

                var indexText = path[(i + 1)..close];
                if (!int.TryParse(indexText, out var index) || index < 0)
                {
                    error = $"路径“{path}”中的索引“{indexText}”无效。";
                    return false;
                }

                steps.Add(new PathStep(null, index));
                i = close + 1;
                continue;
            }

            var start = i;
            while (i < path.Length && path[i] is not ('.' or '['))
            {
                i++;
            }

            var name = path[start..i];
            if (name.Length == 0)
            {
                error = $"路径“{path}”包含空字段名。";
                return false;
            }

            steps.Add(new PathStep(name, null));
            expectName = false;
        }

        if (steps.Count == 0)
        {
            error = "路径不能为空。";
            return false;
        }

        return true;
    }
}
