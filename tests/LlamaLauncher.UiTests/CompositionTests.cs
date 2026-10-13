using System.IO;
using LlamaLauncher.Application.Chat;
using LlamaLauncher.App;
using LlamaLauncher.App.Services;
using LlamaLauncher.App.ViewModels;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Instances;
using LlamaLauncher.Application.Services;
using LlamaLauncher.Infrastructure;
using LlamaLauncher.Infrastructure.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LlamaLauncher.UiTests;

/// <summary>
/// 组合根测试：验证整个依赖注入图（基础设施 + 全部页面 ViewModel）可以完整解析，
/// 尽早暴露注册缺失或循环依赖问题。
/// </summary>
[Collection("数据目录串行")]
public sealed class CompositionTests
{
    [Fact]
    public async Task ContainerResolvesAllPagesAndCoreServices()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "LlamaLauncherUiTests", Guid.NewGuid().ToString("N"));
        var originalRoot = Environment.GetEnvironmentVariable("LLAMALAUNCHER_DATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("LLAMALAUNCHER_DATA_ROOT", tempRoot);
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            InfrastructureServiceCollectionExtensions.AddLlamaLauncherCore(
                services,
                new ConfigurationBuilder().Build());
            services.AddAppShellServices(() => Task.CompletedTask);

            await using var provider = services.BuildServiceProvider();
            var database = provider.GetRequiredService<SqliteDatabase>();
            await database.InitializeAsync();

            // 核心服务
            Assert.NotNull(provider.GetRequiredService<IModelCatalogService>());
            Assert.NotNull(provider.GetRequiredService<IInstanceManager>());
            Assert.NotNull(provider.GetRequiredService<IChatSessionService>());
            Assert.NotNull(provider.GetRequiredService<ILlamaUpdateService>());
            Assert.NotNull(provider.GetRequiredService<IDiagnosticsService>());
            Assert.NotNull(provider.GetRequiredService<IBackupRestoreService>());
            Assert.NotNull(provider.GetRequiredService<ISettingsService>());
            Assert.NotNull(provider.GetRequiredService<IUiActions>());
            // 「关闭窗口=退出」主流程依赖容器中的 IAppLifecycle；缺失会导致点击关闭按钮静默无反应。
            Assert.NotNull(provider.GetRequiredService<IAppLifecycle>());

            // 全部页面 ViewModel
            Assert.NotNull(provider.GetRequiredService<DashboardViewModel>());
            Assert.NotNull(provider.GetRequiredService<InstancesViewModel>());
            Assert.NotNull(provider.GetRequiredService<ModelsViewModel>());
            Assert.NotNull(provider.GetRequiredService<ProfilesViewModel>());
            Assert.NotNull(provider.GetRequiredService<ChatViewModel>());
            Assert.NotNull(provider.GetRequiredService<HardwareViewModel>());
            Assert.NotNull(provider.GetRequiredService<UpdatesViewModel>());
            Assert.NotNull(provider.GetRequiredService<LogsViewModel>());
            Assert.NotNull(provider.GetRequiredService<HistoryViewModel>());
            Assert.NotNull(provider.GetRequiredService<SettingsViewModel>());

            var shell = provider.GetRequiredService<ShellViewModel>();
            Assert.Equal(10, shell.Pages.Count);
            Assert.NotNull(shell.CurrentPage);

            // 配置编辑器工厂可用
            var editorFactory = provider.GetRequiredService<Func<ProfileEditorViewModel>>();
            var editor = editorFactory();
            Assert.NotNull(editor);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LLAMALAUNCHER_DATA_ROOT", originalRoot);
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
