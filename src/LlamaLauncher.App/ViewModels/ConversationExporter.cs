using System.IO;
using System.Text;
using LlamaLauncher.Domain;

namespace LlamaLauncher.App.ViewModels;

/// <summary>把会话导出为 Markdown（纯函数，便于测试；不包含任何密钥）。</summary>
public static class ConversationExporter
{
    public static string SuggestFileName(string title)
    {
        var builder = new StringBuilder();
        foreach (var ch in title.Trim())
        {
            builder.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
        }

        var name = builder.ToString().Trim(' ', '.', '_');
        if (name.Length == 0)
        {
            name = "会话";
        }

        if (name.Length > 60)
        {
            name = name[..60];
        }

        return name + ".md";
    }

    public static string Build(Conversation conversation, IReadOnlyList<ChatMessage> messages, DateTimeOffset exportedAt)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(messages);

        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(conversation.Title);
        builder.AppendLine();
        builder.Append("- 导出时间：").AppendLine(exportedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        builder.Append("- 创建时间：").AppendLine(conversation.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        builder.Append("- 消息数量：").Append(messages.Count).AppendLine();
        var model = messages
            .Select(m => m.Metrics?.ModelNameSnapshot)
            .LastOrDefault(m => !string.IsNullOrWhiteSpace(m)) ?? conversation.ModelNameSnapshot;
        if (!string.IsNullOrWhiteSpace(model))
        {
            builder.Append("- 模型：").AppendLine(model);
        }

        var instance = messages
            .Select(m => m.Metrics?.InstanceNameSnapshot)
            .LastOrDefault(m => !string.IsNullOrWhiteSpace(m));
        if (!string.IsNullOrWhiteSpace(instance))
        {
            builder.Append("- 实例：").AppendLine(instance);
        }

        if (!string.IsNullOrWhiteSpace(conversation.SystemPrompt))
        {
            builder.AppendLine();
            builder.AppendLine("## 系统提示词");
            builder.AppendLine();
            builder.AppendLine(conversation.SystemPrompt);
        }

        foreach (var message in messages.OrderBy(m => m.Sequence))
        {
            builder.AppendLine();
            builder.AppendLine("---");
            builder.AppendLine();
            builder.Append("## ").Append(RoleText(message.Role)).Append(" · ")
                .AppendLine(message.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            builder.AppendLine();
            builder.AppendLine(string.IsNullOrEmpty(message.Content) ? "（无内容）" : message.Content);

            var details = BuildMessageDetails(message);
            if (details.Count > 0)
            {
                builder.AppendLine();
                foreach (var detail in details)
                {
                    builder.Append("> ").AppendLine(detail);
                }
            }
        }

        return builder.ToString();
    }

    private static string RoleText(ChatRole role) => role switch
    {
        ChatRole.User => "我",
        ChatRole.Assistant => "助手",
        _ => "系统",
    };

    private static List<string> BuildMessageDetails(ChatMessage message)
    {
        var details = new List<string>();
        var metrics = message.Metrics;
        var parts = new List<string>();
        if (metrics?.ElapsedMs is { } elapsed)
        {
            parts.Add($"耗时 {elapsed / 1000.0:0.0} 秒");
        }

        if (metrics?.CompletionTokens is { } completion)
        {
            parts.Add($"生成 {completion} tokens");
        }

        if (metrics?.TokensPerSecond is { } speed)
        {
            parts.Add($"{speed:0.#} tok/s");
        }

        if (parts.Count > 0)
        {
            details.Add(string.Join(" · ", parts));
        }

        switch (message.Status)
        {
            case MessageStatus.Interrupted:
                details.Add("状态：已中断");
                break;
            case MessageStatus.Failed:
                details.Add("状态：失败" + (string.IsNullOrWhiteSpace(message.ErrorText) ? string.Empty : "：" + message.ErrorText));
                break;
        }

        if (message.Status is MessageStatus.Interrupted && !string.IsNullOrWhiteSpace(message.ErrorText))
        {
            details.Add(message.ErrorText);
        }

        return details;
    }
}
