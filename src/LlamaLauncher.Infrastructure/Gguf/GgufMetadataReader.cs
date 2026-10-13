using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Models;
using LlamaLauncher.Domain;

namespace LlamaLauncher.Infrastructure.Gguf;

/// <summary>
/// GGUF 元数据读取器：只读取文件头与元数据区，绝不把模型文件整体读入内存。
/// 大字符串（如 chat_template）与数组内容直接跳过（数组仅在需要时逐元素跳过，用于统计 tokenizer 词表大小）。
/// </summary>
public sealed class GgufMetadataReader : IGgufMetadataReader
{
    private const ulong MaxSingleStringBytes = 64UL * 1024 * 1024;
    private const int MaxCapturedScalars = 4000;
    private const int MaxCapturedStringChars = 4096;

    private readonly TimeProvider _timeProvider;

    public GgufMetadataReader(TimeProvider timeProvider) => _timeProvider = timeProvider;

    public async Task<GgufReadResult> ReadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var reader = new AsyncLittleEndianReader(stream);
            var magic = await reader.ReadBytesAsync(4, cancellationToken).ConfigureAwait(false);
            if (magic.Length < 4 || magic[0] != 'G' || magic[1] != 'G' || magic[2] != 'U' || magic[3] != 'F')
            {
                return GgufReadResult.NotGguf("文件头不是 GGUF 魔数（可能不是 GGUF 模型，或文件已损坏）。");
            }

            var version = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
            if (version is < 1 or > 3)
            {
                return GgufReadResult.Failed($"不支持的 GGUF 版本 {version}（当前支持 1-3）。");
            }

            ulong tensorCount;
            ulong kvCount;
            if (version == 1)
            {
                tensorCount = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
                kvCount = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                tensorCount = await reader.ReadUInt64Async(cancellationToken).ConfigureAwait(false);
                kvCount = await reader.ReadUInt64Async(cancellationToken).ConfigureAwait(false);
            }

            if (kvCount > 10_000_000)
            {
                return GgufReadResult.Failed($"元数据键值对数量异常（{kvCount}），文件可能已损坏。");
            }

            var scalars = new Dictionary<string, object>(StringComparer.Ordinal);
            long? tokensArrayCount = null;

            for (ulong i = 0; i < kvCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var key = await reader.ReadStringAsync(maxCaptureChars: 512, cancellationToken).ConfigureAwait(false);
                var valueType = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);

