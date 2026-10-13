using LlamaLauncher.App.ViewModels;
using LlamaLauncher.Domain;
using Xunit;

namespace LlamaLauncher.UiTests;

public sealed class ConversationExporterTests
{
    [Fact]
    public void SuggestFileName_SanitizesInvalidCharacters()
    {
        var name = ConversationExporter.SuggestFileName("a/b:c*d?e\"f<g>h|i");
        Assert.EndsWith(".md", name);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('*', name);
    }

    [Fact]
    public void SuggestFileName_FallsBackForEmptyTitle()
        => Assert.Equal("会话.md", ConversationExporter.SuggestFileName("   "));

    [Fact]
    public void Build_ContainsHeaderRolesAndMetrics()
    {
        var conversation = new Conversation
        {
            Title = "测试会话",
            SystemPrompt = "你是一个助手。",
            ModelNameSnapshot = "model-a",
            CreatedUtc = new DateTimeOffset(2026, 10, 13, 1, 0, 0, TimeSpan.Zero),
            UpdatedUtc = new DateTimeOffset(2026, 10, 13, 1, 0, 0, TimeSpan.Zero),
        };
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = ChatRole.User,
                Content = "你好",
                Status = MessageStatus.Completed,
                Sequence = 0,
                CreatedUtc = new DateTimeOffset(2026, 10, 13, 1, 1, 0, TimeSpan.Zero),
            },
            new()
            {
                Role = ChatRole.Assistant,
                Content = "## 你好\n\n- 条目一\n- 条目二",
                Status = MessageStatus.Interrupted,
                ErrorText = "已停止生成（用户取消）。",
                Sequence = 1,
                CreatedUtc = new DateTimeOffset(2026, 10, 13, 1, 2, 0, TimeSpan.Zero),
                Metrics = new ChatMetrics
                {
                    ElapsedMs = 3200,
                    CompletionTokens = 238,
                    TokensPerSecond = 74.9,
                    ModelNameSnapshot = "model-a",
                    InstanceNameSnapshot = "实例一",
                },
            },
        };

        var markdown = ConversationExporter.Build(
            conversation,
            messages,
            new DateTimeOffset(2026, 10, 13, 2, 0, 0, TimeSpan.Zero));

        Assert.Contains("# 测试会话", markdown);
        Assert.Contains("## 系统提示词", markdown);
        Assert.Contains("你是一个助手。", markdown);
        Assert.Contains("## 我 · ", markdown);
        Assert.Contains("## 助手 · ", markdown);
        Assert.Contains("你好", markdown);
        Assert.Contains("- 条目一", markdown);
        Assert.Contains("耗时 3.2 秒", markdown);
        Assert.Contains("生成 238 tokens", markdown);
        Assert.Contains("74.9 tok/s", markdown);
        Assert.Contains("状态：已中断", markdown);
        Assert.Contains("- 模型：model-a", markdown);
        Assert.Contains("- 实例：实例一", markdown);
    }
}
