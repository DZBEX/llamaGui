using LlamaLauncher.Application.Chat;
using Xunit;

namespace LlamaLauncher.UnitTests.Chat;

public sealed class SseFrameParserTests
{
    [Fact]
    public void ParsesSingleEvent()
    {
        var parser = new SseFrameParser();
        var events = parser.Feed("data: hello\n\n");
        Assert.Equal(new[] { "hello" }, events);
    }

    [Fact]
    public void ParsesMultipleEventsInOneChunk()
    {
        var parser = new SseFrameParser();
        var events = parser.Feed("data: one\n\ndata: two\n\ndata: three\n\n");
        Assert.Equal(new[] { "one", "two", "three" }, events);
    }

    [Fact]
    public void HandlesCrlfLineEndings()
    {
        var parser = new SseFrameParser();
        var events = parser.Feed("data: alpha\r\n\r\ndata: beta\r\n\r\n");
        Assert.Equal(new[] { "alpha", "beta" }, events);
    }

    [Fact]
    public void HandlesSplitAcrossChunks()
    {
        var parser = new SseFrameParser();
        Assert.Empty(parser.Feed("data: par"));
        Assert.Empty(parser.Feed("tial"));
        Assert.Empty(parser.Feed("\n"));
        var events = parser.Feed("\ndata: next\n\n");
        Assert.Equal(new[] { "partial", "next" }, events);
    }

    [Fact]
    public void HandlesCrAtChunkBoundary()
    {
        var parser = new SseFrameParser();
        Assert.Empty(parser.Feed("data: value\r"));
        var events = parser.Feed("\n\r\n");
        Assert.Equal(new[] { "value" }, events);
    }

    [Fact]
    public void JoinsMultilineDataWithNewline()
    {
        var parser = new SseFrameParser();
        var events = parser.Feed("data: line1\ndata: line2\n\n");
        Assert.Equal(new[] { "line1\nline2" }, events);
    }

    [Fact]
    public void IgnoresCommentsAndUnknownFields()
    {
        var parser = new SseFrameParser();
        var events = parser.Feed(": keep-alive\nevent: message\nid: 1\ndata: payload\n\n");
        Assert.Equal(new[] { "payload" }, events);
    }

    [Fact]
    public void PreservesDoneMarker()
    {
        var parser = new SseFrameParser();
        var events = parser.Feed("data: [DONE]\n\n");
        Assert.Equal(new[] { "[DONE]" }, events);
    }

    [Fact]
    public void DataWithoutSpace_IsParsed()
    {
        var parser = new SseFrameParser();
        var events = parser.Feed("data:{\"x\":1}\n\n");
        Assert.Equal(new[] { "{\"x\":1}" }, events);
    }

    [Fact]
    public void IncompleteEventAtEof_IsNotEmittedUntilFlush()
    {
        var parser = new SseFrameParser();
        Assert.Empty(parser.Feed("data: unfinished"));
        var flushed = parser.Flush();
        Assert.Empty(flushed); // 缺少空行分隔，按不完整事件处理（不输出）
    }

    [Fact]
    public void EmptyDataValue_IsPreserved()
    {
        var parser = new SseFrameParser();
        var events = parser.Feed("data:\n\n");
        Assert.Equal(new[] { string.Empty }, events);
    }

    [Fact]
    public void ByteByByteInput_ReconstructsEvents()
    {
        var parser = new SseFrameParser();
        var input = "data: 中文字符\n\ndata: [DONE]\n\n";
        var collected = new List<string>();
        foreach (var c in input)
        {
            collected.AddRange(parser.Feed(c.ToString()));
        }

        Assert.Equal(new[] { "中文字符", "[DONE]" }, collected);
    }
}
