namespace LlamaLauncher.Domain;

/// <summary>从 GGUF 文件头/元数据区解析出的基础信息。未提供的字段保持 null，不做猜测。</summary>
public sealed class GgufModelInfo
{
    public int? GgufVersion { get; set; }

    public long? TensorCount { get; set; }

    public long? MetadataKeyCount { get; set; }

    /// <summary>general.name。</summary>
    public string? Name { get; set; }

    /// <summary>general.architecture（如 llama、qwen2 等）。</summary>
    public string? Architecture { get; set; }

    /// <summary>general.parameter_count（若元数据提供）。</summary>
    public long? ParameterCount { get; set; }

    /// <summary>general.file_type 原始值。</summary>
    public int? FileType { get; set; }

    /// <summary>general.file_type 对应的量化标签（未知值保留原始数字描述）。</summary>
    public string? QuantizationLabel { get; set; }

    /// <summary>{architecture}.context_length。</summary>
    public long? ContextLength { get; set; }

    /// <summary>{architecture}.embedding_length。</summary>
    public long? EmbeddingLength { get; set; }

    /// <summary>{architecture}.block_count。</summary>
    public long? BlockCount { get; set; }

    /// <summary>{tokenizer}.ggml.vocab_size 或 tokenizer.ggml.tokens 数量。</summary>
    public long? VocabularySize { get; set; }

    /// <summary>tokenizer.ggml.model。</summary>
    public string? TokenizerModel { get; set; }

    /// <summary>解析时间（UTC）。</summary>
    public DateTimeOffset ParsedUtc { get; set; }

    /// <summary>少量额外元数据的字符串化视图（用于详情展示）。</summary>
    public Dictionary<string, string> AdditionalMetadata { get; set; } = new(StringComparer.Ordinal);
}
