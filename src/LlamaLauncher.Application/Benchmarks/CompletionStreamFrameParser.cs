using System.Text.Json;

namespace LlamaLauncher.Application.Benchmarks;

/// <summary>llama-server /completion 返回的 timings 统计。</summary>
public sealed record CompletionTimings(
    int? PromptTokens,
    double? PromptMs,
    double? PromptTokensPerSecond,
    int? PredictedTokens,
    double? PredictedMs,
    double? PredictedTokensPerSecond);

/// <summary>解析后的单个流式数据帧。</summary>
public sealed class CompletionFrame
{
    /// <summary>增量内容（无内容为 null）。</summary>
    public string? Content { get; init; }

    /// <summary>是否为结束帧。</summary>
    public bool IsStop { get; init; }

    /// <summary>服务端统计（仅最终帧通常提供）。</summary>
    public CompletionTimings? Timings { get; init; }

    /// <summary>服务端错误信息。</summary>
    public string? Error { get; init; }

    /// <summary>是否包含非空内容。</summary>
    public bool HasContent => !string.IsNullOrEmpty(Content);
}

/// <summary>
/// /completion 流式数据帧解析器（纯逻辑）：
/// 支持 content/stop/timings/error 字段与 [DONE] 标记；无法识别的帧返回 null。
/// </summary>
public static class CompletionStreamFrameParser
{
    /// <summary>解析一个 SSE data 载荷；空载荷与无法解析的帧返回 null。</summary>
    public static CompletionFrame? Parse(string? payload)
    {
        var trimmed = payload?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed == "[DONE]")
        {
            return new CompletionFrame { IsStop = true };
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var errorElement))
            {
                return new CompletionFrame { Error = ExtractErrorMessage(errorElement) };
            }

            string? content = null;
            if (root.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.String)
            {
                content = contentElement.GetString();
            }

            var isStop = root.TryGetProperty("stop", out var stopElement)
                && stopElement.ValueKind == JsonValueKind.True;

            CompletionTimings? timings = null;
            if (root.TryGetProperty("timings", out var timingsElement)
                && timingsElement.ValueKind == JsonValueKind.Object)
            {
                timings = ParseTimings(timingsElement);
            }
            else
            {
                // 旧版本无 timings 对象时的顶层回退字段。
                var predicted = TryGetInt(root, "tokens_predicted");
                var evaluated = TryGetInt(root, "tokens_evaluated");
                if (predicted is not null || evaluated is not null)
                {
                    timings = new CompletionTimings(evaluated, null, null, predicted, null, null);
                }
            }

            return new CompletionFrame
            {
                Content = content,
                IsStop = isStop,
                Timings = timings,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CompletionTimings ParseTimings(JsonElement timings)
        => new(
            TryGetInt(timings, "prompt_n"),
            TryGetDouble(timings, "prompt_ms"),
            TryGetDouble(timings, "prompt_per_second"),
            TryGetInt(timings, "predicted_n"),
            TryGetDouble(timings, "predicted_ms"),
            TryGetDouble(timings, "predicted_per_second"));

    private static string ExtractErrorMessage(JsonElement errorElement)
    {
        if (errorElement.ValueKind == JsonValueKind.Object
            && errorElement.TryGetProperty("message", out var messageElement)
            && messageElement.ValueKind == JsonValueKind.String
            && messageElement.GetString() is { Length: > 0 } message)
        {
            return message;
        }

        return errorElement.ValueKind == JsonValueKind.String
            ? errorElement.GetString() ?? "服务返回错误。"
            : errorElement.ToString();
    }

    private static int? TryGetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var result)
                ? result
                : null;

    private static double? TryGetDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var result)
                ? result
                : null;
}
