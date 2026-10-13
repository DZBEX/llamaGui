using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Domain;
using LlamaLauncher.IntegrationTests.TestSupport;
using LlamaLauncher.Infrastructure.Sqlite;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Sqlite;

public sealed class RepositoryTests
{
    [Fact]
    public async Task ModelRepository_RoundTripsAllFields()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var repository = new SqliteModelRepository(database);
        var model = new ModelRecord
        {
            Name = "测试模型",
            FilePath = "C:\\模型 目录\\a.gguf",
            CanonicalPath = "C:\\模型 目录\\a.gguf",
            FileSizeBytes = 123456,
            FileLastWriteUtc = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero),
            Status = ModelFileStatus.Ok,
            MetadataStatus = MetadataParseStatus.Parsed,
            Metadata = new GgufModelInfo
            {
                GgufVersion = 3,
                Architecture = "llama",
                ContextLength = 4096,
                QuantizationLabel = "Q4_K_M",
                ParsedUtc = DateTimeOffset.UtcNow,
            },
            Alias = "别名",
            Tags = { "中文", "test" },
            Notes = "备注",
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
            LastCheckedUtc = DateTimeOffset.UtcNow,
        };

        await repository.UpsertAsync(model);
        var loaded = await repository.GetByIdAsync(model.Id);

        Assert.NotNull(loaded);
        Assert.Equal("测试模型", loaded!.Name);
        Assert.Equal(123456, loaded.FileSizeBytes);
        Assert.Equal("llama", loaded.Metadata!.Architecture);
        Assert.Equal("Q4_K_M", loaded.Metadata.QuantizationLabel);
        Assert.Equal(new[] { "中文", "test" }, loaded.Tags);

        var byPath = await repository.FindByCanonicalPathAsync("C:\\模型 目录\\A.GGUF");
        Assert.NotNull(byPath);
        Assert.Equal(model.Id, byPath!.Id);

        await repository.DeleteAsync(model.Id);
        Assert.Null(await repository.GetByIdAsync(model.Id));
    }

    [Fact]
    public async Task ProfileRepository_EnforcesSingleDefault()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var repository = new SqliteProfileRepository(database);
        var now = DateTimeOffset.UtcNow;

        var first = new LaunchProfile { Name = "A", CreatedUtc = now, UpdatedUtc = now, IsDefault = true };
        var second = new LaunchProfile { Name = "B", CreatedUtc = now, UpdatedUtc = now };
        await repository.UpsertAsync(first);
        await repository.UpsertAsync(second);

        Assert.Equal(first.Id, (await repository.GetDefaultAsync())!.Id);

        await repository.SetDefaultAsync(second.Id);
        var defaultProfile = await repository.GetDefaultAsync();
        Assert.Equal(second.Id, defaultProfile!.Id);

        var all = await repository.GetAllAsync();
        Assert.Single(all, p => p.IsDefault);
    }

    [Fact]
    public async Task ProfileRepository_UpsertManyIsTransactional()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var repository = new SqliteProfileRepository(database);
        var now = DateTimeOffset.UtcNow;

        var batch = new List<LaunchProfile>
        {
            new() { Name = "配置-1", CreatedUtc = now, UpdatedUtc = now },
            new() { Name = "配置-2", CreatedUtc = now, UpdatedUtc = now, IsDefault = true },
        };
        await repository.UpsertManyAsync(batch);

        var all = await repository.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal("配置-2", (await repository.GetDefaultAsync())!.Name);

        // 第二批包含唯一约束冲突时，整批回滚。
        var duplicate = new List<LaunchProfile>
        {
            new() { Name = "新配置", CreatedUtc = now, UpdatedUtc = now },
            new() { Name = "配置-1", CreatedUtc = now, UpdatedUtc = now },
        };
        await Assert.ThrowsAnyAsync<Exception>(() => repository.UpsertManyAsync(duplicate));
        var after = await repository.GetAllAsync();
        Assert.Equal(2, after.Count);
        Assert.DoesNotContain(after, p => p.Name == "新配置");
    }

    [Fact]
    public async Task ConversationAndMessages_PersistOrderAndCascadeDelete()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var conversations = new SqliteConversationRepository(database);
        var messages = new SqliteMessageRepository(database);
        var now = DateTimeOffset.UtcNow;

        var conversation = new Conversation { Title = "会话 A", CreatedUtc = now, UpdatedUtc = now };
        await conversations.UpsertAsync(conversation);

        var userMessage = new ChatMessage
        {
            ConversationId = conversation.Id,
            Role = ChatRole.User,
            Content = "你好",
            Status = MessageStatus.Completed,
            Sequence = await messages.GetNextSequenceAsync(conversation.Id),
            CreatedUtc = now,
            CompletedUtc = now,
        };
        await messages.AddAsync(userMessage);

        var assistantMessage = new ChatMessage
        {
            ConversationId = conversation.Id,
            Role = ChatRole.Assistant,
            Content = "你好！",
            Status = MessageStatus.Completed,
            Sequence = await messages.GetNextSequenceAsync(conversation.Id),
            CreatedUtc = now,
            CompletedUtc = now,
            Metrics = new ChatMetrics { CompletionTokens = 3, TokensPerSecond = 12.5 },
        };
        await messages.AddAsync(assistantMessage);

        var loaded = await messages.GetByConversationAsync(conversation.Id);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(userMessage.Id, loaded[0].Id);
        Assert.Equal(3, loaded[1].Metrics!.CompletionTokens);

        await conversations.DeleteAsync(conversation.Id);
        Assert.Empty(await messages.GetByConversationAsync(conversation.Id));
        Assert.Null(await conversations.GetByIdAsync(conversation.Id));
    }

    [Fact]
    public async Task Messages_MarkStreamingAsInterrupted()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var conversations = new SqliteConversationRepository(database);
        var messages = new SqliteMessageRepository(database);
        var now = DateTimeOffset.UtcNow;

        var conversation = new Conversation { Title = "中断恢复", CreatedUtc = now, UpdatedUtc = now };
        await conversations.UpsertAsync(conversation);

        await messages.AddAsync(new ChatMessage
        {
            ConversationId = conversation.Id,
            Role = ChatRole.Assistant,
            Content = "正在生成…",
            Status = MessageStatus.Streaming,
            Sequence = 0,
            CreatedUtc = now,
        });
        await messages.AddAsync(new ChatMessage
        {
            ConversationId = conversation.Id,
            Role = ChatRole.User,
            Content = "完整消息",
            Status = MessageStatus.Completed,
            Sequence = 1,
            CreatedUtc = now,
            CompletedUtc = now,
        });

        var affected = await messages.MarkStreamingAsInterruptedAsync(now);
        Assert.Equal(1, affected);

        var loaded = await messages.GetByConversationAsync(conversation.Id);
        Assert.Equal(MessageStatus.Interrupted, loaded[0].Status);
        Assert.NotNull(loaded[0].CompletedUtc);
        Assert.Equal(MessageStatus.Completed, loaded[1].Status);
    }

    [Fact]
    public async Task SettingsRepository_UpsertAndDelete()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var repository = new SqliteSettingsRepository(database);
        var now = DateTimeOffset.UtcNow;

        Assert.Null(await repository.GetAsync("theme"));
        await repository.SetAsync("theme", "\"dark\"", now);
        await repository.SetAsync("theme", "\"light\"", now.AddMinutes(1));
        Assert.Equal("\"light\"", (await repository.GetAsync("theme"))!.ValueJson);

        await repository.SetAsync("locale", "\"zh-CN\"", now);
        Assert.Equal(2, (await repository.GetAllAsync()).Count);

        await repository.DeleteAsync("theme");
        Assert.Null(await repository.GetAsync("theme"));
    }

    [Fact]
    public async Task InstallationRepository_SingleDefaultAndLookupByPath()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var repository = new SqliteInstallationRepository(database);
        var now = DateTimeOffset.UtcNow;

        var first = new LlamaCppInstallation
        {
            VersionLabel = "b1000",
            Directory = "C:\\runtimes\\b1000",
            ExecutablePath = "C:\\runtimes\\b1000\\llama-server.exe",
            InstalledUtc = now,
            IsDefault = true,
            BackendTags = { BackendKind.Cuda, BackendKind.Cpu },
        };
        var second = new LlamaCppInstallation
        {
            VersionLabel = "b1001",
            Directory = "C:\\runtimes\\b1001",
            ExecutablePath = "C:\\runtimes\\b1001\\llama-server.exe",
            InstalledUtc = now.AddDays(1),
        };
        await repository.UpsertAsync(first);
        await repository.UpsertAsync(second);

        Assert.Equal(first.Id, (await repository.GetDefaultAsync())!.Id);

        var byPath = await repository.FindByExecutablePathAsync("c:\\runtimes\\b1001\\LLAMA-SERVER.EXE");
        Assert.NotNull(byPath);
        Assert.Contains(BackendKind.Cuda, first.BackendTags);
        Assert.Equal(new[] { BackendKind.Cuda, BackendKind.Cpu }, (await repository.GetByIdAsync(first.Id))!.BackendTags);
    }

    [Fact]
    public async Task StartupHistoryRepository_FiltersAndOrders()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var repository = new SqliteStartupHistoryRepository(database);
        var instanceId = Guid.NewGuid();
        var baseTime = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 5; i++)
        {
            await repository.AddAsync(new StartupHistoryEntry
            {
                InstanceId = i < 3 ? instanceId : Guid.NewGuid(),
                InstanceNameSnapshot = $"实例-{i}",
                StartedUtc = baseTime.AddMinutes(i),
                FinalState = InstanceState.Stopped,
                CommandLineMasked = "llama-server --api-key ******",
            });
        }

        var all = await repository.QueryAsync(new HistoryQuery());
        Assert.Equal(5, all.Count);
        Assert.True(all[0].StartedUtc > all[^1].StartedUtc);

        var filtered = await repository.QueryAsync(new HistoryQuery { InstanceId = instanceId });
        Assert.Equal(3, filtered.Count);

        var entry = filtered[0];
        entry.FinalState = InstanceState.Failed;
        entry.ErrorSummary = "测试失败";
        await repository.UpdateAsync(entry);
        var reloaded = await repository.GetByIdAsync(entry.Id);
        Assert.Equal(InstanceState.Failed, reloaded!.FinalState);
        Assert.Equal("测试失败", reloaded.ErrorSummary);
    }

    [Fact]
    public async Task UpdateOperationRepository_RoundTrip()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var repository = new SqliteUpdateOperationRepository(database);
        var operation = new UpdateOperation
        {
            Kind = UpdateOperationKind.Download,
            ToVersion = "b9999",
            AssetName = "llama-b9999-bin-win-cuda-cu12.4-x64.zip",
            AssetUrl = "https://example.invalid/asset.zip",
            AssetSizeBytes = 123456789,
            Stage = UpdateStage.Downloading,
            StartedUtc = DateTimeOffset.UtcNow,
        };
        await repository.AddAsync(operation);

        operation.Stage = UpdateStage.Active;
        operation.CompletedUtc = DateTimeOffset.UtcNow;
        operation.Sha256 = new string('a', 64);
        operation.Sha256Verified = true;
        operation.Sha256FromOfficialDigest = true;
        await repository.UpdateAsync(operation);

        var recent = await repository.GetRecentAsync(10);
        var loaded = Assert.Single(recent);
        Assert.Equal(UpdateStage.Active, loaded.Stage);
        Assert.True(loaded.Sha256Verified);
        Assert.True(loaded.Sha256FromOfficialDigest);
    }
}
