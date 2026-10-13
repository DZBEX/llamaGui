using LlamaLauncher.Infrastructure.Gguf;
using LlamaLauncher.IntegrationTests.TestSupport;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Gguf;

public sealed class GgufMetadataReaderTests
{
    private static GgufMetadataReader CreateReader() => new(new FakeTimeProvider());

    private static byte[] CreateTypicalModelBytes() => new GgufBuilder()
        .AddString("general.name", "测试模型 Q4")
        .AddString("general.architecture", "llama")
        .AddUInt64("general.parameter_count", 8030261248UL)
        .AddUInt32("general.file_type", 12)
        .AddUInt32("llama.context_length", 8192)
        .AddUInt32("llama.embedding_length", 4096)
        .AddUInt32("llama.block_count", 32)
        .AddString("tokenizer.ggml.model", "gpt2")
        .AddStringArray("tokenizer.ggml.tokens", "a", "b", "c", "d")
        .Build();

    [Fact]
    public async Task ParsesTypicalMetadata()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("model.gguf");
        await File.WriteAllBytesAsync(path, CreateTypicalModelBytes());

        var result = await CreateReader().ReadAsync(path);

        Assert.True(result.Success, result.Error);
        var info = result.Info!;
        Assert.Equal(3, info.GgufVersion);
        Assert.Equal("测试模型 Q4", info.Name);
        Assert.Equal("llama", info.Architecture);
        Assert.Equal(8030261248L, info.ParameterCount);
        Assert.Equal(12, info.FileType);
        Assert.Equal("Q4_K_M", info.QuantizationLabel);
        Assert.Equal(8192, info.ContextLength);
        Assert.Equal(4096, info.EmbeddingLength);
        Assert.Equal(32, info.BlockCount);
        Assert.Equal("gpt2", info.TokenizerModel);
        Assert.Equal(4, info.VocabularySize);
    }

    [Fact]
    public async Task UnknownFileType_KeepsRawNumber()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("model.gguf");
        await File.WriteAllBytesAsync(path, new GgufBuilder().AddUInt32("general.file_type", 999).Build());

        var result = await CreateReader().ReadAsync(path);

        Assert.True(result.Success);
        Assert.Contains("999", result.Info!.QuantizationLabel);
    }

    [Fact]
    public async Task SupportsVersion1HeaderLayout()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("v1.gguf");
        await File.WriteAllBytesAsync(
            path,
            new GgufBuilder().WithVersion(1).AddString("general.name", "v1 模型").Build());

        var result = await CreateReader().ReadAsync(path);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.Info!.GgufVersion);
        Assert.Equal("v1 模型", result.Info.Name);
    }

    [Fact]
    public async Task RejectsNonGgufFile()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("not-model.gguf");
        await File.WriteAllBytesAsync(path, "NOTGGUF..."u8.ToArray());

        var result = await CreateReader().ReadAsync(path);

        Assert.False(result.Success);
        Assert.False(result.IsGguf);
        Assert.Contains("GGUF", result.Error);
    }

    [Fact]
    public async Task TruncatedFile_FailsWithClearMessage()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("truncated.gguf");
        var full = new GgufBuilder()
            .AddString("general.name", "将被截断的模型")
            .AddString("general.architecture", "llama")
            .Build();
        await File.WriteAllBytesAsync(path, full.Take(full.Length - 5).ToArray());

        var result = await CreateReader().ReadAsync(path);

        Assert.False(result.Success);
        Assert.Contains("截断", result.Error);
    }

    [Fact]
    public async Task SkipsLargeArraysAndStringsWithoutLoadingThem()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("big.gguf");
        var builder = new GgufBuilder().AddString("general.name", "大数组模型");
        builder.AddUInt32Array("some.large.array", Enumerable.Range(0, 100_000).Select(i => (uint)i).ToArray());
        builder.AddString("tokenizer.chat_template", new string('x', 200_000));
        await File.WriteAllBytesAsync(path, builder.Build());

        var result = await CreateReader().ReadAsync(path);

        Assert.True(result.Success, result.Error);
        Assert.Equal("大数组模型", result.Info!.Name);
        Assert.DoesNotContain("tokenizer.chat_template", result.Info.AdditionalMetadata.Keys);
    }

    [Fact]
    public async Task Cancellation_IsHonored()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("model.gguf");
        await File.WriteAllBytesAsync(path, CreateTypicalModelBytes());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateReader().ReadAsync(path, cts.Token));
    }
}