                if (valueType == 9)
                {
                    // 数组：读取元素类型与数量后跳过内容（大数组逐元素跳过，不存储）。
                    var elementType = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
                    var count = await reader.ReadUInt64Async(cancellationToken).ConfigureAwait(false);
                    if (elementType == 9)
                    {
                        return GgufReadResult.Failed("元数据中不支持嵌套数组。");
                    }

                    if (string.Equals(key, "tokenizer.ggml.tokens", StringComparison.Ordinal))
                    {
                        tokensArrayCount = (long)Math.Min(count, long.MaxValue);
                    }

                    await SkipArrayAsync(reader, elementType, count, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var isKnownScalar = IsCapturedScalarKey(key);
                if (isKnownScalar || scalars.Count < MaxCapturedScalars)
                {
                    var value = await reader.ReadScalarAsync(valueType, maxStringChars: MaxCapturedStringChars, cancellationToken)
                        .ConfigureAwait(false);
                    if (value is not null)
                    {
                        scalars[key] = value;
                    }
                }
                else
                {
                    await SkipScalarAsync(reader, valueType, cancellationToken).ConfigureAwait(false);
                }
            }

            var info = BuildInfo(version, tensorCount, kvCount, scalars, tokensArrayCount);
            return GgufReadResult.Ok(info);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EndOfStreamException)
        {
            return GgufReadResult.Failed("元数据在文件结束前被截断（文件可能不完整）。");
        }
        catch (IOException ex)
        {
            return GgufReadResult.Failed($"读取文件失败：{ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return GgufReadResult.Failed($"无权限读取文件：{ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            return GgufReadResult.Failed($"元数据格式无效：{ex.Message}");
        }
    }

    private GgufModelInfo BuildInfo(
        uint version,
        ulong tensorCount,
        ulong kvCount,
        Dictionary<string, object> scalars,
        long? tokensArrayCount)
    {
        var info = new GgufModelInfo
        {
            GgufVersion = (int)version,
            TensorCount = (long)Math.Min(tensorCount, long.MaxValue),
            MetadataKeyCount = (long)Math.Min(kvCount, long.MaxValue),
            ParsedUtc = _timeProvider.GetUtcNow(),
        };

        info.Name = GetString(scalars, "general.name");
        info.Architecture = GetString(scalars, "general.architecture");
        info.ParameterCount = GetLong(scalars, "general.parameter_count");
        info.TokenizerModel = GetString(scalars, "tokenizer.ggml.model");

        if (GetLong(scalars, "general.file_type") is { } fileType)
        {
            var clamped = (int)Math.Clamp(fileType, int.MinValue, int.MaxValue);
            info.FileType = clamped;
            info.QuantizationLabel = FileTypeLabels.TryGetValue(clamped, out var label)
                ? label
                : $"未知文件类型（file_type={clamped}）";
        }

        var architecture = info.Architecture;
        if (!string.IsNullOrEmpty(architecture))
        {
            info.ContextLength = GetLong(scalars, $"{architecture}.context_length");
            info.EmbeddingLength = GetLong(scalars, $"{architecture}.embedding_length");
            info.BlockCount = GetLong(scalars, $"{architecture}.block_count");
        }

        info.VocabularySize = GetLong(scalars, "tokenizer.ggml.vocab_size") ?? tokensArrayCount;

        foreach (var (key, value) in scalars)
        {
            if (key.StartsWith("general.", StringComparison.Ordinal)
                && key is not ("general.name" or "general.architecture" or "general.parameter_count" or "general.file_type"))
            {
                info.AdditionalMetadata[key] = FormatValue(value);
                if (info.AdditionalMetadata.Count >= 30)
                {
                    break;
                }
            }
        }

        return info;
    }

    private static bool IsCapturedScalarKey(string key) =>
        key is "general.name"
            or "general.architecture"
            or "general.parameter_count"
            or "general.file_type"
            or "tokenizer.ggml.model"
            or "tokenizer.ggml.vocab_size"
        || key.EndsWith(".context_length", StringComparison.Ordinal)
        || key.EndsWith(".embedding_length", StringComparison.Ordinal)
        || key.EndsWith(".block_count", StringComparison.Ordinal)
        || key.StartsWith("general.", StringComparison.Ordinal);

    private static string? GetString(Dictionary<string, object> scalars, string key)
        => scalars.TryGetValue(key, out var value) ? value as string : null;

    private static long? GetLong(Dictionary<string, object> scalars, string key)
    {
        if (!scalars.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            byte v => v,
            sbyte v => v,
            ushort v => v,
            short v => v,
            uint v => v,
            int v => v,
            ulong v => (long)Math.Min(v, long.MaxValue),
            long v => v,
            float v => (long)v,
            double v => (long)v,
            _ => null,
        };
    }

    private static string FormatValue(object value) => value switch
    {
        float v => v.ToString("G6", CultureInfo.InvariantCulture),
        double v => v.ToString("G9", CultureInfo.InvariantCulture),
        bool v => v ? "true" : "false",
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>general.file_type → 量化标签（llama.cpp LLAMA_FTYPE_* 常见取值，未知值保留原始数字）。</summary>
    private static readonly Dictionary<int, string> FileTypeLabels = new()
    {
        [0] = "F32",
        [1] = "F16",
        [2] = "Q4_0",
        [3] = "Q4_1",
        [4] = "Q5_0",
        [5] = "Q5_1",
        [6] = "Q8_0",
        [7] = "Q2_K",
        [8] = "Q3_K_S",
        [9] = "Q3_K_M",
        [10] = "Q3_K_L",
        [11] = "Q4_K_S",
        [12] = "Q4_K_M",
        [13] = "Q5_K_S",
        [14] = "Q5_K_M",
        [15] = "Q6_K",
        [16] = "IQ2_XXS",
        [17] = "IQ2_XS",
        [18] = "Q2_K_S",
        [19] = "IQ3_XS",
        [20] = "IQ3_XXS",
        [21] = "IQ1_S",
        [22] = "IQ4_NL",
        [23] = "IQ3_S",
        [24] = "IQ3_M",
        [25] = "IQ2_S",
        [26] = "IQ2_M",
        [27] = "IQ4_XS",
        [28] = "IQ1_M",
        [29] = "BF16",
        [36] = "TQ1_0",
        [37] = "TQ2_0",
    };

    private static async Task SkipArrayAsync(
        AsyncLittleEndianReader reader,
        uint elementType,
        ulong count,
        CancellationToken cancellationToken)
    {
        switch (elementType)
        {
            case 8: // 字符串数组：逐元素跳过（只读长度，不读内容）
                for (ulong i = 0; i < count; i++)
                {
                    var length = await reader.ReadUInt64Async(cancellationToken).ConfigureAwait(false);
                    await reader.SkipAsync(length, cancellationToken).ConfigureAwait(false);
                }

                break;
            default:
                var size = FixedTypeSize(elementType);
                await reader.SkipAsync(size * count, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private static Task SkipScalarAsync(AsyncLittleEndianReader reader, uint valueType, CancellationToken cancellationToken)
    {
        if (valueType == 8)
        {
            return SkipStringAsync(reader, cancellationToken);
        }

        return reader.SkipAsync(FixedTypeSize(valueType), cancellationToken);
    }

    private static async Task SkipStringAsync(AsyncLittleEndianReader reader, CancellationToken cancellationToken)
    {
        var length = await reader.ReadUInt64Async(cancellationToken).ConfigureAwait(false);
        await reader.SkipAsync(length, cancellationToken).ConfigureAwait(false);
    }

    private static ulong FixedTypeSize(uint valueType) => valueType switch
    {
        0 => 1,  // uint8
        1 => 1,  // int8
        2 => 2,  // uint16
        3 => 2,  // int16
        4 => 4,  // uint32
        5 => 4,  // int32
        6 => 4,  // float32
        7 => 1,  // bool
        10 => 8, // uint64
        11 => 8, // int64
        12 => 8, // float64
        _ => throw new InvalidDataException($"未知的 GGUF 值类型 {valueType}。"),
    };

    /// <summary>小端异步读取器（只按需读取字段，不在内存中缓存大块数据）。</summary>
    private sealed class AsyncLittleEndianReader
    {
        private readonly Stream _stream;
        private readonly byte[] _scratch = new byte[8];

        public AsyncLittleEndianReader(Stream stream) => _stream = stream;

        public async Task<byte[]> ReadBytesAsync(int count, CancellationToken cancellationToken)
        {
            var buffer = new byte[count];
            await _stream.ReadExactlyAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            return buffer;
        }

        public async Task<uint> ReadUInt32Async(CancellationToken cancellationToken)
        {
            await _stream.ReadExactlyAsync(_scratch.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
            return BinaryPrimitives.ReadUInt32LittleEndian(_scratch);
        }

        public async Task<ulong> ReadUInt64Async(CancellationToken cancellationToken)
        {
            await _stream.ReadExactlyAsync(_scratch.AsMemory(0, 8), cancellationToken).ConfigureAwait(false);
            return BinaryPrimitives.ReadUInt64LittleEndian(_scratch);
        }

        public async Task<string> ReadStringAsync(int maxCaptureChars, CancellationToken cancellationToken)
        {
            var length = await ReadUInt64Async(cancellationToken).ConfigureAwait(false);
            if (length > MaxSingleStringBytes)
            {
                throw new InvalidDataException($"字符串长度异常（{length} 字节）。");
            }

            if (length > (ulong)maxCaptureChars * 4)
            {
                await SkipAsync(length, cancellationToken).ConfigureAwait(false);
                return "<过长字符串已跳过>";
            }

            var bytes = new byte[length];
            await _stream.ReadExactlyAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
            return Encoding.UTF8.GetString(bytes);
        }

        public async Task<object?> ReadScalarAsync(uint valueType, int maxStringChars, CancellationToken cancellationToken)
        {
            switch (valueType)
            {
                case 0:
                    return await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                case 1:
                    return (sbyte)await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                case 2:
                    return (ushort)await ReadUInt32Async(cancellationToken).ConfigureAwait(false);
                case 3:
                    return (short)(ushort)await ReadUInt32Async(cancellationToken).ConfigureAwait(false);
                case 4:
                    return await ReadUInt32Async(cancellationToken).ConfigureAwait(false);
                case 5:
                    return (int)await ReadUInt32Async(cancellationToken).ConfigureAwait(false);
                case 6:
                    return BitConverter.UInt32BitsToSingle(await ReadUInt32Async(cancellationToken).ConfigureAwait(false));
                case 7:
                    return await ReadByteAsync(cancellationToken).ConfigureAwait(false) != 0;
                case 8:
                    var text = await ReadStringAsync(maxStringChars, cancellationToken).ConfigureAwait(false);
                    return text;
                case 10:
                    return await ReadUInt64Async(cancellationToken).ConfigureAwait(false);
                case 11:
                    return (long)await ReadUInt64Async(cancellationToken).ConfigureAwait(false);
                case 12:
                    return BitConverter.UInt64BitsToDouble(await ReadUInt64Async(cancellationToken).ConfigureAwait(false));
                default:
                    throw new InvalidDataException($"未知的 GGUF 值类型 {valueType}。");
            }
        }

        public async Task SkipAsync(ulong count, CancellationToken cancellationToken)
        {
            if (count == 0)
            {
                return;
            }

            var remaining = _stream.Length - _stream.Position;
            if (count > (ulong)remaining)
            {
                throw new EndOfStreamException();
            }

            _stream.Position += (long)count;
            await Task.CompletedTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        private async Task<byte> ReadByteAsync(CancellationToken cancellationToken)
        {
            await _stream.ReadExactlyAsync(_scratch.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            return _scratch[0];
        }
    }
}
