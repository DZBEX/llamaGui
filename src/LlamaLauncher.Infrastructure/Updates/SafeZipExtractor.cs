using System.IO.Compression;
using LlamaLauncher.Application.Abstractions;

namespace LlamaLauncher.Infrastructure.Updates;

/// <summary>
/// ZIP 安全解压：
/// - 拒绝绝对路径、盘符路径、路径穿越（..）、空名称条目；
/// - 拒绝符号链接/重解析点（通过 ExternalAttributes 判定）；
/// - 限制总展开大小与单文件大小，拒绝可疑压缩比（压缩炸弹）；
/// - 每个条目解析后的完整路径必须仍位于目标目录内。
/// </summary>
public sealed class SafeZipExtractor : IZipExtractor
{
    private const long MaxTotalUncompressedBytes = 4L * 1024 * 1024 * 1024; // 4 GiB
    private const long MaxSingleEntryBytes = 2L * 1024 * 1024 * 1024;
    private const long SuspiciousRatioThreshold = 200;

    public async Task<ExtractResult> ExtractAsync(
        string zipPath,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        var extracted = new List<string>();
        var destinationFull = Path.GetFullPath(destinationDirectory);

        try
        {
            Directory.CreateDirectory(destinationFull);
            await using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            // 第一阶段：静态安全检查（写入任何文件之前全部验证）
            long totalUncompressed = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var error = ValidateEntry(entry, destinationFull, ref totalUncompressed);
                if (error is not null)
                {
                    TryCleanup(destinationFull);
                    return new ExtractResult(false, Array.Empty<string>(), error);
                }
            }

            // 第二阶段：解压
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(Path.GetFullPath(Path.Combine(destinationFull, entry.FullName)));
                    continue;
                }

                var targetPath = Path.GetFullPath(Path.Combine(destinationFull, entry.FullName));
                var directory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await using var entryStream = entry.Open();
                await using var target = new FileStream(
                    targetPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    FileOptions.Asynchronous);
                await entryStream.CopyToAsync(target, 128 * 1024, cancellationToken).ConfigureAwait(false);
                extracted.Add(targetPath);
            }

            return new ExtractResult(true, extracted, null);
        }
        catch (OperationCanceledException)
        {
            TryCleanup(destinationFull);
            throw;
        }
        catch (InvalidDataException ex)
        {
            TryCleanup(destinationFull);
            return new ExtractResult(false, Array.Empty<string>(), "压缩包无效或已损坏：" + ex.Message);
        }
        catch (IOException ex)
        {
            TryCleanup(destinationFull);
            return new ExtractResult(false, Array.Empty<string>(), "解压写入失败：" + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            TryCleanup(destinationFull);
            return new ExtractResult(false, Array.Empty<string>(), "解压失败（权限不足）：" + ex.Message);
        }
    }

    private static string? ValidateEntry(ZipArchiveEntry entry, string destinationFull, ref long totalUncompressed)
    {
        var name = entry.FullName;
        if (string.IsNullOrWhiteSpace(name))
        {
            return "压缩包包含空名称条目，已拒绝。";
        }

        if (Path.IsPathRooted(name)
            || name.StartsWith("\\\\", StringComparison.Ordinal)
            || (name.Length >= 2 && char.IsLetter(name[0]) && name[1] == ':'))
        {
            return $"压缩包包含绝对路径条目（{name}），已拒绝。";
        }

        var segments = name.Split('/', '\\');
        if (segments.Any(s => s == ".."))
        {
            return $"压缩包包含路径穿越条目（{name}），已拒绝。";
        }

        string targetPath;
        try
        {
            targetPath = Path.GetFullPath(Path.Combine(destinationFull, name));
        }
        catch (Exception)
        {
            return $"压缩包条目路径无效（{name}），已拒绝。";
        }

        var destinationWithSeparator = destinationFull.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!targetPath.StartsWith(destinationWithSeparator, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(targetPath, destinationFull, StringComparison.OrdinalIgnoreCase))
        {
            return $"压缩包条目解析后超出目标目录（{name}），已拒绝。";
        }

        if (IsSymlinkOrReparse(entry))
        {
            return $"压缩包包含符号链接/重解析点条目（{name}），出于安全考虑已拒绝。";
        }

        if (entry.Length > MaxSingleEntryBytes)
        {
            return $"压缩包条目过大（{name}，{entry.Length} 字节），已拒绝。";
        }

        if (entry.CompressedLength > 0
            && entry.Length > 64L * 1024 * 1024
            && entry.Length / Math.Max(entry.CompressedLength, 1) > SuspiciousRatioThreshold)
        {
            return $"压缩包条目压缩比异常（疑似压缩炸弹：{name}），已拒绝。";
        }

        totalUncompressed += entry.Length;
        if (totalUncompressed > MaxTotalUncompressedBytes)
        {
            return "压缩包总展开大小超出允许上限，已拒绝。";
        }

        return null;
    }

    private static bool IsSymlinkOrReparse(ZipArchiveEntry entry)
    {
        // Unix 模式位位于 ExternalAttributes 的高 16 位（S_IFLNK = 0xA000）；
        // Windows 文件属性位于低 16 位（FILE_ATTRIBUTE_REPARSE_POINT = 0x400）。
        var raw = unchecked((uint)entry.ExternalAttributes);
        var unixMode = raw >> 16;
        var windowsAttributes = raw & 0xFFFF;
        const uint fileTypeMask = 0xF000;
        const uint symlinkType = 0xA000;
        const uint reparsePoint = 0x400;
        return (unixMode & fileTypeMask) == symlinkType || (windowsAttributes & reparsePoint) != 0;
    }

    private static void TryCleanup(string destinationFull)
    {
        try
        {
            if (Directory.Exists(destinationFull))
            {
                Directory.Delete(destinationFull, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }
}
