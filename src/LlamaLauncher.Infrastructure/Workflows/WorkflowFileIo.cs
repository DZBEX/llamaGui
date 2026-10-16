using System.Text;

namespace LlamaLauncher.Infrastructure.Workflows;

/// <summary>工作流文件读写帮助：大小限制、安全 ID、原子写入（避免中途失败损坏原文件）。</summary>
internal static class WorkflowFileIo
{
    /// <summary>最大允许的文件字节数（超过即拒绝，防止异常超大文件）。</summary>
    public const int MaxFileBytes = Domain.Workflows.WorkflowLimits.MaxDefinitionFileBytes;

    /// <summary>校验 ID 是否只含安全字符（防路径穿越）。</summary>
    public static bool IsSafeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
        {
            return false;
        }

        foreach (var ch in id)
        {
            if (!(char.IsLetterOrDigit(ch) || ch is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>读取文本文件（带大小上限）；超限或不可读时抛出带说明的异常。</summary>
    public static async Task<string> ReadAllTextLimitedAsync(
        string path,
        int maxBytes = MaxFileBytes,
        CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("文件不存在。", path);
        }

        if (info.Length > maxBytes)
        {
            throw new InvalidOperationException(
                $"文件过大（{info.Length / 1024.0 / 1024.0:0.##} MB），超过上限 {maxBytes / 1024.0 / 1024.0:0.##} MB，已拒绝读取。");
        }

        return await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>原子写入文本：先写临时文件再替换目标，避免中途失败损坏原文件。</summary>
    public static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>尝试删除文件（清理临时文件用，不抛出）。</summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响主流程。
        }
    }
}
