using System.Windows;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Instances;
using LlamaLauncher.Application.Services;
using LlamaLauncher.App.Controls;
using LlamaLauncher.App.Services;
using LlamaLauncher.App.ViewModels;
using LlamaLauncher.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.App;

/// <summary>应用程序入口：宿主构建、启动恢复、托盘装配与退出流程。</summary>
public partial class App : System.Windows.Application
{
    private IHost? _host;
    private ILogger<App>? _logger;
    private TrayService? _tray;

    /// <summary>是否已进入退出流程（关闭窗口事件据此放行）。</summary>
    public bool IsShutdownRequested { get; private set; }

    public IServiceProvider Services => _host?.Services
        ?? throw new InvalidOperationException("宿主尚未初始化。");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smokeTest = e.Args.Any(a => string.Equals(a, "--smoke-test", StringComparison.OrdinalIgnoreCase));

        // 旧数据位置（%AppData% 或旧便携布局）→ 程序根目录 的一次性迁移，必须发生在任何路径解析之前。
        var migrationReport = LegacyDataMigrator.MigrateIfNeeded(AppContext.BaseDirectory);

        try
        {
            var builder = Host.CreateApplicationBuilder();
            InfrastructureServiceCollectionExtensions.AddLlamaLauncherCore(builder.Services, builder.Configuration);
            builder.Services.AddAppShellServices(ShutdownAsync);

            _host = builder.Build();
            await _host.StartAsync().ConfigureAwait(true);
            _logger = _host.Services.GetRequiredService<ILogger<App>>();
            _logger.LogInformation("LlamaLauncher 启动，版本 {Version}", ThisAssemblyVersion);
            if (migrationReport is { } report)
            {
                _logger.LogInformation(
                    "旧数据迁移：{Source} → {Destination}（成功={Migrated}，问题={Issues}）",
                    report.Source,
                    report.Destination,
                    report.Migrated,
                    report.Issues.Count == 0 ? "无" : string.Join("；", report.Issues));
            }

            var services = _host.Services;
            var notifications = services.GetRequiredService<INotificationService>();
            var settingsService = services.GetRequiredService<ISettingsService>();
            var manager = services.GetRequiredService<IInstanceManager>();
            var monitor = services.GetRequiredService<IResourceMonitorService>();

            var settings = await settingsService.LoadAsync().ConfigureAwait(true);
            services.GetRequiredService<IThemeService>().Apply(settings.Theme);

            // 代码块复制走统一的剪贴板服务（带失败反馈）
            var clipboard = services.GetRequiredService<IClipboardService>();
            MarkdownViewer.CodeCopyHandler = code => clipboard.SetTextAsync(code).GetAwaiter().GetResult();

            // 上次运行恢复（进程校验/接管/标记异常退出）与聊天中断恢复
            var recovery = await manager.InitializeAsync().ConfigureAwait(true);
            var conversations = services.GetRequiredService<IConversationService>();
            await conversations.RecoverInterruptedAsync().ConfigureAwait(true);

            // 无界面冒烟测试模式：用于发布产物自动验证（--smoke-test），成功输出标记后立即退出。
            if (smokeTest)
            {
                Console.WriteLine("SMOKE_OK");
                Console.Out.Flush();
                Shutdown(0);
                return;
            }

            // 数据目录相关提示：迁移结果与解析警告（例如程序目录不可写而回退）。
            if (migrationReport is { Migrated: true } migratedReport)
            {
                notifications.Notify(
                    $"应用数据已迁移到程序目录：{migratedReport.Destination}（原位置：{migratedReport.Source}）。");
            }
            else if (migrationReport is { Issues.Count: > 0 } failedReport)
            {
                notifications.Notify("数据迁移存在问题：" + string.Join("；", failedReport.Issues));
            }

            if ((services.GetRequiredService<IAppPaths>() as AppPaths)?.ResolutionWarning is { } resolutionWarning)
            {
                notifications.Notify(resolutionWarning);
            }

            // 资源监控
            monitor.Interval = TimeSpan.FromSeconds(settings.ResourceSampleIntervalSeconds);

            void SyncTrackedInstances()
            {
                monitor.SetTrackedInstances(manager.GetInstances()
                    .Select(i => new TrackedInstance(i.Id, i.Name, i.Record.ProcessId, i.Record.ExecutablePathSnapshot))
                    .ToList());
            }

            manager.InstanceChanged += (_, _) => SyncTrackedInstances();
            SyncTrackedInstances();
            monitor.Start();

            // Shell 与默认版本显示
            var shell = services.GetRequiredService<ShellViewModel>();
            var defaultInstallation = await services
                .GetRequiredService<ILlamaInstallationService>()
                .GetDefaultAsync()
                .ConfigureAwait(true);
            shell.SetDefaultVersion(defaultInstallation?.VersionLabel);

            // 托盘与退出协调（与主窗口关闭流程共用容器中的同一实例）
            var lifecycle = services.GetRequiredService<IAppLifecycle>();
            _tray = new TrayService(
                manager,
                lifecycle,
                notifications,
                services.GetRequiredService<ILogger<TrayService>>());
            _tray.Show();
            manager.InstanceChanged += (_, _) => _tray.UpdateTooltip();

            var mainWindow = services.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            if (!settings.StartMinimizedToTray)
            {
                mainWindow.Show();
            }

            if (recovery.Messages.Count > 0)
            {
                notifications.Notify(string.Join("；", recovery.Messages));
            }

            if (settings.AutoCheckUpdates)
            {
                _ = AutoCheckUpdatesAsync(services, settings.UpdateChannel);
            }
        }
        catch (Exception ex)
        {
            if (smokeTest)
            {
                Console.Error.WriteLine("SMOKE_FAILED: " + ex);
                Shutdown(2);
                return;
            }

            System.Windows.MessageBox.Show(
                "应用启动失败：" + ex.Message + Environment.NewLine + ex,
                "LlamaLauncher 启动错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private static async Task AutoCheckUpdatesAsync(IServiceProvider services, Domain.UpdateChannel channel)
    {
        try
        {
            var updates = services.GetRequiredService<ILlamaUpdateService>();
            var notifications = services.GetRequiredService<INotificationService>();
            var result = await updates.CheckForUpdatesAsync(channel).ConfigureAwait(false);
            if (result.Success && result.UpdateAvailable && result.LatestRelease is { } latest)
            {
                notifications.Notify($"检测到 llama.cpp 新版本 {latest.TagName}（可在“版本管理”页安装）。");
            }
        }
        catch (Exception)
        {
            // 自动检查失败静默处理（离线环境属正常）
        }
    }

    /// <summary>退出流程的最终步骤（由 AppLifecycleService 调用）。</summary>
    private Task ShutdownAsync()
    {
        IsShutdownRequested = true;
        return Dispatcher.InvokeAsync(() =>
        {
            try
            {
                _tray?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "释放托盘失败");
            }

            Shutdown();
        }).Task;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                if (_host.Services.GetService(typeof(IInstanceLogStore)) is IInstanceLogStore logStore)
                {
                    await logStore.FlushAsync().ConfigureAwait(true);
                }

                await _host.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
                _host.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "关闭宿主时发生错误");
        }

        base.OnExit(e);
    }

    internal static string ThisAssemblyVersion =>
        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
