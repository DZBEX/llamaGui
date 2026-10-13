using System.Text.Json.Serialization;

namespace LlamaLauncher.Contracts;

/// <summary>OpenAI 兼容聊天请求消息。</summary>
public sealed class ChatMessageDto
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

/// <summary>OpenAI 兼容聊天完成请求（llama-server /v1/chat/completions）。</summary>
public sealed class ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "default";

    [JsonPropertyName("messages")]
    public List<ChatMessageDto> Messages { get; set; } = new();

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = true;

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TopP { get; set; }

    [JsonPropertyName("top_k")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TopK { get; set; }

    [JsonPropertyName("min_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MinP { get; set; }

    [JsonPropertyName("repeat_penalty")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RepeatPenalty { get; set; }

    [JsonPropertyName("seed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Seed { get; set; }

    [JsonPropertyName("max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("stop")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Stop { get; set; }
}

/// <summary>流式分块 delta。</summary>
public sealed class ChatCompletionDelta
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

/// <summary>流式分块选项。</summary>
public sealed class ChatCompletionChunkChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("delta")]
    public ChatCompletionDelta? Delta { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

/// <summary>用量统计（服务器返回时使用，未返回则为 null）。</summary>
public sealed class ChatCompletionUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int? PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int? CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int? TotalTokens { get; set; }
}

/// <summary>流式分块。</summary>
public sealed class ChatCompletionChunk
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("choices")]
    public List<ChatCompletionChunkChoice>? Choices { get; set; }

    [JsonPropertyName("usage")]
    public ChatCompletionUsage? Usage { get; set; }
}

/// <summary>非流式响应。</summary>
public sealed class ChatCompletionResponse
{
    [JsonPropertyName("choices")]
    public List<ChatCompletionResponseChoice>? Choices { get; set; }

    [JsonPropertyName("usage")]
    public ChatCompletionUsage? Usage { get; set; }
}

/// <summary>非流式响应选项。</summary>
public sealed class ChatCompletionResponseChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public ChatMessageDto? Message { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

/// <summary>服务端错误响应。</summary>
public sealed class ApiErrorResponse
{
    [JsonPropertyName("error")]
    public ApiErrorBody? Error { get; set; }
}

/// <summary>错误体（不同版本可能是对象或纯字符串）。</summary>
public sealed class ApiErrorBody
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("code")]
    public int? Code { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}
