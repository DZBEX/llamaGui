using Microsoft.Extensions.Logging;

namespace LlamaLauncher.IntegrationTests.TestSupport;

/// <summary>把日志消息收集到列表的测试用 ILogger（用于断言与诊断）。</summary>
public sealed class CaptureLogger<T> : ILogger<T>
{
    private readonly List<string> _messages = new();
    private readonly object _gate = new();

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages.ToArray();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _messages.Add($"[{logLevel}] {formatter(state, exception)}" +
                          (exception is null ? string.Empty : $" | {exception.GetType().Name}: {exception.Message}"));
        }
    }
}
