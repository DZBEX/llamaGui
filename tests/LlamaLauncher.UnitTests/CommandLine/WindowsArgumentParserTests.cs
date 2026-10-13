using LlamaLauncher.Application.CommandLine;
using Xunit;

namespace LlamaLauncher.UnitTests.CommandLine;

public sealed class WindowsArgumentParserTests
{
    [Fact]
    public void SplitsSimpleArguments()
    {
        var tokens = WindowsArgumentParser.Parse("--foo bar baz");
        Assert.Equal(new[] { "--foo", "bar", "baz" }, tokens);
    }

    [Fact]
    public void HandlesQuotedValuesWithSpaces()
    {
        var tokens = WindowsArgumentParser.Parse("--name \"C:\\模型 目录\\a.gguf\" --flag");
        Assert.Equal(new[] { "--name", "C:\\模型 目录\\a.gguf", "--flag" }, tokens);
    }

    [Fact]
    public void HandlesEscapedQuotes()
    {
        var tokens = WindowsArgumentParser.Parse("--text \"say \\\"hello\\\"\"");
        Assert.Equal(new[] { "--text", "say \"hello\"" }, tokens);
    }

    [Fact]
    public void HandlesBackslashes()
    {
        var tokens = WindowsArgumentParser.Parse("--path C:\\llama\\lib\\ && --x");
        Assert.Equal(new[] { "--path", "C:\\llama\\lib\\", "&&", "--x" }, tokens);
    }

    [Fact]
    public void HandlesEmptyQuotedArgument()
    {
        var tokens = WindowsArgumentParser.Parse("--empty \"\" end");
        Assert.Equal(new[] { "--empty", string.Empty, "end" }, tokens);
    }

    [Fact]
    public void NullOrEmpty_ReturnsEmptyList()
    {
        Assert.Empty(WindowsArgumentParser.Parse(null));
        Assert.Empty(WindowsArgumentParser.Parse("   "));
    }

    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("with\"quote", "\"with\\\"quote\"")]
    [InlineData("trailing\\", "trailing\\")]
    [InlineData("path with space\\", "\"path with space\\\\\"")]
    [InlineData("", "\"\"")]
    public void QuoteArgument_MatchesWindowsRules(string input, string expected)
        => Assert.Equal(expected, WindowsCommandLine.QuoteArgument(input));
}
