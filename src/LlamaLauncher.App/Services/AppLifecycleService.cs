using System.Windows;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Instances;
using LlamaLauncher.Application.Services;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.App.Services;

/// <summary>
/// 退出流程：确认 → 停止本应用启动的服务（安全默认，不留孤儿进程）→ 刷新日志 → 关闭宿主。
/// 关闭按钮行为：按设置“隐藏到托盘”或走退出流程。
/// </summary>
public sealed class AppLifecycleService : IAppLifecycle
{
    private readonly IInstanceManager _instances;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ISettingsService _settings;
    private readonly IInstanceLogStore _logStore;
    private readonly Func<Task> _shutdownAsync;
    private readonly ILogger<AppLifecycleService> _logger;

    public AppLifecycleService(
        IInstanceManager instances,
        IDialogService dialogs,
        INotificationService notifications,
        ISettingsService settings,
        IInstanceLogStore logStore,
        Func<Task> shutdownAsync,
        ILogger<AppLifecycleService> logger)
    {
        _instances = instances;
        _dialogs = dialogs;
        _notifications = notifications;
        _settings = settings;
        _logStore = logStore;
        _shutdownAsync = shutdownAsync;
        _logger = logger;
    }

    public bool IsExiting { get; private set; }

    public void ShowMainWindow()
    {
        var window = System.Windows.Application.Current?.MainWindow;
        if (window is null)
        {
            return;
        }

        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    public async Task RequestExitAsync()
    {
        if (IsExiting)
        {
            return;
        }

        IsExiting = true;
        try
        {
            var running = _instances.GetInstances().Where(i => i.IsActive).ToList();
            if (running.Count > 0)
            {
                var names = string.Join("、", running.Take(5).Select(i => i.Name));
                var confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogOptions
                {
                    Title = "退出确认",
                    Message = $"仍有 {running.Count} 个服务实例在运行（{names}）。\n" +
                              "退出 LlamaLauncher 前将停止这些由本应用启动的进程（不会影响其他程序启动的同名进程）。",
                    PrimaryButtonText = "停止服务并退出",
                    SecondaryButtonText = "取消",
                    IsDestructive = true,
                }).ConfigureAwait(true);

                if (!confirmed)
                {
                    IsExiting = false;
                    return;
                }

                _notifications.SetStatus("正在停止所有服务实例…");
                await _instances.StopAllAsync(force: false).ConfigureAwait(true);
            }

            _notifications.SetStatus("正在关闭…");
            try
            {
                await _logStore.FlushAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "刷新日志失败");
            }

            await _shutdownAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "退出流程失败");
            IsExiting = false;
            await _dialogs.ShowErrorAsync("退出失败", "关闭过程中出现错误：" + ex.Message).ConfigureAwait(true);
        }
    }
}
