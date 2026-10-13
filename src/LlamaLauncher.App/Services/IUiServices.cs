namespace LlamaLauncher.App.Services;

/// <summary>确认对话框选项。</summary>
public sealed class ConfirmDialogOptions
{
    public required string Title { get; init; }

    public required string Message { get; init; }

    public string PrimaryButtonText { get; init; } = "确定";

    public string? SecondaryButtonText { get; init; } = "取消";

    /// <summary>需要用户勾选的风险确认文本（如局域网访问）。</summary>
    public string? RiskConfirmationText { get; init; }

    public bool IsDestructive { get; init; }
}

/// <summary>对话框服务（ViewModel 不直接使用 MessageBox，便于测试）。</summary>
public interface IDialogService
{
    /// <summary>返回 true 表示用户确认（若需要风险勾选，必须勾选）。</summary>
    Task<bool> ConfirmAsync(ConfirmDialogOptions options);

    Task ShowInfoAsync(string title, string message);

    Task ShowWarningAsync(string title, string message);

    Task ShowErrorAsync(string title, string message);

    Task<string?> PromptAsync(string title, string message, string initialValue = "");

    /// <summary>用户输入确认文本（如输入“删除”二字才能继续的高风险确认）。</summary>
    Task<string?> PromptWithConfirmationAsync(
        string title,
        string message,
        string requiredConfirmationKeyword);

    /// <summary>带可选复选框的确认对话框。</summary>
    Task<(bool Confirmed, bool CheckboxChecked)> ConfirmWithCheckboxAsync(
        ConfirmDialogOptions options,
        string checkboxText,
        bool defaultChecked = false);

    string? PickOpenFile(string title, string filter, string? initialDirectory = null);

    string? PickFolder(string title, string? initialDirectory = null);

    string? PickSaveFile(string title, string filter, string? defaultFileName = null, string? initialDirectory = null);
}

/// <summary>剪贴板服务（带失败反馈）。</summary>
public interface IClipboardService
{
    Task<(bool Success, string? Error)> SetTextAsync(string text);
}

/// <summary>主题服务。</summary>
public interface IThemeService
{
    string CurrentTheme { get; }

    /// <summary>应用主题（dark / light）。</summary>
    void Apply(string theme);
}

/// <summary>状态栏与轻量通知。</summary>
public interface INotificationService
{
    /// <summary>状态栏文本变化（null 表示清空）。</summary>
    event Action<string?>? StatusChanged;

    /// <summary>底部状态栏文本（可含时间前缀）。</summary>
    void SetStatus(string message);

    void ClearStatus();

    /// <summary>是否将后端事件提示推送到状态栏。</summary>
    void Notify(string message);
}

/// <summary>应用退出协调（关闭策略、托盘、停止服务）。</summary>
public interface IAppLifecycle
{
    /// <summary>用户请求退出（托盘菜单或关闭按钮按设置触发）。</summary>
    Task RequestExitAsync();

    /// <summary>请求把主窗口显示到前台。</summary>
    void ShowMainWindow();

    bool IsExiting { get; }
}
