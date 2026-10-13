using CommunityToolkit.Mvvm.ComponentModel;
using LlamaLauncher.App.Services;

namespace LlamaLauncher.App.ViewModels;

/// <summary>页面 ViewModel 基类：忙碌状态、状态栏通知与异常包装。</summary>
public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _busyText;

    public INotificationService? Notifications { get; set; }

    protected IDialogService? Dialogs { get; set; }

    /// <summary>执行带忙碌标记的异步操作；异常转为用户可见的错误对话框。</summary>
    protected async Task RunBusyAsync(string busyText, Func<Task> action, string? errorTitle = null)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        BusyText = busyText;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Notifications?.Notify("操作已取消。");
        }
        catch (Exception ex)
        {
            if (Dialogs is not null)
            {
                await Dialogs.ShowErrorAsync(errorTitle ?? "操作失败", ex.Message).ConfigureAwait(true);
            }

            Notifications?.Notify("操作失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }
}

/// <summary>需要在切换到页面时刷新数据的 ViewModel。</summary>
public interface IPageViewModel
{
    Task OnActivatedAsync();
}
