using System.Windows;

namespace LlamaLauncher.App.Services;

/// <summary>剪贴板服务（失败时返回错误消息，不抛异常）。</summary>
public sealed class ClipboardService : IClipboardService
{
    public Task<(bool Success, string? Error)> SetTextAsync(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return Task.FromResult<(bool, string?)>((true, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult<(bool, string?)>((false, "复制失败（剪贴板被其他程序占用）：" + ex.Message));
        }
    }
}
