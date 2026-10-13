using System.Windows;
using System.ComponentModel;
using LlamaLauncher.App.Services;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.App;

public partial class MainWindow : Window
{
    private readonly IServiceProvider _services;

    public MainWindow(IServiceProvider services, ViewModels.ShellViewModel viewModel)
    {
        _services = services;
        InitializeComponent();
        DataContext = viewModel;
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (System.Windows.Application.Current is not App app || app.IsShutdownRequested)
        {
            return;
        }

        // 先同步取消本次关闭，再异步决定“隐藏到托盘”或走受控退出流程，
        // 避免 async void 在首个 await 完成前放行关闭（窗口被直接关闭但退出流程还没跑）。
        e.Cancel = true;
        await HandleCloseRequestAsync();
    }

    private async Task HandleCloseRequestAsync()
    {
        var settings = _services.GetService(typeof(LlamaLauncher.Application.Services.ISettingsService))
            as LlamaLauncher.Application.Services.ISettingsService;
        var closeToTray = true;
        try
        {
            if (settings is not null)
            {
                closeToTray = (await settings.LoadAsync()).CloseToTray;
            }
        }
        catch (Exception ex)
        {
            (_services.GetService(typeof(Microsoft.Extensions.Logging.ILogger<MainWindow>))
                as Microsoft.Extensions.Logging.ILogger<MainWindow>)?.LogWarning(ex, "读取关闭设置失败，按“最小化到托盘”处理");
        }

        if (closeToTray)
        {
            Hide();
            return;
        }

        if (_services.GetService(typeof(IAppLifecycle)) is IAppLifecycle lifecycle)
        {
            await lifecycle.RequestExitAsync();
        }
        else
        {
            (_services.GetService(typeof(Microsoft.Extensions.Logging.ILogger<MainWindow>))
                as Microsoft.Extensions.Logging.ILogger<MainWindow>)?.LogError("IAppLifecycle 未注册，无法执行退出流程。");
        }
    }
}
