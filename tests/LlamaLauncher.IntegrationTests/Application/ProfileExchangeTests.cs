using LlamaLauncher.Application.Exchange;
using LlamaLauncher.Application.Models;
using LlamaLauncher.Application.Services;
using LlamaLauncher.Domain;
using LlamaLauncher.IntegrationTests.TestSupport;
using LlamaLauncher.Infrastructure.Gguf;
using LlamaLauncher.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Application;

public sealed class ProfileExchangeTests
{
    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Directory = new TempDirectory();
            var database = TestDatabase.CreateAsync(Directory).GetAwaiter().GetResult();
            Models = new SqliteModelRepository(database);
            Profiles = new SqliteProfileRepository(database);
            Installations = new SqliteInstallationRepository(database);
            TimeProvider = new FakeTimeProvider();
            Catalog = new ModelCatalogService(
                Models,
                new GgufMetadataReader(TimeProvider),
                TimeProvider,
                NullLogger<ModelCatalogService>.Instance);
            Exchange = new ProfileExchangeService(
                Profiles,
                Models,
                Installations,
                Catalog,
                TimeProvider,
                NullLogger<ProfileExchangeService>.Instance);
        }

        public TempDirectory Directory { get; }

        public SqliteModelRepository Models { get; }

        public SqliteProfileRepository Profiles { get; }

        public SqliteInstallationRepository Installations { get; }

        public FakeTimeProvider TimeProvider { get; }

        public ModelCatalogService Catalog { get; }

        public ProfileExchangeService Exchange { get; }

        public void Dispose() => Directory.Dispose();
    }

    private static string CreateModelFile(TempDirectory directory, string name = "model.gguf")
    {
        var path = directory.Combine(name);
        File.WriteAllBytes(path, new GgufBuilder()
            .AddString("general.name", "导出测试模型")
            .AddString("general.architecture", "llama")
            .Build());
        return path;
    }

    [Fact]
    public async Task ExportStripsSecretReferencesByDefault()
    {
        using var fixture = new Fixture();
        var modelPath = CreateModelFile(fixture.Directory);
        var model = await fixture.Catalog.AddAsync(modelPath);
        var now = fixture.TimeProvider.GetUtcNow();

        var profile = new LaunchProfile
        {
            Name = "局域网配置",
            ModelId = model.Id,
            Options = new ServerLaunchOptions
            {
                ModelPath = modelPath,
                Host = "0.0.0.0",
                Port = 9000,
                ApiKeyEnabled = true,
                ApiKeySecretName = "llamalauncher.lan.apikey",
            },
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        await fixture.Profiles.UpsertAsync(profile);

        var exportPath = fixture.Directory.Combine("export-default.json");
        await fixture.Exchange.ExportAsync(null, exportPath, includeSecretReferences: false);
        var content = await File.ReadAllTextAsync(exportPath);
        Assert.DoesNotContain("llamalauncher.lan.apikey", content);
        Assert.Contains("\"apiKeyEnabled\": true", content);

        var exportWithSecrets = fixture.Directory.Combine("export-secrets.json");
        await fixture.Exchange.ExportAsync(null, exportWithSecrets, includeSecretReferences: true);
        var contentWithSecrets = await File.ReadAllTextAsync(exportWithSecrets);
        Assert.Contains("llamalauncher.lan.apikey", contentWithSecrets);
    }

    [Fact]
    public async Task ExportThenPreviewThenApply_RoundTrips()
    {
        using var fixture = new Fixture();
        var modelPath = CreateModelFile(fixture.Directory);
        var model = await fixture.Catalog.AddAsync(modelPath);
        var now = fixture.TimeProvider.GetUtcNow();

        var profile = new LaunchProfile
        {
            Name = "配置甲",
            ModelId = model.Id,
            Options = new ServerLaunchOptions
            {
                ModelPath = modelPath,
                Host = "127.0.0.1",
                Port = 9100,
                ContextSize = 4096,
                GpuLayers = "auto",
            },
            ExtraArguments = "--no-warmup --verbose",
            IsDefault = true,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        await fixture.Profiles.UpsertAsync(profile);

        var exportPath = fixture.Directory.Combine("profiles.json");
        await fixture.Exchange.ExportAsync(new[] { profile.Id }, exportPath, includeSecretReferences: false);

        // 重命名原档案，使导入走“新增”路径
        profile.Name = "配置甲-已改名";
        await fixture.Profiles.UpsertAsync(profile);

        var plan = await fixture.Exchange.PreviewAsync(exportPath);
        Assert.True(plan.SchemaSupported);
        var item = Assert.Single(plan.Items);
        Assert.Equal(ImportItemAction.Add, item.Action);
        Assert.False(item.HasErrors);

        var result = await fixture.Exchange.ApplyAsync(plan, null);
        Assert.Equal(1, result.Added);

        var imported = (await fixture.Profiles.GetAllAsync()).Single(p => p.Name == "配置甲");
        Assert.Equal(9100, imported.Options.Port);
        Assert.Equal(4096, imported.Options.ContextSize);
        Assert.Equal("auto", imported.Options.GpuLayers);
        Assert.Equal("--no-warmup --verbose", imported.ExtraArguments);
        Assert.Equal(model.Id, imported.ModelId);
        Assert.True(imported.IsDefault);
    }

    [Fact]
    public async Task Apply_OverwritesExistingProfileKeepingId()
    {
        using var fixture = new Fixture();
        var modelPath = CreateModelFile(fixture.Directory);
        var model = await fixture.Catalog.AddAsync(modelPath);
        var now = fixture.TimeProvider.GetUtcNow();

        var profile = new LaunchProfile
        {
            Name = "覆盖目标",
            ModelId = model.Id,
            Options = new ServerLaunchOptions { ModelPath = modelPath, Port = 9200 },
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        await fixture.Profiles.UpsertAsync(profile);

        var exportPath = fixture.Directory.Combine("overwrite.json");
        await fixture.Exchange.ExportAsync(new[] { profile.Id }, exportPath, includeSecretReferences: false);

        profile.Options.Port = 1;
        await fixture.Profiles.UpsertAsync(profile);

        var plan = await fixture.Exchange.PreviewAsync(exportPath);
        var item = Assert.Single(plan.Items);
        Assert.Equal(ImportItemAction.Overwrite, item.Action);

        var result = await fixture.Exchange.ApplyAsync(plan, null);
        Assert.Equal(1, result.Overwritten);

        var loaded = await fixture.Profiles.GetByIdAsync(profile.Id);
        Assert.NotNull(loaded);
        Assert.Equal(9200, loaded!.Options.Port);
    }

    [Fact]
    public async Task Apply_SkipsItemsNotSelected()
    {
        using var fixture = new Fixture();
        var now = fixture.TimeProvider.GetUtcNow();

        var document = new ProfileExchangeDocument
        {
            SchemaVersion = 1,
            ExportedUtc = now,
            Profiles =
            {
                new ProfileExchangeEntry
                {
                    Name = "条目一",
                    Options = new ServerLaunchOptions { Port = 1, Host = "127.0.0.1" },
                },
                new ProfileExchangeEntry
                {
                    Name = "条目二",
                    Options = new ServerLaunchOptions { Port = 2, Host = "127.0.0.1" },
                },
            },
        };
        var path = fixture.Directory.Combine("manual.json");
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(document, JsonDefaults.Export));

        var plan = await fixture.Exchange.PreviewAsync(path);
        Assert.Equal(2, plan.Items.Count);

        var result = await fixture.Exchange.ApplyAsync(plan, new HashSet<int> { 1 });
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Skipped);
        var profiles = await fixture.Profiles.GetAllAsync();
        Assert.Single(profiles);
        Assert.Equal("条目二", profiles[0].Name);
    }

    [Fact]
    public async Task Preview_FutureSchema_IsRejected()
    {
        using var fixture = new Fixture();
        var path = fixture.Directory.Combine("future.json");
        await File.WriteAllTextAsync(
            path,
            """{"schemaVersion": 99, "profiles": []}""");

        var plan = await fixture.Exchange.PreviewAsync(path);
        Assert.False(plan.SchemaSupported);
        Assert.Contains("99", plan.SchemaError);
    }

    [Fact]
    public async Task Preview_InvalidJson_ThrowsFriendlyError()
    {
        using var fixture = new Fixture();
        var path = fixture.Directory.Combine("broken.json");
        await File.WriteAllTextAsync(path, "{ this is not json ");

        var exception = await Assert.ThrowsAsync<ProfileExchangeException>(() => fixture.Exchange.PreviewAsync(path));
        Assert.Contains("JSON", exception.Message);
    }

    [Fact]
    public async Task MissingModelFile_ImportsWithWarningAndNoModelLink()
    {
        using var fixture = new Fixture();
        var now = fixture.TimeProvider.GetUtcNow();
        var document = new ProfileExchangeDocument
        {
            SchemaVersion = 1,
            ExportedUtc = now,
            Profiles =
            {
                new ProfileExchangeEntry
                {
                    Name = "缺模型配置",
                    Model = new ModelReferenceSnapshot
                    {
                        Name = "缺失模型",
                        FilePath = fixture.Directory.Combine("missing.gguf"),
                        PathIsRelative = false,
                    },
                    Options = new ServerLaunchOptions { Port = 8080, Host = "127.0.0.1" },
                },
            },
        };
        var path = fixture.Directory.Combine("missing-model.json");
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(document, JsonDefaults.Export));

        var plan = await fixture.Exchange.PreviewAsync(path);
        var item = Assert.Single(plan.Items);
        Assert.False(item.HasErrors);
        Assert.Contains(item.Issues, i => i.Message.Contains("模型文件不存在"));

        var result = await fixture.Exchange.ApplyAsync(plan, null);
        Assert.Equal(1, result.Added);
        var imported = (await fixture.Profiles.GetAllAsync()).Single();
        Assert.Null(imported.ModelId);
        Assert.NotNull(imported.ModelSnapshot);
    }
}
