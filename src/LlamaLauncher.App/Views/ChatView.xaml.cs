using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LlamaLauncher.App.Controls;
using LlamaLauncher.App.ViewModels;

namespace LlamaLauncher.App.Views;

public partial class ChatView : UserControl
{
    private ChatViewModel? _viewModel;
    private bool _autoScroll = true;
    private bool _suppressScrollEvents;

    public ChatView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Detach();
        if (DataContext is ChatViewModel viewModel)
        {
            _viewModel = viewModel;
            viewModel.Messages.CollectionChanged += OnMessagesChanged;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            viewModel.InputFocusRequested += OnInputFocusRequested;
        }

        MarkdownViewer.CodeCopied += OnCodeCopied;
        _autoScroll = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            ScrollToEndSuppressed();
            UpdateScrollButton();
        });
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        MarkdownViewer.CodeCopied -= OnCodeCopied;
        Detach();
    }

    private void Detach()
    {
        if (_viewModel is not null)
        {
            _viewModel.Messages.CollectionChanged -= OnMessagesChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.InputFocusRequested -= OnInputFocusRequested;
            _viewModel = null;
        }
    }

    private void OnCodeCopied(bool success, string? error)
    {
        if (DataContext is ChatViewModel chat && chat.Notifications is not null)
        {
            chat.Notifications.Notify(success ? "代码已复制。" : "复制失败：" + error);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChatViewModel.SelectedConversation):
                _autoScroll = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    ScrollToEndSuppressed();
                    UpdateScrollButton();
                });
                break;
            case nameof(ChatViewModel.IsGenerating):
                UpdateScrollButton();
                break;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_autoScroll && e.Action == NotifyCollectionChangedAction.Add)
        {
            ScrollToEndSuppressed();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ScrollToEndSuppressed);
        }

        UpdateScrollButton();
    }

    /// <summary>用户滚动离开底部后暂停自动跟随；回到底部后恢复。</summary>
    private void OnMessagesScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_suppressScrollEvents)
        {
            return;
        }

        if (Math.Abs(e.ExtentHeightChange) < 0.01)
        {
            _autoScroll = IsAtBottom();
            UpdateScrollButton();
        }
        else if (_autoScroll)
        {
            // 内容高度变化（流式追加/新消息）：保持钉在底部
            ScrollToEndSuppressed();
        }
    }

    private void OnScrollToBottomClick(object sender, RoutedEventArgs e)
    {
        _autoScroll = true;
        ScrollToEndSuppressed();
        UpdateScrollButton();
    }

    private void OnInputFocusRequested()
    {
        Dispatcher.BeginInvoke(() =>
        {
            InputBox.Focus();
            InputBox.CaretIndex = InputBox.Text.Length;
        });
    }

    private bool IsAtBottom()
        => MessagesScroll.ScrollableHeight <= 0
           || MessagesScroll.VerticalOffset >= MessagesScroll.ScrollableHeight - 24;

    private void ScrollToEndSuppressed()
    {
        _suppressScrollEvents = true;
        try
        {
            MessagesScroll.ScrollToEnd();
        }
        finally
        {
            _suppressScrollEvents = false;
        }
    }

    private void UpdateScrollButton()
    {
        var show = _viewModel?.SelectedConversation is not null
            && MessagesScroll.ScrollableHeight > 0
            && !IsAtBottom();
        ScrollToBottomButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ScrollToBottomButton.Content = show && _viewModel is { IsGenerating: true }
            ? "↓ 有新内容"
            : "↓ 回到底部";
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            if (DataContext is ChatViewModel viewModel && viewModel.SendCommand.CanExecute(null))
            {
                viewModel.SendCommand.Execute(null);
            }
        }
    }
}
