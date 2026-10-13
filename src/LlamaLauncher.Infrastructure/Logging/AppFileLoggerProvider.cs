using System.Text;
using LlamaLauncher.Application.Security;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.Logging;

/// <summary>应用日志文件 Provider：logs/app-{日期}.log，带大小轮转与脱敏。</summary>
public sealed class AppFileLoggerProvider : ILoggerProvider
{
    private const long MaxFileBytes = 10 * 1024 * 1024;
    private readonly string _logsDirectory;
    private readonly object _gate = new();

    public AppFileLoggerProvider(string logsDirectory)
    {
        _logsDirectory = logsDirectory;
        Directory.CreateDirectory(logsDirectory);
    }

    public ILogger CreateLogger(string categoryName) => new AppFileLogger(categoryName, this);

    public void Dispose()
    {
    }

    internal string CurrentFilePath =>
        Path.Combine(_logsDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");

    internal void Write(string message)
    {
        lock (_gate)
        {
            var path = CurrentFilePath;
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxFileBytes)
                {
                    var rotated = path + ".1";
                    if (File.Exists(rotated))
                    {
                        File.Delete(rotated);
                    }

                    File.Move(path, rotated);
                }

                File.AppendAllText(
                    path,
                    SecretMasker.MaskText(message) + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // 日志写入失败不得影响应用运行
            }
        }
    }

    private sealed class AppFileLogger : ILogger
    {
        private readonly string _category;
        private readonly AppFileLoggerProvider _provider;

        public AppFileLogger(string category, AppFileLoggerProvider provider)
        {
            _category = category;
            _provider = provider;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{Level(logLevel)}] {_category}: {message}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            _provider.Write(line);
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}
