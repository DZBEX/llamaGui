namespace LlamaLauncher.App.Services;

/// <summary>跨页面导航与联动（由 Shell 在运行时装配，避免 ViewModel 之间互相引用）。</summary>
public interface IUiActions
{
    void NavigateToInstances();

    void NavigateToChat(Guid instanceId);

    void NavigateToLogs(Guid? instanceId);

    void NavigateToUpdates();
}

/// <summary>默认空实现（测试与设计时使用）。</summary>
public sealed class NullUiActions : IUiActions
{
    public void NavigateToInstances()
    {
    }

    public void NavigateToChat(Guid instanceId)
    {
    }

    public void NavigateToLogs(Guid? instanceId)
    {
    }

    public void NavigateToUpdates()
    {
    }
}
