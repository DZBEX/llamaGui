using LlamaLauncher.Application.Capabilities;
using LlamaLauncher.Domain;
using LlamaLauncher.UnitTests.TestSupport;
using Xunit;

namespace LlamaLauncher.UnitTests.Capabilities;

public sealed class HelpTextParserTests
{
    [Fact]
    public void ParsesModernLayout()
    {
        var options = HelpTextParser.ParseOptions(HelpFixtures.Modern);
        var byName = options.ToDictionary(o => o.CanonicalName, StringComparer.OrdinalIgnoreCase);

        var ctx = byName["ctx-size"];
        Assert.Contains("--ctx-size", ctx.Flags);
        Assert.Contains("-c", ctx.Flags);
        Assert.Equal(OptionValueKind.Required, ctx.ValueKind);
        Assert.Equal("<N>", ctx.ValueHint);
        Assert.Contains("size of the prompt context", ctx.Description);

        Assert.Contains("--model", byName["model"].Flags);
        Assert.Contains("-m", byName["model"].Flags);
        Assert.Contains("--host", byName["host"].Flags);
        Assert.Equal(OptionValueKind.Required, byName["host"].ValueKind);
        Assert.Equal("<HOST>", byName["host"].ValueHint);

        Assert.Contains("--api-key", byName["api-key"].Flags);
        Assert.Contains("--cont-batching", byName["cont-batching"].Flags);
        Assert.Contains("--verbose", byName["verbose"].Flags);
    }

    [Fact]
    public void ParsesOptionalValueForms()
    {
        var options = HelpTextParser.ParseOptions(HelpFixtures.Modern);
        var flash = options.Single(o => o.CanonicalName == "flash-attn");

        Assert.Equal(OptionValueKind.Optional, flash.ValueKind);
        Assert.Equal("[on|off|auto]", flash.ValueHint);
        Assert.Contains("--flash-attn", flash.Flags);
        Assert.Contains("-fa", flash.Flags);
    }

    [Fact]
    public void ParsesLegacyInterleavedLayout()
    {
        var options = HelpTextParser.ParseOptions(HelpFixtures.LegacyInterleaved);
        var byName = options.ToDictionary(o => o.CanonicalName, StringComparer.OrdinalIgnoreCase);

        var ctx = byName["ctx-size"];
        Assert.Contains("--ctx-size", ctx.Flags);
        Assert.Contains("-c", ctx.Flags);
        Assert.Equal(OptionValueKind.Required, ctx.ValueKind);

        var flash = byName["flash-attn"];
        Assert.Equal(OptionValueKind.None, flash.ValueKind);

        Assert.True(byName.ContainsKey("n-gpu-layers"));
        Assert.True(byName.ContainsKey("host"));
        Assert.True(byName.ContainsKey("port"));
    }

    [Fact]
    public void GarbageInput_DoesNotThrowAndKeepsWhatItCan()
    {
        var options = HelpTextParser.ParseOptions(HelpFixtures.Garbage);
        Assert.Contains(options, o => o.CanonicalName == "ctx-size");
    }

    [Fact]
    public void EmptyOrNullInput_ReturnsEmpty()
    {
        Assert.Empty(HelpTextParser.ParseOptions(null));
        Assert.Empty(HelpTextParser.ParseOptions(string.Empty));
        Assert.Empty(HelpTextParser.ParseOptions("完全没有参数的文本"));
    }

    [Fact]
    public void UnknownFlags_AreCapturedWithDerivedCanonicalName()
    {
        var options = HelpTextParser.ParseOptions("""
              --future-feature <X>     something new
            """);
        var option = Assert.Single(options);
        Assert.Equal("future-feature", option.CanonicalName);
        Assert.Equal(OptionValueKind.Required, option.ValueKind);
    }

    [Theory]
    [InlineData("version: 4321 (abcdef1)", "b4321")]
    [InlineData("version: 12", "b12")]
    [InlineData("version: 0.2.0-dev (build 10618, commit eb25b7263)", "b10618")]
    [InlineData("version: 0.2.0-dev (build 10618, commit eb25b7263)\nbuilt with Clang 20.1.8 for Windows x86_64", "b10618")]
    [InlineData("load_backend: loaded CPU backend", "load_backend: loaded CPU backend")]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    public void ExtractVersionLabel_Works(string? text, string? expected)
        => Assert.Equal(expected, HelpTextParser.ExtractVersionLabel(text));
}
