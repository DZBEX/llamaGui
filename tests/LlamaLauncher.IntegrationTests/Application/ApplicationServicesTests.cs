using System.IO.Compression;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Services;
using LlamaLauncher.Domain;
using LlamaLauncher.IntegrationTests.TestSupport;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Application;

public sealed class ApplicationServicesTests
{
    [Fact]
    public async Task SettingsService_RoundTrips()
    {
        using var fixture = new IntegrationFixture();
        var settings = await fixture.SettingsService.LoadAsync();
        Assert.Equal("dark", settings.Theme);

        settings.Theme = "light";
        settings.CloseToTray = false;
        settings.UpdateChannel = UpdateChannel.Prerelease;
        settings.RuntimesDirectory = @"D:\custom\runtimes\llama.cpp";
        settings.DownloadsDirectory = @"D:\custom\downloads";
        settings.ModelsDirectory = @"D:\models";
        await fixture.SettingsService.SaveAsync(settings);

        var reloaded = await fixture.SettingsService.LoadAsync();
        Assert.Equal("light", reloaded.Theme);
        Assert.False(reloaded.CloseToTray);
        Assert.Equal(UpdateChannel.Prerelease, reloaded.UpdateChannel);
        Assert.Equal(@"D:\custom\runtimes\llama.cpp", reloaded.RuntimesDirectory);
        Assert.Equal(@"D:\custom\downloads", reloaded.DownloadsDirectory);
        Assert.Equal(@"D:\models", reloaded.ModelsDirectory);
    }

    [Fact]
    public async Task ProfileService_CreateDuplicateValidateDelete()
    {
        using var fixture = new IntegrationFixture();
        var modelPath = fixture.CreateModelFile();
        var model = await fixture.ModelCatalog.AddAsync(modelPath);
        var installation = await fixture.InstallationService.RegisterExternalAsync(
            IntegrationFixture.ChildExecutablePath);

        var profile = await fixture.ProfileService.CreateAsync("配置一", model.Id, installation.Id);
        Assert.Equal(8080, profile.Options.Port);
        Assert.Equal(modelPath, profile.Options.ModelPath);

        var exception = await Assert.ThrowsAsync<LaunchProfileException>(
            () => fixture.ProfileService.CreateAsync("配置一", null, null));
        Assert.Contains("同名", exception.Message);

        var report = await fixture.ProfileService.ValidateAsync(profile);
        Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(i => i.Message)));
        Assert.NotNull(report.MaskedCommandLinePreview);

        // 第二个配置建议不同端口
        var second = await fixture.ProfileService.CreateAsync("配置二", model.Id, installation.Id);
        Assert.NotEqual(profile.Options.Port, second.Options.Port);

        // 复制
        var copy = await fixture.ProfileService.DuplicateAsync(profile.Id, null);
        Assert.Contains("副本", copy.Name);
        Assert.NotEqual(profile.Id, copy.Id);
        Assert.False(copy.IsDefault);

        // 删除被运行中实例引用的配置会被阻止（使用未启动实例验证普通删除路径）
        await fixture.ProfileService.DeleteAsync(second.Id);
        Assert.Null(await fixture.ProfileService.GetAsync(second.Id));
    }

    [Fact]
    public async Task ProfileService_DeleteBlockedWhenInstanceActive()
    {
        using var fixture = new IntegrationFixture();
        var now = fixture.TimeProvider.GetUtcNow();
        var profile = new LaunchProfile
        {
            Name = "被引用配置",
            Options = new ServerLaunchOptions { Port = IntegrationFixture.GetFreePort() },
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        await fixture.Profiles.UpsertAsync(profile);
        await fixture.InstancesRepo.UpsertAsync(new ServerInstance
        {
            Name = "运行实例",
            ProfileId = profile.Id,
            State = InstanceState.Ready,
            CreatedUtc = now,
            UpdatedUtc = now,
        });

        var exception = await Assert.ThrowsAsync<LaunchProfileException>(
            () => fixture.ProfileService.DeleteAsync(profile.Id));

        Assert.Contains("运行中的实例", exception.Message);
    }

    [Fact]
    public async Task BackupAndRestore_RoundTripsThroughValidation()
    {
        using var fixture = new IntegrationFixture();
        var now = fixture.TimeProvider.GetUtcNow();
        await fixture.ConversationsService.CreateAsync(null, null, "备份测试用会话");

        var backupPath = await fixture.Backup.CreateBackupAsync();
        Assert.True(File.Exists(backupPath));

        // 删除会话后恢复备份
        var conversations = await fixture.ConversationsService.GetAllAsync();
        await fixture.ConversationsService.DeleteAsync(conversations[0].Id);
        Assert.Empty(await fixture.ConversationsService.GetAllAsync());

        var restore = await fixture.Backup.RestoreAsync(backupPath);

        Assert.True(restore.Success, restore.Message);
        Assert.NotNull(restore.PreRestoreBackupPath);
        Assert.True(File.Exists(restore.PreRestoreBackupPath!));

        // 恢复后重新查询（同一进程连接池已清理）
        var restored = await fixture.ConversationsService.GetAllAsync();
        Assert.Single(restored);
    }

    [Fact]
    public async Task BackupRestore_RejectsNonSqliteFile()
    {
        using var fixture = new IntegrationFixture();
        using var directory = new TempDirectory();
        var fakeBackup = directory.Combine("fake.db");
        await File.WriteAllTextAsync(fakeBackup, "这不是数据库");

        var result = await fixture.Backup.RestoreAsync(fakeBackup);

        Assert.False(result.Success);
        Assert.Contains("校验失败", result.Message);
    }

    [Fact]
    public async Task Diagnostics_ExportsMaskedBundle()
    {
        using var fixture = new IntegrationFixture();
        var now = fixture.TimeProvider.GetUtcNow();
        var instanceId = Guid.NewGuid();
        fixture.LogStore.AddKnownSecret("diagnostics-secret-value");
        fixture.LogStore.Append(instanceId, "诊断实例", LogStreamKind.StandardOutput, "token=diagnostics-secret-value");
        await fixture.LogStore.FlushAsync();

        using var directory = new TempDirectory();
        var destination = directory.Combine("bundle.zip");
        var result = await fixture.Diagnostics.ExportBundleAsync(destination, includeUserPaths: false);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(destination));

        using var archive = ZipFile.OpenRead(destination);
        Assert.Contains(archive.Entries, e => e.FullName == "reports/environment.txt");
        Assert.Contains(archive.Entries, e => e.FullName == "reports/installations.json");

        var logEntry = archive.Entries.First(e => e.FullName.StartsWith("logs/", StringComparison.Ordinal));
        using var reader = new StreamReader(logEntry.Open());
        var content = await reader.ReadToEndAsync();
        Assert.DoesNotContain("diagnostics-secret-value", content);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), content);
    }
}
