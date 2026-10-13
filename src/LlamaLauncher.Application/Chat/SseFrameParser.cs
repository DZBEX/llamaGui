using System.Text;

namespace LlamaLauncher.Application.Chat;

/// <summary>
/// SSE（Server-Sent Events）分帧解析器：正确处理跨 buffer 分块、CRLF/LF、
/// 多行 data:、注释行；输出每个事件的完整 data 载荷。
/// </summary>
public sealed class SseFrameParser
{
    private readonly StringBuilder _lineBuffer = new();
    private readonly StringBuilder _dataBuffer = new();
    private bool _hasData;
    private bool _sawCarriageReturn;

    /// <summary>输入一段原始文本，返回其中已完整的 data 载荷列表（可能为空）。</summary>
    public IReadOnlyList<string> Feed(string chunk)
    {
        var events = new List<string>();
        if (string.IsNullOrEmpty(chunk))
        {
            return events;
        }

        foreach (var rawChar in chunk)
        {
            var c = rawChar;

            // 处理 CRLF：遇到 \r 时先不提交行，等下一个字符判断
            if (_sawCarriageReturn)
            {
                _sawCarriageReturn = false;
                if (c == '\n')
                {
                    ProcessLine(events);
                    continue;
                }

                ProcessLine(events);
            }

            if (c == '\r')
            {
                _sawCarriageReturn = true;
                continue;
            }

            if (c == '\n')
            {
                ProcessLine(events);
                continue;
            }

            _lineBuffer.Append(c);
        }

        return events;
    }

    /// <summary>流结束时调用：提交未完成的行（不完整的事件不输出）。</summary>
    public IReadOnlyList<string> Flush()
    {
        var events = new List<string>();
        if (_sawCarriageReturn)
        {
            _sawCarriageReturn = false;
            ProcessLine(events);
        }

        if (_lineBuffer.Length > 0)
        {
            ProcessLine(events);
        }

        return events;
    }

    private void ProcessLine(List<string> events)
    {
        var line = _lineBuffer.ToString();
        _lineBuffer.Clear();

        if (line.Length == 0)
        {
            // 空行：事件结束
            if (_hasData)
            {
                events.Add(_dataBuffer.ToString());
                _dataBuffer.Clear();
                _hasData = false;
            }

            return;
        }

        if (line[0] == ':')
        {
            // 注释行（心跳）
            return;
        }

        if (line.StartsWith("data:", StringComparison.Ordinal))
        {
            var value = line.Length > 5 ? line[5..] : string.Empty;
            if (value.StartsWith(" ", StringComparison.Ordinal))
            {
                value = value[1..];
            }

            if (_hasData)
            {
                _dataBuffer.Append('\n');
            }

            _dataBuffer.Append(value);
            _hasData = true;
        }

        // 其他字段（event:、id:、retry:）当前协议未使用，忽略。
    }
}
