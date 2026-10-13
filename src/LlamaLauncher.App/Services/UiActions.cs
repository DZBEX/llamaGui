using LlamaLauncher.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LlamaLauncher.App.Services;

/// <summary>通过懒解析 Shell/页面 ViewModel 实现跨页导航（避免 DI 循环依赖）。</summary>
public sealed class UiActions : IUiActions
{
    private readonly IServiceProvider _services;

    public UiActions(IServiceProvider services) => _services = services;

    public void NavigateToInstances()
        => _services.GetRequiredService<ShellViewModel>().NavigateTo<InstancesViewModel>();

    public void NavigateToChat(Guid instanceId)
    {
        _ = PrepareChatAndNavigateAsync(instanceId);
    }

    public void NavigateToLogs(Guid? instanceId)
    {
        var shell = _services.GetRequiredService<ShellViewModel>();
        if (instanceId is { } id)
        {
            _services.GetRequiredService<LogsViewModel>().SelectInstance(id);
        }

        shell.NavigateTo<LogsViewModel>();
    }

    public void NavigateToUpdates()
        => _services.GetRequiredService<ShellViewModel>().NavigateTo<UpdatesViewModel>();

    private async Task PrepareChatAndNavigateAsync(Guid instanceId)
    {
        try
        {
            var chat = _services.GetRequiredService<ChatViewModel>();
            await chat.PrepareForInstanceAsync(instanceId);
            _services.GetRequiredService<ShellViewModel>().NavigateTo<ChatViewModel>();
        }
        catch (Exception)
        {
            // 导航失败不致命；用户仍可手动切换到聊天页
        }
    }
}
