using Microsoft.Extensions.DependencyInjection;
using LlamaLauncher.App.Services;
using LlamaLauncher.App.ViewModels;
using LlamaLauncher.App.ViewModels.Lab;

namespace LlamaLauncher.App;

/// <summary>WPF 应用层（Shell、View、ViewModel、UI 服务）依赖注册。</summary>
internal static class AppServiceCollectionExtensions
{
    /// <param name="shutdownAsync">
    /// 受控退出流程的最后一步（由 App 提供）。注册进 DI 后，主窗口关闭按钮、托盘菜单等所有退出入口
    /// 都通过同一个 <see cref="IAppLifecycle"/> 实例工作；测试环境可传 no-op。
    /// </param>
    public static IServiceCollection AddAppShellServices(
        this IServiceCollection services,
        Func<Task>? shutdownAsync = null)
    {
        // UI 服务
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<NotificationService>());
        services.AddSingleton<IUiActions, UiActions>();

        // 受控退出编排（主窗口“关闭=退出”分支与托盘退出都依赖它）。
        // 注意：必须注册进容器——此前只被手动 new 给托盘使用，导致“关闭窗口=退出”静默失效。
        services.AddSingleton<IAppLifecycle>(sp => new AppLifecycleService(
            sp.GetRequiredService<LlamaLauncher.Application.Instances.IInstanceManager>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<INotificationService>(),
            sp.GetRequiredService<LlamaLauncher.Application.Services.ISettingsService>(),
            sp.GetRequiredService<LlamaLauncher.Application.Abstractions.IInstanceLogStore>(),
            shutdownAsync ?? (() => Task.CompletedTask),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AppLifecycleService>>()));

        // 页面 ViewModel（单例：切换页面不中断状态）
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<InstancesViewModel>();
        services.AddSingleton<ModelsViewModel>();
        services.AddSingleton<ProfilesViewModel>();
        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<BenchmarkViewModel>();
        services.AddSingleton<HardwareViewModel>();
        services.AddSingleton<UpdatesViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ShellViewModel>();

        // 决策模型实验室
        services.AddSingleton<ModelDebugViewModel>();
        services.AddSingleton<WorkflowDesignerViewModel>();
        services.AddSingleton<TestCaseViewModel>();
        services.AddSingleton<BatchTestViewModel>();
        services.AddSingleton<RunHistoryViewModel>();
        services.AddSingleton<LabViewModel>();

        // 配置编辑器按需创建
        services.AddTransient<ProfileEditorViewModel>();
        services.AddSingleton<Func<ProfileEditorViewModel>>(sp =>
            () => sp.GetRequiredService<ProfileEditorViewModel>());

        services.AddSingleton<MainWindow>();
        return services;
    }
}
