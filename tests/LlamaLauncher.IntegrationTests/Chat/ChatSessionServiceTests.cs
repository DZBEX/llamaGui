using LlamaLauncher.Application.Chat;
using LlamaLauncher.Application.Instances;
using LlamaLauncher.Domain;
using LlamaLauncher.IntegrationTests.TestSupport;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Chat;

public sealed class ChatSessionServiceTests
{
    private static async Task<ManagedInstance> StartReadyInstanceAsync(
        IntegrationFixture fixture,
        Dictionary<string, string?> environment,
        Action<ServerLaunchOptions>? configure = null)
    {
        foreach (var (name, value) in environment)
        {
            fixture.SetEnvironment(name, value);
        }

        fixture.SetEnvironment("LLAMALAUNCHER_TEST_ENABLE_HTTP", "1");
        var modelPath = fixture.CreateModelFile("chat-model.gguf");
        var installation = await fixture.InstallationService.RegisterExternalAsync(IntegrationFixture.ChildExecutablePath);
        var now = fixture.TimeProvider.GetUtcNow();
        var profile = new LaunchProfile
        {
            Name = "聊天配置-" + Guid.NewGuid().ToString("N")[..6],
            InstallationId = installation.Id,
            Options = new ServerLaunchOptions
            {
                ModelPath = modelPath,
                Host = "127.0.0.1",
                Port = IntegrationFixture.GetFreePort(),
                ContextSize = 512,
                StartupTimeoutSeconds = 30,
                StopGraceSeconds = 1,
                Alias = "test-alias",
            },
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        configure?.Invoke(profile.Options);
        await fixture.Profiles.UpsertAsync(profile);

        var instance = await fixture.Manager.CreateAsync("聊天实例", profile.Id);
        var result = await fixture.Manager.StartAsync(instance.Id, waitForReady: true);
        Assert.True(result.Success, result.Message);
        Assert.Equal(InstanceState.Ready, instance.State);
        return instance;
    }

    private static void ClearEnvironment()
    {
        foreach (var name in new[]
                 {
                     "LLAMALAUNCHER_TEST_ENABLE_HTTP",
                     "LLAMALAUNCHER_TEST_CHAT_MODE",
                     "LLAMALAUNCHER_TEST_CHAT_TEXT",
                     "LLAMALAUNCHER_TEST_CHAT_CHUNK_DELAY_MS",
                 })
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task Send_PersistsUserAndAssistantMessagesWithMetrics()
    {
        using var fixture = new IntegrationFixture();
        try
        {
            var instance = await StartReadyInstanceAsync(fixture, new Dictionary<string, string?>
            {
                ["LLAMALAUNCHER_TEST_CHAT_TEXT"] = "完整回复内容：一二三四五。",
                ["LLAMALAUNCHER_TEST_CHAT_CHUNK_DELAY_MS"] = "5",
            });
            var conversation = await fixture.ConversationsService.CreateAsync(
                instance.Id,
                "test-alias",
                "你是一个测试助手。");

            var outcome = await fixture.ChatService.SendAsync(conversation.Id, instance.Id, "你好，请回复。");

            Assert.True(outcome.Success, outcome.Message);
            Assert.Equal(MessageStatus.Completed, outcome.Status);

            var messages = await fixture.ConversationsService.GetMessagesAsync(conversation.Id);
            Assert.Equal(2, messages.Count);
            Assert.Equal(ChatRole.User, messages[0].Role);
            Assert.Equal("你好，请回复。", messages[0].Content);
            Assert.Equal(MessageStatus.Completed, messages[0].Status);
            Assert.Equal(ChatRole.Assistant, messages[1].Role);
            Assert.Equal("完整回复内容：一二三四五。", messages[1].Content);
            Assert.Equal(MessageStatus.Completed, messages[1].Status);
            Assert.NotNull(messages[1].Metrics);
            Assert.True(messages[1].Metrics!.ElapsedMs >= 0);

            // 会话标题自动命名
            var reloaded = await fixture.ConversationsService.GetAsync(conversation.Id);
            Assert.Equal("你好，请回复。", reloaded!.Title);
        }
        finally
        {
            ClearEnvironment();
        }
    }

    [Fact]
    public async Task Send_SecondMessage_IncludesHistory()
    {
        using var fixture = new IntegrationFixture();
        try
        {
            var instance = await StartReadyInstanceAsync(fixture, new Dictionary<string, string?>
            {
                ["LLAMALAUNCHER_TEST_CHAT_TEXT"] = "回复。",
            });
            var conversation = await fixture.ConversationsService.CreateAsync(instance.Id, "test-alias", null);

            await fixture.ChatService.SendAsync(conversation.Id, instance.Id, "第一问");
            await fixture.ChatService.SendAsync(conversation.Id, instance.Id, "第二问");

            var messages = await fixture.ConversationsService.GetMessagesAsync(conversation.Id);
            Assert.Equal(4, messages.Count);
            Assert.Equal(new long[] { 0, 1, 2, 3 }, messages.Select(m => m.Sequence).ToArray());

            // BuildTurns：包含系统提示词（应为空）与全部已完成历史
            var reloaded = await fixture.ConversationsService.GetAsync(conversation.Id);
            var turns = fixture.ConversationsService.BuildTurns(reloaded!, messages);
            Assert.Equal(4, turns.Count);
            Assert.Equal(ChatRole.User, turns[0].Role);
            Assert.Equal("第一问", turns[0].Content);
        }
        finally
        {
            ClearEnvironment();
        }
    }

    [Fact]
    public async Task Cancel_InterruptsAndKeepsPartialContent()
    {
        using var fixture = new IntegrationFixture();
        try
        {
            var instance = await StartReadyInstanceAsync(fixture, new Dictionary<string, string?>
            {
                ["LLAMALAUNCHER_TEST_CHAT_TEXT"] = "这是一段较长的回复内容，用于测试停止生成时保存部分内容的行为。",
                ["LLAMALAUNCHER_TEST_CHAT_CHUNK_DELAY_MS"] = "120",
            });
            var conversation = await fixture.ConversationsService.CreateAsync(instance.Id, "test-alias", null);

            var sendTask = fixture.ChatService.SendAsync(conversation.Id, instance.Id, "开始生成");
            // 等生成开始并有部分内容
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!fixture.ChatService.IsGenerating(conversation.Id) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.True(fixture.ChatService.IsGenerating(conversation.Id), "生成未开始。");
            await Task.Delay(400);
            fixture.ChatService.Cancel(conversation.Id);

            var outcome = await sendTask;

            Assert.Equal(MessageStatus.Interrupted, outcome.Status);
            var messages = await fixture.ConversationsService.GetMessagesAsync(conversation.Id);
            var assistant = messages.Single(m => m.Role == ChatRole.Assistant);
            Assert.Equal(MessageStatus.Interrupted, assistant.Status);
            Assert.False(string.IsNullOrEmpty(assistant.Content), "中断时应保留已生成内容。");
            Assert.NotNull(assistant.CompletedUtc);
            Assert.Contains("停止", assistant.ErrorText);

            // 取消后可以继续发送
            Assert.False(fixture.ChatService.IsGenerating(conversation.Id));
        }
        finally
        {
            ClearEnvironment();
        }
    }

    [Fact]
    public async Task Send_Unauthorized_MarksAssistantFailedAndKeepsUserMessage()
    {
        using var fixture = new IntegrationFixture();
        try
        {
            var instance = await StartReadyInstanceAsync(fixture, new Dictionary<string, string?>
            {
                ["LLAMALAUNCHER_TEST_CHAT_MODE"] = "401",
            });
            var conversation = await fixture.ConversationsService.CreateAsync(instance.Id, "test-alias", null);

            var outcome = await fixture.ChatService.SendAsync(conversation.Id, instance.Id, "需要认证的消息");

            Assert.False(outcome.Success);
            Assert.Equal(MessageStatus.Failed, outcome.Status);
            Assert.NotNull(outcome.Error);
            Assert.Equal(ChatErrorKind.Unauthorized, outcome.Error!.Kind);

            var messages = await fixture.ConversationsService.GetMessagesAsync(conversation.Id);
            Assert.Equal(2, messages.Count);
            Assert.Equal(MessageStatus.Completed, messages[0].Status);
            Assert.Equal(MessageStatus.Failed, messages[1].Status);
            Assert.Contains("API Key", messages[1].ErrorText);
        }
        finally
        {
            ClearEnvironment();
        }
    }

    [Fact]
    public async Task Regenerate_ReplacesTailAssistantAndKeepsUserMessage()
    {
        using var fixture = new IntegrationFixture();
        try
        {
            var instance = await StartReadyInstanceAsync(fixture, new Dictionary<string, string?>
            {
                ["LLAMALAUNCHER_TEST_CHAT_TEXT"] = "重新生成的回复。",
            });
            var conversation = await fixture.ConversationsService.CreateAsync(instance.Id, "test-alias", null);
            var now = fixture.TimeProvider.GetUtcNow();
            var userMessage = new ChatMessage
            {
                ConversationId = conversation.Id,
                Role = ChatRole.User,
                Content = "原始问题",
                Status = MessageStatus.Completed,
                Sequence = 0,
                CreatedUtc = now,
                CompletedUtc = now,
            };
            await fixture.Messages.AddAsync(userMessage);
            await fixture.Messages.AddAsync(new ChatMessage
            {
                ConversationId = conversation.Id,
                Role = ChatRole.Assistant,
                Content = string.Empty,
                Status = MessageStatus.Failed,
                Sequence = 1,
                CreatedUtc = now,
                ErrorText = "上一次生成失败。",
            });

            var outcome = await fixture.ChatService.RegenerateAsync(conversation.Id, instance.Id);

            Assert.True(outcome.Success, outcome.Message);
            var messages = await fixture.ConversationsService.GetMessagesAsync(conversation.Id);
            Assert.Equal(2, messages.Count);
            Assert.Equal(userMessage.Id, messages[0].Id);
            Assert.Equal("原始问题", messages[0].Content);
            Assert.Equal(ChatRole.Assistant, messages[1].Role);
            Assert.Equal("重新生成的回复。", messages[1].Content);
            Assert.Equal(MessageStatus.Completed, messages[1].Status);
            Assert.NotNull(messages[1].Metrics);
            Assert.Equal(new long[] { 0, 1 }, messages.Select(m => m.Sequence).ToArray());

            // 旧的失败占位消息已被清理
            Assert.DoesNotContain(messages, m => m.ErrorText == "上一次生成失败。");
        }
        finally
        {
            ClearEnvironment();
        }
    }

    [Fact]
    public async Task Regenerate_KeepsEarlierTurnsAndAppendsNewReply()
    {
        using var fixture = new IntegrationFixture();
        try
        {
            var instance = await StartReadyInstanceAsync(fixture, new Dictionary<string, string?>
            {
                ["LLAMALAUNCHER_TEST_CHAT_TEXT"] = "第二轮回复。",
            });
            var conversation = await fixture.ConversationsService.CreateAsync(instance.Id, "test-alias", null);
            var now = fixture.TimeProvider.GetUtcNow();
            await fixture.Messages.AddAsync(new ChatMessage
            {
                ConversationId = conversation.Id,
                Role = ChatRole.User,
                Content = "第一问",
                Status = MessageStatus.Completed,
                Sequence = 0,
                CreatedUtc = now,
                CompletedUtc = now,
            });
            await fixture.Messages.AddAsync(new ChatMessage
            {
                ConversationId = conversation.Id,
                Role = ChatRole.Assistant,
                Content = "第一答",
                Status = MessageStatus.Completed,
                Sequence = 1,
                CreatedUtc = now,
                CompletedUtc = now,
            });
            await fixture.Messages.AddAsync(new ChatMessage
            {
                ConversationId = conversation.Id,
                Role = ChatRole.User,
                Content = "第二问",
                Status = MessageStatus.Completed,
                Sequence = 2,
                CreatedUtc = now,
                CompletedUtc = now,
            });

            var outcome = await fixture.ChatService.RegenerateAsync(conversation.Id, instance.Id);

            Assert.True(outcome.Success, outcome.Message);
            var messages = await fixture.ConversationsService.GetMessagesAsync(conversation.Id);
            Assert.Equal(4, messages.Count);
            Assert.Equal("第一答", messages[1].Content);
            Assert.Equal("第二问", messages[2].Content);
            Assert.Equal("第二轮回复。", messages[3].Content);
        }
        finally
        {
            ClearEnvironment();
        }
    }

    [Fact]
    public async Task Regenerate_WithoutUserMessage_ReturnsFailure()
    {
        using var fixture = new IntegrationFixture();
        try
        {
            var instance = await StartReadyInstanceAsync(fixture, new Dictionary<string, string?>());
            var conversation = await fixture.ConversationsService.CreateAsync(instance.Id, "test-alias", null);

            var outcome = await fixture.ChatService.RegenerateAsync(conversation.Id, instance.Id);

            Assert.False(outcome.Success);
            Assert.Contains("用户消息", outcome.Message);
            Assert.Empty(await fixture.ConversationsService.GetMessagesAsync(conversation.Id));
        }
        finally
        {
            ClearEnvironment();
        }
    }

    [Fact]
    public async Task DeleteMessage_RemovesSingleMessageAndRejectsStreaming()
    {
        using var fixture = new IntegrationFixture();
        var now = fixture.TimeProvider.GetUtcNow();
        var conversation = await fixture.ConversationsService.CreateAsync(null, null, null);
        var question = new ChatMessage
        {
            ConversationId = conversation.Id,
            Role = ChatRole.User,
            Content = "问题",
            Status = MessageStatus.Completed,
            Sequence = 0,
            CreatedUtc = now,
            CompletedUtc = now,
        };
        var answer = new ChatMessage
        {
            ConversationId = conversation.Id,
            Role = ChatRole.Assistant,
            Content = "回答",
            Status = MessageStatus.Completed,
            Sequence = 1,
            CreatedUtc = now,
            CompletedUtc = now,
        };
        await fixture.Messages.AddAsync(question);
        await fixture.Messages.AddAsync(answer);

        await fixture.ConversationsService.DeleteMessageAsync(conversation.Id, answer.Id);

        var messages = await fixture.ConversationsService.GetMessagesAsync(conversation.Id);
        Assert.Single(messages);
        Assert.Equal(question.Id, messages[0].Id);

        // 会话 ID 不匹配时不删除
        var other = await fixture.ConversationsService.CreateAsync(null, null, null);
        await fixture.ConversationsService.DeleteMessageAsync(other.Id, question.Id);
        Assert.Single(await fixture.ConversationsService.GetMessagesAsync(conversation.Id));

        // 生成中的消息拒绝删除
        var streaming = new ChatMessage
        {
            ConversationId = conversation.Id,
            Role = ChatRole.Assistant,
            Content = "生成中",
            Status = MessageStatus.Streaming,
            Sequence = 2,
            CreatedUtc = now,
        };
        await fixture.Messages.AddAsync(streaming);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.ConversationsService.DeleteMessageAsync(conversation.Id, streaming.Id));
    }

    [Fact]
    public async Task Send_WhenInstanceNotReady_ReturnsFailureWithoutPersisting()
    {
        using var fixture = new IntegrationFixture();
        var now = fixture.TimeProvider.GetUtcNow();
        var profile = new LaunchProfile
        {
            Name = "未启动配置",
            Options = new ServerLaunchOptions { Port = IntegrationFixture.GetFreePort() },
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        await fixture.Profiles.UpsertAsync(profile);
        var instance = await fixture.Manager.CreateAsync("未启动实例", profile.Id);
        var conversation = await fixture.ConversationsService.CreateAsync(instance.Id, null, null);

        var outcome = await fixture.ChatService.SendAsync(conversation.Id, instance.Id, "你好");

        Assert.False(outcome.Success);
        Assert.Contains("未就绪", outcome.Message);
        Assert.Empty(await fixture.ConversationsService.GetMessagesAsync(conversation.Id));
    }

    [Fact]
    public async Task RecoverInterrupted_MarksStreamingMessages()
    {
        using var fixture = new IntegrationFixture();
        var now = fixture.TimeProvider.GetUtcNow();
        var conversation = await fixture.ConversationsService.CreateAsync(null, null, null);
        await fixture.Messages.AddAsync(new ChatMessage
        {
            ConversationId = conversation.Id,
            Role = ChatRole.Assistant,
            Content = "被中断的部分内容",
            Status = MessageStatus.Streaming,
            Sequence = 0,
            CreatedUtc = now,
        });

        var affected = await fixture.ConversationsService.RecoverInterruptedAsync();

        Assert.Equal(1, affected);
        var messages = await fixture.ConversationsService.GetMessagesAsync(conversation.Id);
        Assert.Equal(MessageStatus.Interrupted, messages[0].Status);
        Assert.Equal("被中断的部分内容", messages[0].Content);
    }

    [Fact]
    public void BuildTurns_IncludesSystemPromptFirstAndSkipsEmpty()
    {
        using var fixture = new IntegrationFixture();
        var conversation = new Conversation
        {
            SystemPrompt = "系统提示词",
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = ChatRole.User,
                Content = "问题一",
                Status = MessageStatus.Completed,
                Sequence = 0,
                CreatedUtc = DateTimeOffset.UtcNow,
            },
            new()
            {
                Role = ChatRole.Assistant,
                Content = string.Empty,
                Status = MessageStatus.Failed,
                Sequence = 1,
                CreatedUtc = DateTimeOffset.UtcNow,
            },
            new()
            {
                Role = ChatRole.Assistant,
                Content = "部分回复",
                Status = MessageStatus.Interrupted,
                Sequence = 2,
                CreatedUtc = DateTimeOffset.UtcNow,
            },
        };

        var turns = fixture.ConversationsService.BuildTurns(conversation, messages);

        Assert.Equal(3, turns.Count);
        Assert.Equal(ChatRole.System, turns[0].Role);
        Assert.Equal("系统提示词", turns[0].Content);
        Assert.Equal("部分回复", turns[2].Content);
    }
}
