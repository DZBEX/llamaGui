using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Chat;
using LlamaLauncher.Application.Instances;
using LlamaLauncher.App.Services;
using LlamaLauncher.Domain;
using LlamaLauncher.Domain.Validation;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.App.ViewModels;

/// <summary>会话列表项。</summary>
public sealed partial class ConversationItemViewModel : ObservableObject
{
    public ConversationItemViewModel(Conversation conversation) => Conversation = conversation;

    [ObservableProperty]
    private Conversation _conversation;

    [ObservableProperty]
    private bool _isVisible = true;

    public Guid Id => Conversation.Id;

    public string Title => Conversation.Title;

    public string UpdatedText => Formatting.LocalTime(Conversation.UpdatedUtc);

    /// <summary>按搜索词更新可见性（返回是否可见）。</summary>
    public bool ApplyFilter(string query)
    {
        IsVisible = query.Length == 0
            || Title.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        return IsVisible;
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(UpdatedText));
    }
}

/// <summary>聊天消息项（流式期间原位更新，避免整列表重建）。</summary>
public sealed partial class MessageViewModel : ObservableObject
{
    public MessageViewModel(ChatMessage message) => _message = message;

    [ObservableProperty]
    private ChatMessage _message;

    [ObservableProperty]
    private bool _isLastAssistant;

    partial void OnIsLastAssistantChanged(bool value) => OnPropertyChanged(nameof(ShowRegenerate));

    public void UpdateFrom(ChatMessage message)
    {
        Message = message;
        OnPropertyChanged(nameof(MarkdownContent));
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ShowStatus));
        OnPropertyChanged(nameof(IsStreaming));
        OnPropertyChanged(nameof(MetricsText));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(ShowRegenerate));
    }

    public Guid Id => Message.Id;

    public bool IsUser => Message.Role == ChatRole.User;

    public bool IsAssistant => Message.Role == ChatRole.Assistant;

    public string RoleText => Message.Role switch
    {
        ChatRole.User => "我",
        ChatRole.Assistant => "助手",
        _ => "系统",
    };

    /// <summary>头像文字（圆形徽标内使用）。</summary>
    public string AvatarText => Message.Role switch
    {
        ChatRole.User => "我",
        ChatRole.Assistant => "AI",
        _ => "系",
    };

    public string MarkdownContent => Message.Content;

    public string TimeText => Formatting.MessageTime(Message.CreatedUtc);

    public string StatusText => Message.Status switch
    {
        MessageStatus.Streaming => "生成中…",
        MessageStatus.Interrupted => "已中断",
        MessageStatus.Failed => "失败",
        _ => string.Empty,
    };

    public bool ShowStatus => Message.Status is MessageStatus.Streaming or MessageStatus.Interrupted or MessageStatus.Failed;

    public bool IsStreaming => Message.Status == MessageStatus.Streaming;

    public bool CanDelete => Message.Status != MessageStatus.Streaming;

    /// <summary>仅最后一条助手消息、且不在流式生成中时提供“重新生成”操作。</summary>
    public bool ShowRegenerate => IsLastAssistant && !IsStreaming;

    public string MetricsText
    {
        get
        {
            if (Message.Status == MessageStatus.Streaming)
            {
                return string.Empty;
            }

            var metrics = Message.Metrics;
            if (metrics is null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if (metrics.ElapsedMs is { } elapsed)
            {
                parts.Add($"耗时 {elapsed / 1000.0:0.0} 秒");
            }

            if (metrics.CompletionTokens is { } completion)
            {
                parts.Add($"生成 {completion} tokens");
            }

            if (metrics.TokensPerSecond is { } speed)
            {
                parts.Add($"{speed:0.#} tok/s");
            }

            if (metrics.ModelNameSnapshot is { Length: > 0 } model)
            {
                parts.Add(model);
            }

            return parts.Count == 0 ? string.Empty : string.Join(" · ", parts);
        }
    }

    public string? ErrorText => Message.ErrorText;
}

/// <summary>内置聊天页。</summary>
public sealed partial class ChatViewModel : ViewModelBase, IPageViewModel, IDisposable
{
    private static readonly string[] DefaultSuggestions =
    {
        "用三句话介绍一下你自己。",
        "写一个 Python 快速排序示例，并解释思路。",
        "把下面这段话润色得更专业：这个功能很好用。",
    };

