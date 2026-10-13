using LlamaLauncher.Domain;

namespace LlamaLauncher.Application.Models;

/// <summary>GGUF 元数据读取结果。</summary>
public sealed class GgufReadResult
{
    public bool Success { get; private init; }

    /// <summary>文件是否为 GGUF 格式（魔数校验通过）。</summary>
    public bool IsGguf { get; private init; }

    public GgufModelInfo? Info { get; private init; }

    public string? Error { get; private init; }

    public static GgufReadResult Ok(GgufModelInfo info) => new() { Success = true, IsGguf = true, Info = info };

    public static GgufReadResult NotGguf(string error) => new() { Success = false, IsGguf = false, Error = error };

    public static GgufReadResult Failed(string error) => new() { Success = false, IsGguf = true, Error = error };
}

/// <summary>目录扫描进度。</summary>
public sealed record ModelScanProgress(int Discovered, int Examined, string? CurrentFile);

/// <summary>模型库操作失败（携带面向用户的消息）。</summary>
public sealed class ModelCatalogException : Exception
{
    public ModelCatalogException(string userMessage, Exception? inner = null)
        : base(userMessage, inner)
    {
    }
}
