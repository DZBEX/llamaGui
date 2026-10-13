using System.Diagnostics;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Domain;
using LlamaLauncher.IntegrationTests.TestSupport;
using LlamaLauncher.Infrastructure.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Chat;

public sealed class StreamingChatClientTests
{
    internal sealed class ChatMockServer : IDisposable
    {
        private readonly string? _originalEnable;
        private readonly Process _process;
        private readonly Dictionary<string, string?> _environment;

        public ChatMockServer(Dictionary<string, string?> environment)
        {
            Port = IntegrationFixture.GetFreePort();
            _originalEnable = Environment.GetEnvironmentVariable("LLAMALAUNCHER_TEST_ENABLE_HTTP");
            _environment = environment;
            Environment.SetEnvironmentVariable("LLAMALAUNCHER_TEST_ENABLE_HTTP", "1");
            foreach (var (name, value) in environment)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = IntegrationFixture.ChildExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            startInfo.ArgumentList.Add("--host");
            startInfo.ArgumentList.Add("127.0.0.1");
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(Port.ToString());
            _process = Process.Start(startInfo)!;
            WaitForListening();
        }

        public int Port { get; }

        public string BaseUrl => $"http://127.0.0.1:{Port}";

        private void WaitForListening()
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var client = new System.Net.Sockets.TcpClient();
                    client.Connect("127.0.0.1", Port);
                    return;
                }
                catch (Exception)
                {
                    Thread.Sleep(100);
                }
            }

            throw new TimeoutException("聊天模拟服务器未在预期时间内开始监听。");
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
            }

            _process.Dispose();
            Environment.SetEnvironmentVariable("LLAMALAUNCHER_TEST_ENABLE_HTTP", _originalEnable);
            foreach (var name in _environment.Keys)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    private static ChatRequest CreateRequest(string baseUrl) => new()
    {
        BaseUrl = baseUrl,
        ModelId = "test-model",
        Messages = new[] { new ChatTurn(ChatRole.User, "你好") },
        Parameters = ChatParameters.CreateDefaults(),
    };

    [Fact]
    public async Task StreamAsync_ReassemblesDeltasInOrder()
    {
        using var server = new ChatMockServer(new Dictionary<string, string?>
        {
            ["LLAMALAUNCHER_TEST_CHAT_TEXT"] = "这是一段流式测试回复内容，需要完整还原。",
            ["LLAMALAUNCHER_TEST_CHAT_CHUNK_DELAY_MS"] = "5",
        });
        using var client = new StreamingChatClient(NullLogger<StreamingChatClient>.Instance);

        var text = new System.Text.StringBuilder();
        var completed = false;
        ChatError? error = null;
        await foreach (var streamEvent in client.StreamAsync(CreateRequest(server.BaseUrl)))
        {
            switch (streamEvent.Kind)
            {
                case ChatStreamEventKind.Delta:
                    text.Append(streamEvent.Text);
                    break;
                case ChatStreamEventKind.Completed:
                    completed = true;
                    break;
                case ChatStreamEventKind.Error:
                    error = streamEvent.Error;
                    break;
            }
        }

        Assert.Null(error);
        Assert.True(completed, "应收到 [DONE] 完成事件。");
        Assert.Equal("这是一段流式测试回复内容，需要完整还原。", text.ToString());
    }

    [Fact]
    public async Task StreamAsync_ParsesLlamaTimingsWhenUsageMissing()
    {
        using var server = new ChatMockServer(new Dictionary<string, string?>
        {
            ["LLAMALAUNCHER_TEST_CHAT_MODE"] = "timings-stream",
            ["LLAMALAUNCHER_TEST_CHAT_TEXT"] = "带统计的回复",
        });
        using var client = new StreamingChatClient(NullLogger<StreamingChatClient>.Instance);

        int? promptTokens = null;
        int? completionTokens = null;
        var completed = false;
        var text = new System.Text.StringBuilder();
        await foreach (var streamEvent in client.StreamAsync(CreateRequest(server.BaseUrl)))
        {
            switch (streamEvent.Kind)
            {
                case ChatStreamEventKind.Delta:
                    text.Append(streamEvent.Text);
                    break;
                case ChatStreamEventKind.Completed:
                    completed = true;
                    promptTokens = streamEvent.PromptTokens;
                    completionTokens = streamEvent.CompletionTokens;
                    break;
            }
        }

        Assert.True(completed, "应收到 [DONE] 完成事件。");
        Assert.Equal("带统计的回复", text.ToString());
        Assert.Equal(9, promptTokens);
        Assert.Equal(42, completionTokens);
    }

    [Fact]
    public async Task StreamAsync_InvalidFramesAreTolerated()
    {
        using var server = new ChatMockServer(new Dictionary<string, string?>
        {
            ["LLAMALAUNCHER_TEST_CHAT_MODE"] = "bad-stream",
        });
        using var client = new StreamingChatClient(NullLogger<StreamingChatClient>.Instance);

        var completed = false;
        ChatError? error = null;
        await foreach (var streamEvent in client.StreamAsync(CreateRequest(server.BaseUrl)))
        {
            if (streamEvent.Kind == ChatStreamEventKind.Completed)
            {
                completed = true;
            }

            if (streamEvent.Kind == ChatStreamEventKind.Error)
            {
                error = streamEvent.Error;
            }
        }

        Assert.Null(error);
        Assert.True(completed);
    }

    [Fact]
    public async Task StreamAsync_401_IsClassified()
    {
        using var server = new ChatMockServer(new Dictionary<string, string?>
        {
            ["LLAMALAUNCHER_TEST_CHAT_MODE"] = "401",
        });
        using var client = new StreamingChatClient(NullLogger<StreamingChatClient>.Instance);

        ChatError? error = null;
        await foreach (var streamEvent in client.StreamAsync(CreateRequest(server.BaseUrl)))
        {
            if (streamEvent.Kind == ChatStreamEventKind.Error)
            {
                error = streamEvent.Error;
            }
        }

        Assert.NotNull(error);
        Assert.Equal(ChatErrorKind.Unauthorized, error!.Kind);
        Assert.Equal(401, error.HttpStatusCode);
    }

    [Fact]
    public async Task StreamAsync_500_IsClassified()
    {
        using var server = new ChatMockServer(new Dictionary<string, string?>
        {
            ["LLAMALAUNCHER_TEST_CHAT_MODE"] = "500",
        });
        using var client = new StreamingChatClient(NullLogger<StreamingChatClient>.Instance);

        ChatError? error = null;
        await foreach (var streamEvent in client.StreamAsync(CreateRequest(server.BaseUrl)))
        {
            if (streamEvent.Kind == ChatStreamEventKind.Error)
            {
                error = streamEvent.Error;
            }
        }

        Assert.NotNull(error);
        Assert.Equal(ChatErrorKind.ServerError, error!.Kind);
    }

    [Fact]
    public async Task StreamAsync_TruncatedStream_ReportsConnectionError()
    {
        using var server = new ChatMockServer(new Dictionary<string, string?>
        {
            ["LLAMALAUNCHER_TEST_CHAT_MODE"] = "truncated-stream",
        });
        using var client = new StreamingChatClient(NullLogger<StreamingChatClient>.Instance);

        var text = new System.Text.StringBuilder();
        ChatError? error = null;
        await foreach (var streamEvent in client.StreamAsync(CreateRequest(server.BaseUrl)))
        {
            if (streamEvent.Kind == ChatStreamEventKind.Delta)
            {
                text.Append(streamEvent.Text);
            }

            if (streamEvent.Kind == ChatStreamEventKind.Error)
            {
                error = streamEvent.Error;
            }
        }

        Assert.NotNull(error);
        Assert.Equal(ChatErrorKind.Connection, error!.Kind);
        Assert.Equal("半截回复", text.ToString());
    }

    [Fact]
    public async Task StreamAsync_Cancellation_Throws()
    {
        using var server = new ChatMockServer(new Dictionary<string, string?>
        {
            ["LLAMALAUNCHER_TEST_CHAT_TEXT"] = "需要较长时间生成的文本内容用于取消测试。",
            ["LLAMALAUNCHER_TEST_CHAT_CHUNK_DELAY_MS"] = "120",
        });
        using var client = new StreamingChatClient(NullLogger<StreamingChatClient>.Instance);
        using var cts = new CancellationTokenSource();

        var sawDelta = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var streamEvent in client.StreamAsync(CreateRequest(server.BaseUrl), cts.Token))
            {
                if (streamEvent.Kind == ChatStreamEventKind.Delta && !sawDelta)
                {
                    sawDelta = true;
                    cts.Cancel();
                }
            }
        });
        Assert.True(sawDelta);
    }

    [Fact]
    public async Task CompleteAsync_ReturnsContent()
    {
        using var server = new ChatMockServer(new Dictionary<string, string?>
        {
            ["LLAMALAUNCHER_TEST_CHAT_MODE"] = "json",
        });
        using var client = new StreamingChatClient(NullLogger<StreamingChatClient>.Instance);

        var (success, content, error) = await client.CompleteAsync(CreateRequest(server.BaseUrl));

        Assert.True(success);
        Assert.Null(error);
        Assert.Equal("固定回复", content);
    }

    [Fact]
    public async Task StreamAsync_UnreachableServer_ReportsConnectionError()
    {
        using var client = new StreamingChatClient(NullLogger<StreamingChatClient>.Instance);
        var port = IntegrationFixture.GetFreePort();

        ChatError? error = null;
        await foreach (var streamEvent in client.StreamAsync(CreateRequest($"http://127.0.0.1:{port}")))
        {
            if (streamEvent.Kind == ChatStreamEventKind.Error)
            {
                error = streamEvent.Error;
            }
        }

        Assert.NotNull(error);
        Assert.True(error!.Kind is ChatErrorKind.Connection or ChatErrorKind.Timeout);
    }
}