    private readonly IConversationService _conversations;
    private readonly IChatSessionService _chat;
    private readonly IInstanceManager _instances;
    private readonly IClipboardService _clipboard;
    private readonly IUiActions _uiActions;
    private readonly ILogger<ChatViewModel> _logger;
    private readonly DispatcherTimer _elapsedTimer;
    private DateTimeOffset? _generationStart;

    [ObservableProperty]
    private ConversationItemViewModel? _selectedConversation;

    [ObservableProperty]
    private InstanceOption? _selectedInstance;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isGenerating;

    [ObservableProperty]
    private string _generationStatus = string.Empty;

    [ObservableProperty]
    private bool _isParametersExpanded;

    [ObservableProperty]
    private string _temperatureText = string.Empty;

    [ObservableProperty]
    private string _topPText = string.Empty;

    [ObservableProperty]
    private string _topKText = string.Empty;

    [ObservableProperty]
    private string _minPText = string.Empty;

    [ObservableProperty]
    private string _repeatPenaltyText = string.Empty;

    [ObservableProperty]
    private string _seedText = string.Empty;

    [ObservableProperty]
    private string _maxTokensText = string.Empty;

    [ObservableProperty]
    private string _stopSequencesText = string.Empty;

    [ObservableProperty]
    private string _systemPromptText = string.Empty;

    [ObservableProperty]
    private string _parameterStatus = string.Empty;

    [ObservableProperty]
    private string _connectionStateText = "无可用实例";

    [ObservableProperty]
    private string _connectionDetailText = "请先在“服务实例”页启动一个实例。";

    [ObservableProperty]
    private InstanceState? _connectionState;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isTestingConnection;

    [ObservableProperty]
    private bool _hasSearchResults = true;

