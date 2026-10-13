using LlamaLauncher.Application.Models;
using LlamaLauncher.Application.Services;
using LlamaLauncher.Domain;
using LlamaLauncher.IntegrationTests.TestSupport;
using LlamaLauncher.Infrastructure.Gguf;
using LlamaLauncher.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Application;

public sealed class ModelCatalogServiceTests
{
    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Directory = new TempDirectory();
            var database = TestDatabase.CreateAsync(Directory).GetAwaiter().GetResult();
            Repository = new SqliteModelRepository(database);
            TimeProvider = new FakeTimeProvider();
            Catalog = new ModelCatalogService(
                Repository,
                new GgufMetadataReader(TimeProvider),
                TimeProvider,
                NullLogger<ModelCatalogService>.Instance);
        }

        public TempDirectory Directory { get; }

        public SqliteModelRepository Repository { get; }

        public FakeTimeProvider TimeProvider { get; }

        public ModelCatalogService Catalog { get; }

        public string CreateModelFile(string name, string displayName = "模型")
        {
            var path = Directory.Combine(name);
            var bytes = new GgufBuilder()
                .AddString("general.name", displayName)
                .AddString("general.architecture", "llama")
                .AddUInt32("general.file_type", 15)
                .Build();
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose() => Directory.Dispose();
    }

    [Fact]
    public async Task AddAsync_RegistersWithMetadata()
    {
        using var fixture = new Fixture();
        var path = fixture.CreateModelFile("模型 甲.gguf", "模型甲");

        var model = await fixture.Catalog.AddAsync(path);

        Assert.Equal("模型甲", model.Name);
        Assert.Equal(ModelFileStatus.Ok, model.Status);
        Assert.Equal(MetadataParseStatus.Parsed, model.MetadataStatus);
        Assert.Equal("Q6_K", model.Metadata!.QuantizationLabel);

        var reloaded = await fixture.Catalog.GetAsync(model.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(path, reloaded!.FilePath);
    }

    [Fact]
    public async Task AddAsync_DuplicatePath_Throws()
    {
        using var fixture = new Fixture();
        var path = fixture.CreateModelFile("dup.gguf");
        await fixture.Catalog.AddAsync(path);

        var exception = await Assert.ThrowsAsync<ModelCatalogException>(() => fixture.Catalog.AddAsync(path));
        Assert.Contains("已登记", exception.Message);
    }

    [Fact]
    public async Task AddAsync_WrongExtension_Throws()
    {
        using var fixture = new Fixture();
        var path = fixture.Directory.Combine("model.bin");
        await File.WriteAllBytesAsync(path, new GgufBuilder().Build());

        var exception = await Assert.ThrowsAsync<ModelCatalogException>(() => fixture.Catalog.AddAsync(path));
        Assert.Contains("扩展名", exception.Message);
    }

    [Fact]
    public async Task AddAsync_InvalidMagic_Throws()
    {
        using var fixture = new Fixture();
        var path = fixture.Directory.Combine("fake.gguf");
        await File.WriteAllBytesAsync(path, "not a gguf file at all"u8.ToArray());

        var exception = await Assert.ThrowsAsync<ModelCatalogException>(() => fixture.Catalog.AddAsync(path));
        Assert.Contains("GGUF", exception.Message);
    }

    [Fact]
    public async Task AddAsync_MissingFile_Throws()
    {
        using var fixture = new Fixture();
        var exception = await Assert.ThrowsAsync<ModelCatalogException>(
            () => fixture.Catalog.AddAsync(fixture.Directory.Combine("missing.gguf")));
        Assert.Contains("文件不存在", exception.Message);
    }

    [Fact]
    public async Task ScanDirectory_FindsValidModelsAndSkipsInvalid()
    {
        using var fixture = new Fixture();
        fixture.CreateModelFile("a.gguf", "模型A");
        fixture.CreateModelFile("b.gguf", "模型B");
        await File.WriteAllBytesAsync(fixture.Directory.Combine("broken.gguf"), "junk data not gguf"u8.ToArray());
        await File.WriteAllTextAsync(fixture.Directory.Combine("readme.txt"), "not a model");

        var progressReports = new List<ModelScanProgress>();
        var added = await fixture.Catalog.ScanDirectoryAsync(
            fixture.Directory.Path,
            includeSubdirectories: false,
            progress: new Progress<ModelScanProgress>(progressReports.Add));

        Assert.Equal(2, added.Count);
        Assert.Contains(progressReports, p => p.CurrentFile?.EndsWith("a.gguf", StringComparison.Ordinal) == true);

        // 二次扫描：不重复登记
        var again = await fixture.Catalog.ScanDirectoryAsync(fixture.Directory.Path, includeSubdirectories: false);
        Assert.Empty(again);
        Assert.Equal(2, (await fixture.Catalog.GetAllAsync()).Count);
    }

    [Fact]
    public async Task ScanDirectory_HonorsSubdirectoryOption()
    {
        using var fixture = new Fixture();
        var nested = fixture.Directory.Combine("nested");
        Directory.CreateDirectory(nested);
        var bytes = new GgufBuilder().AddString("general.name", "嵌套模型").Build();
        await File.WriteAllBytesAsync(Path.Combine(nested, "nested.gguf"), bytes);

        Assert.Empty(await fixture.Catalog.ScanDirectoryAsync(fixture.Directory.Path, includeSubdirectories: false));
        var added = await fixture.Catalog.ScanDirectoryAsync(fixture.Directory.Path, includeSubdirectories: true);
        Assert.Single(added);
    }

    [Fact]
    public async Task ScanDirectory_CanBeCancelled()
    {
        using var fixture = new Fixture();
        for (var i = 0; i < 5; i++)
        {
            fixture.CreateModelFile($"model-{i}.gguf");
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Catalog.ScanDirectoryAsync(fixture.Directory.Path, false, null, cts.Token));
    }

    [Fact]
    public async Task Revalidate_MarksMissingFile()
    {
        using var fixture = new Fixture();
        var path = fixture.CreateModelFile("gone.gguf");
        var model = await fixture.Catalog.AddAsync(path);
        File.Delete(path);

        var revalidated = await fixture.Catalog.RevalidateAsync(model.Id);

        Assert.Equal(ModelFileStatus.Missing, revalidated.Status);
    }

    [Fact]
    public async Task Revalidate_RefreshesMetadataWhenFileChanges()
    {
        using var fixture = new Fixture();
        var path = fixture.CreateModelFile("evolving.gguf", "旧名称");
        var model = await fixture.Catalog.AddAsync(path);
        Assert.Equal("旧名称", model.Name);

        await Task.Delay(20);
        await File.WriteAllBytesAsync(path, new GgufBuilder()
            .AddString("general.name", "新名称")
            .AddString("general.architecture", "llama")
            .Build());

        var revalidated = await fixture.Catalog.RevalidateAsync(model.Id);
        Assert.Equal("新名称", revalidated.Name);
    }

    [Fact]
    public async Task UpdateDetails_And_Remove()
    {
        using var fixture = new Fixture();
        var model = await fixture.Catalog.AddAsync(fixture.CreateModelFile("details.gguf"));

        await fixture.Catalog.UpdateDetailsAsync(model.Id, "别名", new[] { "tag1", "tag1", " tag2 " }, "备注");
        var updated = await fixture.Catalog.GetAsync(model.Id);
        Assert.Equal("别名", updated!.Alias);
        Assert.Equal(2, updated.Tags.Count);
        Assert.Equal("备注", updated.Notes);

        await fixture.Catalog.RemoveAsync(model.Id);
        Assert.Null(await fixture.Catalog.GetAsync(model.Id));
        Assert.True(File.Exists(model.FilePath), "移除登记不得删除模型文件。");
    }

    [Fact]
    public async Task EnsureRegistered_ReturnsExistingWithoutThrow()
    {
        using var fixture = new Fixture();
        var path = fixture.CreateModelFile("ensure.gguf");
        var first = await fixture.Catalog.EnsureRegisteredAsync(path);
        var second = await fixture.Catalog.EnsureRegisteredAsync(path);
        Assert.Equal(first.Id, second.Id);
    }
}
