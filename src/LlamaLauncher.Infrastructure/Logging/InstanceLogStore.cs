using System.Collections.Concurrent;
using System.Text;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Security;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.Logging;

/// <summary>
/// 每实例日志采集：内存环形缓冲（供 UI 虚拟化读取）+ 滚动文件（带上限与脱敏）。
/// 落盘失败不会中断推理，只在内存缓冲写入告警行。
/// </summary>
public sealed class InstanceLogStore : IInstanceLogStore, IAsyncDisposable
{
    private readonly string _logsDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InstanceLogStore> _logger;
    private readonly ConcurrentDictionary<Guid, InstanceLogChannel> _channels = new();
    private volatile IReadOnlyList<string> _knownSecrets = Array.Empty<string>();
    private readonly object _secretsGate = new();

    public InstanceLogStore(string logsDirectory, TimeProvider timeProvider, ILogger<InstanceLogStore> logger)
    {
        _logsDirectory = logsDirectory;
        _timeProvider = timeProvider;
        _logger = logger;
        Directory.CreateDirectory(logsDirectory);
    }

    public LogRetentionOptions Options { get; set; } = new();

    public void Append(Guid instanceId, string instanceName, LogStreamKind stream, string text)
    {
        try
        {
            var masked = SecretMasker.MaskText(text, _knownSecrets);
            var line = new InstanceLogLine(_timeProvider.GetUtcNow(), stream, masked);
            var channel = _channels.GetOrAdd(instanceId, _ => new InstanceLogChannel(instanceName));
            channel.Append(line, Options, _logsDirectory, instanceId, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "追加实例日志失败（{InstanceId}）", instanceId);
        }
    }

    public IReadOnlyList<InstanceLogLine> GetRecent(Guid instanceId, int maxLines = 500)
        => _channels.TryGetValue(instanceId, out var channel)
            ? channel.GetRecent(maxLines)
            : Array.Empty<InstanceLogLine>();

    public string GetLogFilePath(Guid instanceId, string instanceName)
        => Path.Combine(_logsDirectory, BuildFileName(instanceId, instanceName));

    public void SetKnownSecrets(IEnumerable<string> secrets)
    {
        lock (_secretsGate)
        {
            _knownSecrets = secrets.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToArray();
        }
    }

    public void AddKnownSecret(string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return;
        }

        lock (_secretsGate)
        {
            if (!_knownSecrets.Contains(secret, StringComparer.Ordinal))
            {
                _knownSecrets = _knownSecrets.Append(secret).ToArray();
            }
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        foreach (var channel in _channels.Values)
        {
            await channel.FlushAsync().ConfigureAwait(false);
        }
    }

    public int CleanupOldFiles(TimeSpan retention)
    {
        var removed = 0;
        try
        {
            var threshold = DateTime.UtcNow - retention;
            foreach (var file in Directory.EnumerateFiles(_logsDirectory, "*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < threshold)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "清理过期日志失败：{File}", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清理日志目录失败");
        }

        return removed;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var channel in _channels.Values)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        _channels.Clear();
    }

    private static string BuildFileName(Guid instanceId, string instanceName)
    {
        var safeName = new string((instanceName ?? string.Empty)
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
            .ToArray())
            .Trim();
        if (safeName.Length == 0)
        {
            safeName = "instance";
        }

        if (safeName.Length > 40)
        {
            safeName = safeName[..40];
        }

        return $"{safeName}-{instanceId.ToString("N")[..8]}.log";
    }

    /// <summary>单实例日志通道：内存环形缓冲 + 文件写入 + 轮转。</summary>
    private sealed class InstanceLogChannel : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly Queue<InstanceLogLine> _buffer = new();
        private readonly string _instanceName;
        private StreamWriter? _writer;
        private string? _filePath;
        private long _currentSize;
        private bool _diskFailureReported;

        public InstanceLogChannel(string instanceName) => _instanceName = instanceName;

        public void Append(
            InstanceLogLine line,
            LogRetentionOptions options,
            string logsDirectory,
            Guid instanceId,
            ILogger logger)
        {
            lock (_gate)
            {
                _buffer.Enqueue(line);
                while (_buffer.Count > options.MemoryBufferLines)
                {
                    _buffer.Dequeue();
                }

                try
                {
                    EnsureWriter(logsDirectory, instanceId, options);
                }
                catch (Exception ex)
                {
                    if (!_diskFailureReported)
                    {
                        _diskFailureReported = true;
                        logger.LogWarning(ex, "实例日志文件不可写（{Instance}），仅保留内存缓冲", _instanceName);
                        _buffer.Enqueue(new InstanceLogLine(
                            line.TimestampUtc,
                            LogStreamKind.Application,
                            "【警告】日志文件不可写：" + ex.Message));
                    }

                    return;
                }

                try
                {
                    var text = $"[{line.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}] " +
                               $"{StreamLabel(line.Stream)} {line.Text}";
                    _writer!.WriteLine(text);
                    _currentSize += Encoding.UTF8.GetByteCount(text) + Environment.NewLine.Length;
                    if (_currentSize > options.MaxFileBytes)
                    {
                        Rotate(options, logger);
                    }
                }
                catch (Exception ex)
                {
                    if (!_diskFailureReported)
                    {
                        _diskFailureReported = true;
                        logger.LogWarning(ex, "写入实例日志失败（{Instance}）", _instanceName);
                    }
                }
            }
        }

        public IReadOnlyList<InstanceLogLine> GetRecent(int maxLines)
        {
            lock (_gate)
            {
                var count = Math.Min(Math.Max(maxLines, 1), _buffer.Count);
                return _buffer.Skip(_buffer.Count - count).ToArray();
            }
        }

        public Task FlushAsync()
        {
            lock (_gate)
            {
                try
                {
                    _writer?.Flush();
                }
                catch (Exception)
                {
                }
            }

            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            StreamWriter? writer;
            lock (_gate)
            {
                writer = _writer;
                _writer = null;
            }

            if (writer is not null)
            {
                try
                {
                    await writer.FlushAsync().ConfigureAwait(false);
                    await writer.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
        }

        private void EnsureWriter(string logsDirectory, Guid instanceId, LogRetentionOptions options)
        {
            Directory.CreateDirectory(logsDirectory);
            _filePath ??= Path.Combine(logsDirectory, BuildFileName(instanceId, _instanceName));

            if (_writer is null)
            {
                _writer = new StreamWriter(
                    new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true,
                };
                _currentSize = new FileInfo(_filePath).Exists ? new FileInfo(_filePath).Length : 0;
            }
        }

        private void Rotate(LogRetentionOptions options, ILogger logger)
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;

                if (_filePath is null)
                {
                    return;
                }

                for (var i = options.MaxRotatedFiles; i >= 1; i--)
                {
                    var older = $"{_filePath}.{i}";
                    if (!File.Exists(older))
                    {
                        continue;
                    }

                    if (i == options.MaxRotatedFiles)
                    {
                        File.Delete(older);
                    }
                    else
                    {
                        File.Move(older, $"{_filePath}.{i + 1}", overwrite: true);
                    }
                }

                if (File.Exists(_filePath))
                {
                    File.Move(_filePath, $"{_filePath}.1", overwrite: true);
                }

                _currentSize = 0;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "实例日志轮转失败（{Instance}）", _instanceName);
            }
        }

        private static string StreamLabel(LogStreamKind stream) => stream switch
        {
            LogStreamKind.StandardOutput => "[stdout]",
            LogStreamKind.StandardError => "[stderr]",
            _ => "[app]",
        };
    }
}