    public ChatViewModel(
        IConversationService conversations,
        IChatSessionService chat,
        IInstanceManager instances,
        IClipboardService clipboard,
        IDialogService dialogs,
        INotificationService notifications,
        IUiActions uiActions,
        ILogger<ChatViewModel> logger)
    {
        _conversations = conversations;
        _chat = chat;
        _instances = instances;
        _clipboard = clipboard;
        _uiActions = uiActions;
        _logger = logger;
        Dialogs = dialogs;
        Notifications = notifications;

        _chat.ConversationUpdated += (_, conversationId) =>
        {
            if (SelectedConversation?.Id == conversationId)
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                dispatcher?.BeginInvoke(() => _ = ReloadMessagesAsync());
            }
        };
        _instances.InstanceChanged += (_, _) =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            dispatcher?.BeginInvoke(() =>
            {
                RefreshInstanceOptions();
                _ = ReloadConversationsAsync();
            });
        };

        Messages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowStartState));
            UpdateSendState();
        };

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _elapsedTimer.Tick += (_, _) => UpdateGenerationStatus();
        RefreshInstanceOptions();
    }

    public ObservableCollection<ConversationItemViewModel> Conversations { get; } = new();

    public ObservableCollection<MessageViewModel> Messages { get; } = new();

    public ObservableCollection<InstanceOption> InstanceOptions { get; } = new();

    public IReadOnlyList<string> Suggestions { get; } = DefaultSuggestions;

    /// <summary>视图层聚焦输入框（建议词填入、发送完成后）。</summary>
    public event Action? InputFocusRequested;

    public bool ShowConversationEmptyState => SelectedConversation is null;

    public bool ShowStartState => SelectedConversation is not null && Messages.Count == 0;

    public bool ShowNoInstanceHint => SelectedInstance is not { IsReady: true };

    public Task OnActivatedAsync() => ReloadAsync();

    public async Task ReloadAsync()
    {
        await ReloadConversationsAsync();
        RefreshInstanceOptions();

        if (SelectedConversation is null && Conversations.Count > 0)
        {
            SelectedConversation = Conversations[0];
        }
        else if (Conversations.Count == 0)
        {
            Messages.Clear();
        }

        UpdateSendState();
    }

    private async Task ReloadConversationsAsync()
    {
        var selectedId = SelectedConversation?.Id;
        var conversations = await _conversations.GetAllAsync();
        Conversations.Clear();
        foreach (var conversation in conversations)
        {
            Conversations.Add(new ConversationItemViewModel(conversation));
        }

        ApplyFilter();

        if (selectedId is { } id)
        {
            SelectedConversation = Conversations.FirstOrDefault(c => c.Id == id);
        }
    }

    /// <summary>从实例页跳转进入：准备目标实例并选中/创建会话。</summary>
    public async Task PrepareForInstanceAsync(Guid instanceId)
    {
        await ReloadAsync();
        var option = InstanceOptions.FirstOrDefault(o => o.InstanceId == instanceId);
        if (option is not null)
        {
            SelectedInstance = option;
        }

        var conversation = Conversations.FirstOrDefault(c => c.Conversation.InstanceId == instanceId);
        if (conversation is not null)
        {
            SelectedConversation = conversation;
        }
        else if (Conversations.Count == 0)
        {
            await NewConversationAsync();
        }
    }

    [RelayCommand]
    private async Task NewConversationAsync()
    {
        var instanceName = SelectedInstance?.DisplayName ?? _instances.GetInstances().FirstOrDefault(i => i.State == InstanceState.Ready)?.Name;
        var conversation = await _conversations.CreateAsync(
            SelectedInstance?.InstanceId,
            instanceName,
            null);
        await ReloadConversationsAsync();
        SelectedConversation = Conversations.FirstOrDefault(c => c.Id == conversation.Id);
        InputFocusRequested?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RenameConversationAsync()
    {
        if (SelectedConversation is not { } item)
        {
            return;
        }

        var name = await Dialogs!.PromptAsync("重命名会话", "新的会话标题：", item.Title);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await _conversations.RenameAsync(item.Id, name);
        await ReloadConversationsAsync();
        SelectedConversation?.Refresh();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ClearConversationAsync()
    {
        if (SelectedConversation is not { } item)
        {
            return;
        }

        var confirmed = await Dialogs!.ConfirmAsync(new ConfirmDialogOptions
        {
            Title = "清空会话消息",
            Message = $"将清空会话“{item.Title}”中的所有消息（保留会话本身）。此操作不可撤销。",
            PrimaryButtonText = "清空消息",
            SecondaryButtonText = "取消",
            IsDestructive = true,
        });
        if (!confirmed)
        {
            return;
        }

        await _conversations.ClearMessagesAsync(item.Id);
        await ReloadMessagesAsync();
        Notifications?.Notify("会话消息已清空。");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteConversationAsync()
    {
        if (SelectedConversation is not { } item)
        {
            return;
        }

        var confirmed = await Dialogs!.ConfirmAsync(new ConfirmDialogOptions
        {
            Title = "删除会话",
            Message = $"将删除会话“{item.Title}”及其全部消息。此操作不可撤销。",
            PrimaryButtonText = "删除会话",
            SecondaryButtonText = "取消",
            IsDestructive = true,
        });
        if (!confirmed)
        {
            return;
        }

        await _conversations.DeleteAsync(item.Id);
        SelectedConversation = null;
        await ReloadAsync();
        Notifications?.Notify("会话已删除。");
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportConversationAsync()
    {
        if (SelectedConversation is not { } item)
        {
            return;
        }

        var messages = await _conversations.GetMessagesAsync(item.Id);
        var markdown = ConversationExporter.Build(item.Conversation, messages, DateTimeOffset.Now);
        var path = Dialogs!.PickSaveFile(
            "导出会话",
            "Markdown 文件 (*.md)|*.md|文本文件 (*.txt)|*.txt",
            ConversationExporter.SuggestFileName(item.Title),
            null);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, markdown, new UTF8Encoding(false));
            Notifications?.Notify($"会话已导出：{path}");
        }
        catch (Exception ex)
        {
            Notifications?.Notify("导出失败：" + ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        if (SelectedInstance is not { } instance)
        {
            return;
        }

        IsTestingConnection = true;
        UpdateSendState();
        try
        {
            var (success, message) = await _chat.TestConnectionAsync(instance.InstanceId);
            Notifications?.Notify((success ? "对话测试通过：" : "对话测试失败：") + message);
        }
        catch (Exception ex)
        {
            Notifications?.Notify("对话测试失败：" + ex.Message);
        }
        finally
        {
            IsTestingConnection = false;
            UpdateSendState();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (SelectedConversation is not { } item)
        {
            await NewConversationAsync();
            if (SelectedConversation is null)
            {
                return;
            }

            item = SelectedConversation;
        }

        if (SelectedInstance is not { } instance)
        {
            Notifications?.Notify("请先选择一个运行中的实例。");
            return;
        }

        var text = InputText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        InputText = string.Empty;
        BeginGeneration();
        try
        {
            var outcome = await _chat.SendAsync(item.Id, instance.InstanceId, text);
            if (!outcome.Success && outcome.Status == MessageStatus.Failed)
            {
                // 失败时保留输入便于重试
                if (string.IsNullOrEmpty(InputText))
                {
                    InputText = text;
                }

                if (outcome.Message is { Length: > 0 })
                {
                    Notifications?.Notify("发送失败：" + outcome.Message);
                }
            }
            else if (outcome.Status == MessageStatus.Interrupted)
            {
                Notifications?.Notify("已停止生成（已保留部分内容）。");
            }
        }
        finally
        {
            await EndGenerationAsync(item.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRegenerate))]
    private async Task RegenerateAsync()
    {
        if (SelectedConversation is not { } item || SelectedInstance is not { } instance)
        {
            return;
        }

        BeginGeneration();
        try
        {
            var outcome = await _chat.RegenerateAsync(item.Id, instance.InstanceId);
            if (!outcome.Success && outcome.Status == MessageStatus.Failed)
            {
                if (outcome.Message is { Length: > 0 })
                {
                    Notifications?.Notify("重新生成失败：" + outcome.Message);
                }
            }
            else if (outcome.Status == MessageStatus.Interrupted)
            {
                Notifications?.Notify("已停止生成（已保留部分内容）。");
            }
        }
        finally
        {
            await EndGenerationAsync(item.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void StopGeneration()
    {
        if (SelectedConversation is { } item)
        {
            _chat.Cancel(item.Id);
        }
    }

    [RelayCommand]
    private void ToggleParameters() => IsParametersExpanded = !IsParametersExpanded;

    [RelayCommand]
    private void CloseParameters() => IsParametersExpanded = false;

    [RelayCommand]
    private void NavigateToInstances() => _uiActions.NavigateToInstances();

    [RelayCommand]
    private void UseSuggestion(string? suggestion)
    {
        if (string.IsNullOrWhiteSpace(suggestion))
        {
            return;
        }

        InputText = suggestion;
        InputFocusRequested?.Invoke();
    }

    [RelayCommand]
    private async Task CopyMessageAsync(MessageViewModel? message)
    {
        if (message is null)
        {
            return;
        }

        var (success, error) = await _clipboard.SetTextAsync(message.MarkdownContent);
        Notifications?.Notify(success ? "消息已复制。" : error ?? "复制失败。");
    }

    [RelayCommand(CanExecute = nameof(CanDeleteMessage))]
    private async Task DeleteMessageAsync(MessageViewModel? message)
    {
        if (SelectedConversation is not { } item || message is null)
        {
            return;
        }

        var confirmed = await Dialogs!.ConfirmAsync(new ConfirmDialogOptions
        {
            Title = "删除消息",
            Message = $"将删除这条 {message.RoleText} 消息（{message.TimeText}）。此操作不可撤销。",
            PrimaryButtonText = "删除消息",
            SecondaryButtonText = "取消",
            IsDestructive = true,
        });
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _conversations.DeleteMessageAsync(item.Id, message.Id);
            await ReloadMessagesAsync();
            Notifications?.Notify("消息已删除。");
        }
        catch (InvalidOperationException ex)
        {
            Notifications?.Notify(ex.Message);
        }
    }

    [RelayCommand]
    private async Task SaveParametersAsync()
    {
        if (SelectedConversation is not { } item)
        {
            return;
        }

        var parameters = BuildParameters(out var parseError);
        if (parseError is not null)
        {
            ParameterStatus = parseError;
            return;
        }

        var issues = ChatParametersValidator.Validate(parameters);
        if (issues.Any(i => i.IsBlocking))
        {
            ParameterStatus = string.Join("；", issues.Where(i => i.IsBlocking).Select(i => i.Message));
            return;
        }

        await _conversations.UpdateSettingsAsync(item.Id, SystemPromptText, parameters);
        ParameterStatus = "参数已保存（新消息生效）。";
        Notifications?.Notify("聊天参数已保存。");
    }

    [RelayCommand]
    private void ResetParameters()
    {
        var defaults = ChatParameters.CreateDefaults();
        TemperatureText = defaults.Temperature?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        TopPText = defaults.TopP?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        TopKText = string.Empty;
        MinPText = string.Empty;
        RepeatPenaltyText = string.Empty;
        SeedText = string.Empty;
        MaxTokensText = string.Empty;
        StopSequencesText = string.Empty;
        ParameterStatus = "已恢复默认采样参数（系统提示词保持不变）。";
    }

    partial void OnSelectedConversationChanged(ConversationItemViewModel? value)
    {
        _ = LoadConversationSettingsAsync(value);
        _ = ReloadMessagesAsync();
        OnPropertyChanged(nameof(ShowConversationEmptyState));
        OnPropertyChanged(nameof(ShowStartState));
        UpdateSendState();
    }

    partial void OnSelectedInstanceChanged(InstanceOption? value)
    {
        UpdateConnectionState();
        OnPropertyChanged(nameof(ShowNoInstanceHint));
        UpdateSendState();
    }

    partial void OnInputTextChanged(string value) => UpdateSendState();

    partial void OnIsGeneratingChanged(bool value) => UpdateSendState();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = SearchText?.Trim() ?? string.Empty;
        var visible = 0;
        foreach (var item in Conversations)
        {
            if (item.ApplyFilter(query))
            {
                visible++;
            }
        }

        HasSearchResults = query.Length == 0 || visible > 0;
    }

    private async Task LoadConversationSettingsAsync(ConversationItemViewModel? item)
    {
        if (item is null)
        {
            SystemPromptText = string.Empty;
            return;
        }

        var conversation = await _conversations.GetAsync(item.Id);
        if (conversation is null)
        {
            return;
        }

        var parameters = conversation.Parameters;
        TemperatureText = parameters.Temperature?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        TopPText = parameters.TopP?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        TopKText = parameters.TopK?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        MinPText = parameters.MinP?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        RepeatPenaltyText = parameters.RepeatPenalty?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        SeedText = parameters.Seed?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        MaxTokensText = parameters.MaxTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        StopSequencesText = string.Join("\n", parameters.StopSequences);
        SystemPromptText = conversation.SystemPrompt ?? string.Empty;
        ParameterStatus = string.Empty;
    }

    public async Task ReloadMessagesAsync()
    {
        if (SelectedConversation is not { } item)
        {
            Messages.Clear();
            return;
        }

        var messages = await _conversations.GetMessagesAsync(item.Id);

        // 原位同步：只更新变化的项，保持滚动位置与渲染开销最小
        var existing = Messages.ToDictionary(m => m.Id);
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (existing.TryGetValue(message.Id, out var viewModel))
            {
                viewModel.UpdateFrom(message);
                var currentIndex = Messages.IndexOf(viewModel);
                if (currentIndex != i)
                {
                    Messages.Move(currentIndex, i);
                }
            }
            else
            {
                Messages.Insert(i, new MessageViewModel(message));
            }
        }

        for (var i = Messages.Count - 1; i >= messages.Count; i--)
        {
            Messages.RemoveAt(i);
        }

        MarkLastAssistantMessage();

        if (IsGenerating)
        {
            UpdateGenerationStatus();
        }
    }

    private void MarkLastAssistantMessage()
    {
        MessageViewModel? lastAssistant = null;
        foreach (var message in Messages)
        {
            if (message.IsAssistant)
            {
                lastAssistant = message;
            }
        }

        foreach (var message in Messages)
        {
            message.IsLastAssistant = ReferenceEquals(message, lastAssistant);
        }
    }

    private void BeginGeneration()
    {
        IsGenerating = true;
        _generationStart = DateTimeOffset.UtcNow;
        UpdateGenerationStatus();
        _elapsedTimer.Start();
    }

    private async Task EndGenerationAsync(Guid conversationId)
    {
        _elapsedTimer.Stop();
        IsGenerating = false;
        GenerationStatus = string.Empty;
        _generationStart = null;
        if (SelectedConversation?.Id == conversationId)
        {
            await ReloadMessagesAsync();
        }

        UpdateSendState();
        InputFocusRequested?.Invoke();
    }

    private ChatParameters BuildParameters(out string? parseError)
    {
        var errors = new List<string>();
        var parameters = new ChatParameters();

        double? ParseDouble(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            errors.Add($"无法解析数值：“{text}”。");
            return null;
        }

        int? ParseInt(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            errors.Add($"无法解析整数：“{text}”。");
            return null;
        }

        parameters.Temperature = ParseDouble(TemperatureText);
        parameters.TopP = ParseDouble(TopPText);
        parameters.TopK = ParseInt(TopKText);
        parameters.MinP = ParseDouble(MinPText);
        parameters.RepeatPenalty = ParseDouble(RepeatPenaltyText);
        if (!string.IsNullOrWhiteSpace(SeedText))
        {
            if (long.TryParse(SeedText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed))
            {
                parameters.Seed = seed;
            }
            else
            {
                errors.Add("无法解析随机种子。");
            }
        }

        parameters.MaxTokens = ParseInt(MaxTokensText);
        parameters.StopSequences = StopSequencesText
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        parseError = errors.FirstOrDefault();
        return parameters;
    }

    private void RefreshInstanceOptions()
    {
        var selectedId = SelectedInstance?.InstanceId;
        InstanceOptions.Clear();
        foreach (var instance in _instances.GetInstances())
        {
            InstanceOptions.Add(new InstanceOption(instance));
        }

        SelectedInstance = selectedId is { } id
            ? InstanceOptions.FirstOrDefault(o => o.InstanceId == id)
            : InstanceOptions.FirstOrDefault(o => o.IsReady) ?? InstanceOptions.FirstOrDefault();

        UpdateConnectionState();
        UpdateSendState();
    }

    private void UpdateConnectionState()
    {
        ConnectionState = SelectedInstance?.State;
        if (SelectedInstance is null)
        {
            ConnectionStateText = "无可用实例";
            ConnectionDetailText = "请先在“服务实例”页启动一个实例。";
        }
        else if (SelectedInstance.IsReady)
        {
            ConnectionStateText = "已连接";
            ConnectionDetailText = $"{SelectedInstance.DisplayName} · {SelectedInstance.Endpoint}";
        }
        else
        {
            ConnectionStateText = SelectedInstance.StateText;
            ConnectionDetailText = $"“{SelectedInstance.DisplayName}”就绪后才能发送。";
        }
    }

    private void UpdateGenerationStatus()
    {
        if (_generationStart is null)
        {
            GenerationStatus = string.Empty;
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - _generationStart.Value;
        var chars = Messages.LastOrDefault(m => m.IsAssistant)?.MarkdownContent.Length ?? 0;
        GenerationStatus = $"正在生成… 已 {elapsed.TotalSeconds:0} 秒 · 约 {chars} 字符";
    }

    private void UpdateSendState()
    {
        SendCommand.NotifyCanExecuteChanged();
        StopGenerationCommand.NotifyCanExecuteChanged();
        RegenerateCommand.NotifyCanExecuteChanged();
        DeleteMessageCommand.NotifyCanExecuteChanged();
        RenameConversationCommand.NotifyCanExecuteChanged();
        ClearConversationCommand.NotifyCanExecuteChanged();
        DeleteConversationCommand.NotifyCanExecuteChanged();
        ExportConversationCommand.NotifyCanExecuteChanged();
        TestConnectionCommand.NotifyCanExecuteChanged();
    }

    private bool HasSelection() => SelectedConversation is not null;

    private bool CanExport() => !IsGenerating && SelectedConversation is not null;

    private bool CanSend() => !IsGenerating
        && SelectedInstance is { IsReady: true }
        && !string.IsNullOrWhiteSpace(InputText);

    private bool CanStop() => IsGenerating;

    private bool CanRegenerate() => !IsGenerating
        && SelectedInstance is { IsReady: true }
        && SelectedConversation is not null
        && Messages.LastOrDefault() is { IsAssistant: true };

    private bool CanDeleteMessage(MessageViewModel? message)
        => !IsGenerating && message is { CanDelete: true } && SelectedConversation is not null;

    private bool CanTestConnection() => !IsTestingConnection && SelectedInstance is { IsReady: true };

    public void Dispose() => _elapsedTimer.Stop();
}

/// <summary>聊天目标实例选项。</summary>
public sealed class InstanceOption
{
    public InstanceOption(ManagedInstance instance)
    {
        InstanceId = instance.Id;
        DisplayName = instance.Name;
        Endpoint = instance.LocalBaseUrl;
        State = instance.State;
    }

    public Guid InstanceId { get; }

    public string DisplayName { get; }

    public string Endpoint { get; }

    public InstanceState State { get; }

    public bool IsReady => State == InstanceState.Ready;

    public string StateText => Formatting.InstanceStateText(State);

    public string DisplayText => $"{DisplayName}（{StateText}）· {Endpoint}";
}
