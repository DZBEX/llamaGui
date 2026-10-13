namespace LlamaLauncher.App.Services;

/// <summary>状态栏文本承载（由 Shell 订阅）。</summary>
public sealed class NotificationService : INotificationService
{
    public event Action<string?>? StatusChanged;

    public string? CurrentStatus { get; private set; }

    public void SetStatus(string message)
    {
        CurrentStatus = message;
        StatusChanged?.Invoke(message);
    }

    public void ClearStatus()
    {
        CurrentStatus = null;
        StatusChanged?.Invoke(null);
    }

    public void Notify(string message)
        => SetStatus($"[{DateTime.Now:HH:mm:ss}] {message}");
}
