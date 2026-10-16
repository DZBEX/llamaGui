using LlamaLauncher.Application.Benchmarks;
using Xunit;

namespace LlamaLauncher.UnitTests.Benchmarks;

public sealed class CompletionStreamFrameParserTests
{
    [Fact]
    public void ParsesContentFrame()
    {
        var frame = CompletionStreamFrameParser.Parse("{\"content\":\"Hello\"}");

        Assert.NotNull(frame);
        Assert.Equal("Hello", frame!.Content);
        Assert.True(frame.HasContent);
        Assert.False(frame.IsStop);
        Assert.Null(frame.Timings);
        Assert.Null(frame.Error);
    }

    [Fact]
    public void ParsesFinalFrameWithTimings()
    {
        var frame = CompletionStreamFrameParser.Parse(
            "{\"content\":\"\",\"stop\":true,\"timings\":{\"prompt_n\":512,\"prompt_ms\":256.5," +
            "\"prompt_per_second\":1996.1,\"predicted_n\":128,\"predicted_ms\":1024.0," +
            "\"predicted_per_second\":125.0}}");

        Assert.NotNull(frame);
        Assert.True(frame!.IsStop);
        Assert.False(frame.HasContent);
        Assert.NotNull(frame.Timings);
        Assert.Equal(512, frame.Timings!.PromptTokens);
        Assert.Equal(256.5, frame.Timings.PromptMs);
        Assert.Equal(1996.1, frame.Timings.PromptTokensPerSecond);
        Assert.Equal(128, frame.Timings.PredictedTokens);
        Assert.Equal(1024.0, frame.Timings.PredictedMs);
        Assert.Equal(125.0, frame.Timings.PredictedTokensPerSecond);
    }

    [Fact]
    public void ParsesTopLevelTokenCountsWhenTimingsMissing()
    {
        var frame = CompletionStreamFrameParser.Parse(
            "{\"content\":\"x\",\"stop\":true,\"tokens_predicted\":10,\"tokens_evaluated\":64}");

        Assert.NotNull(frame?.Timings);
        Assert.Equal(64, frame!.Timings!.PromptTokens);
        Assert.Equal(10, frame.Timings.PredictedTokens);
    }

    [Fact]
    public void ParsesErrorObject()
    {
        var frame = CompletionStreamFrameParser.Parse(
            "{\"error\":{\"code\":500,\"message\":\"failed to process prompt\"}}");

        Assert.NotNull(frame);
        Assert.Equal("failed to process prompt", frame!.Error);
    }

    [Fact]
    public void ParsesErrorString()
    {
        var frame = CompletionStreamFrameParser.Parse("{\"error\":\"slot unavailable\"}");

        Assert.NotNull(frame);
        Assert.Equal("slot unavailable", frame!.Error);
    }

    [Fact]
    public void ParsesDoneMarkerAsStop()
    {
        var frame = CompletionStreamFrameParser.Parse("[DONE]");

        Assert.NotNull(frame);
        Assert.True(frame!.IsStop);
    }

    [Fact]
    public void ReturnsNullForEmptyOrInvalidPayload()
    {
        Assert.Null(CompletionStreamFrameParser.Parse(null));
        Assert.Null(CompletionStreamFrameParser.Parse("   "));
        Assert.Null(CompletionStreamFrameParser.Parse("not-json"));
    }

    [Fact]
    public void IgnoresNonStringContent()
    {
        var frame = CompletionStreamFrameParser.Parse("{\"content\":null,\"stop\":false}");

        Assert.NotNull(frame);
        Assert.False(frame!.HasContent);
        Assert.False(frame.IsStop);
    }
}
