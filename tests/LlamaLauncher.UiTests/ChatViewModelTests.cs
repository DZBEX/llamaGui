using System.IO;
using LlamaLauncher.App.Services;
using LlamaLauncher.App.ViewModels;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Chat;
using LlamaLauncher.Application.Instances;
using LlamaLauncher.Application.Services;
using LlamaLauncher.Domain;
using LlamaLauncher.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.UiTests;

/// <summary>
/// 聊天页关键命令的 UI 逻辑测试：使用真实 SQLite 会话服务 + 伪对话框，
/// 覆盖“删除单条消息”（确认/取消）与“导出会话为 Markdown”。
/// </summary>
public sealed class ChatViewModelTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteDatabase _database;
    private readonly SqliteMessageRepository _messagesRepository;
    private readonly ConversationService _conversationService;

    public ChatViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "LlamaLauncherChatVmTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _database = new SqliteDatabase(Path.Combine(_root, "test.db"), NullLogger<SqliteDatabase>.Instance);
        _database.InitializeAsync().GetAwaiter().GetResult();
        _messagesRepository = new SqliteMessageRepository(_database);
        _conversationService = new ConversationService(
            new SqliteConversationRepository(_database),
            _messagesRepository,
            TimeProvider.System,
            NullLogger<ConversationService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    [Fact]
    public async Task DeleteMessage_WhenConfirmed_RemovesOnlyThatMessage()
    {
        var conversation = await SeedConversationAsync();
        var notifications = new RecordingNotificationService();
        using var viewModel = CreateViewModel(new FakeDialogService { ConfirmResult = true }, notifications);
        await PrepareViewModelAsync(viewModel);

        Assert.Equal(2, viewModel.Messages.Count);
        var assistant = viewModel.Messages.Single(m => m.IsAssistant);
        await viewModel.DeleteMessageCommand.ExecuteAsync(assistant);

        var remaining = await _conversationService.GetMessagesAsync(conversation.Id);
        var message = Assert.Single(remaining);
        Assert.Equal(ChatRole.User, message.Role);
        Assert.Contains(notifications.Messages, m => m.Contains("消息已删除"));
    }

    [Fact]
    public async Task DeleteMessage_WhenCancelled_KeepsMessage()
    {
        var conversation = await SeedConversationAsync();
        var notifications = new RecordingNotificationService();
        using var viewModel = CreateViewModel(new FakeDialogService { ConfirmResult = false }, notifications);
        await PrepareViewModelAsync(viewModel);

        var assistant = viewModel.Messages.Single(m => m.IsAssistant);
        await viewModel.DeleteMessageCommand.ExecuteAsync(assistant);

        Assert.Equal(2, (await _conversationService.GetMessagesAsync(conversation.Id)).Count);
        Assert.DoesNotContain(notifications.Messages, m => m.Contains("消息已删除"));
    }

    [Fact]
    public async Task ExportConversation_WritesMarkdownFileWithContent()
    {
        await SeedConversationAsync();
        var exportPath = Path.Combine(_root, "导出-会话.md");
        var dialogs = new FakeDialogService { SaveFileResult = exportPath };
        using var viewModel = CreateViewModel(dialogs, new RecordingNotificationService());
        await PrepareViewModelAsync(viewModel);

        await viewModel.ExportConversationCommand.ExecuteAsync(null);

        Assert.True(File.Exists(exportPath), "导出文件未生成。");
        var markdown = await File.ReadAllTextAsync(exportPath);
        Assert.Contains("# 导出测试会话", markdown);
        Assert.Contains("## 我 · ", markdown);
        Assert.Contains("你好，请介绍一下你自己。", markdown);
        Assert.Contains("这是一条测试回复。", markdown);
        Assert.Contains("- 导出时间：", markdown);
    }

    private async Task<Conversation> SeedConversationAsync()
    {
        var conversation = await _conversationService.CreateAsync(null, null, null);
        await _conversationService.RenameAsync(conversation.Id, "导出测试会话");
        var now = DateTimeOffset.UtcNow;
        await _messagesRepository.AddAsync(new ChatMessage
        {
            ConversationId = conversation.Id,
            Role = ChatRole.User,
            Content = "你好，请介绍一下你自己。",
            Status = MessageStatus.Completed,
            Sequence = 0,
            CreatedUtc = now,
            CompletedUtc = now,
        });
        await _messagesRepository.AddAsync(new ChatMessage
        {
            ConversationId = conversation.Id,
            Role = ChatRole.Assistant,
            Content = "这是一条测试回复。",
            Status = MessageStatus.Completed,
            Sequence = 1,
            CreatedUtc = now,
            CompletedUtc = now,
        });
        return conversation;
    }

    private static async Task PrepareViewModelAsync(ChatViewModel viewModel)
    {
        await viewModel.ReloadAsync();
        await viewModel.ReloadMessagesAsync();
    }

    private ChatViewModel CreateViewModel(IDialogService dialogs, INotificationService notifications)
        => new(
            _conversationService,
            new FakeChatSessionService(),
            new FakeInstanceManager(),
            new FakeClipboardService(),
            dialogs,
            notifications,
            new NullUiActions(),
            NullLogger<ChatViewModel>.Instance);

    private sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;

        public string? SaveFileResult { get; set; }

        public Task<bool> ConfirmAsync(ConfirmDialogOptions options) => Task.FromResult(ConfirmResult);

        public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;

        public Task ShowWarningAsync(string title, string message) => Task.CompletedTask;

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public Task<string?> PromptAsync(string title, string message, string initialValue = "")
            => Task.FromResult<string?>(null);

        public Task<string?> PromptWithConfirmationAsync(string title, string message, string requiredConfirmationKeyword)
            => Task.FromResult<string?>(null);

        public Task<(bool Confirmed, bool CheckboxChecked)> ConfirmWithCheckboxAsync(
            ConfirmDialogOptions options,
            string checkboxText,
            bool defaultChecked = false)
            => Task.FromResult((false, false));

        public string? PickOpenFile(string title, string filter, string? initialDirectory = null) => null;

        public string? PickFolder(string title, string? initialDirectory = null) => null;

        public string? PickSaveFile(string title, string filter, string? defaultFileName = null, string? initialDirectory = null)
            => SaveFileResult;
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<string> Messages { get; } = new();

        public event Action<string?>? StatusChanged;

        public void SetStatus(string message) => Messages.Add(message);

        public void ClearStatus() => StatusChanged?.Invoke(null);

        public void Notify(string message) => Messages.Add(message);
    }

    private sealed class FakeClipboardService : IClipboardService
    {
        public Task<(bool Success, string? Error)> SetTextAsync(string text) => Task.FromResult((true, (string?)null));
    }

    private sealed class FakeChatSessionService : IChatSessionService
    {
        public event EventHandler<Guid>? ConversationUpdated;

        public bool IsGenerating(Guid conversationId) => false;

        public Task<ChatSendOutcome> SendAsync(
            Guid conversationId,
            Guid instanceId,
            string userText,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ChatSendOutcome> RegenerateAsync(
            Guid conversationId,
            Guid instanceId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Cancel(Guid conversationId)
        {
        }

        public Task<(bool Success, string Message)> TestConnectionAsync(
            Guid instanceId,
            CancellationToken cancellationToken = default)
            => Task.FromResult((true, "ok"));

        public void RaiseConversationUpdated(Guid conversationId)
            => ConversationUpdated?.Invoke(this, conversationId);
    }

    private sealed class FakeInstanceManager : IInstanceManager
    {
        public event EventHandler<Guid>? InstanceChanged;

        public Task<RecoveryReport> InitializeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new RecoveryReport(0, 0, Array.Empty<string>()));

        public IReadOnlyList<ManagedInstance> GetInstances() => Array.Empty<ManagedInstance>();

        public ManagedInstance? Get(Guid instanceId) => null;

        public Task<ManagedInstance> CreateAsync(string name, Guid profileId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<StartResult> StartAsync(
            Guid instanceId,
            bool waitForReady = false,
            bool lanAccessConfirmed = false,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task StopAsync(Guid instanceId, bool force = false, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<StartResult> RestartAsync(Guid instanceId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RemoveAsync(Guid instanceId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task StopAllAsync(bool force = false, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<string?> GetInstanceApiKeyAsync(Guid instanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public void RaiseInstanceChanged(Guid instanceId)
            => InstanceChanged?.Invoke(this, instanceId);
    }
}
