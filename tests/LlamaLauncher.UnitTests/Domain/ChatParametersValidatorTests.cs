using LlamaLauncher.Domain;
using LlamaLauncher.Domain.Validation;
using Xunit;

namespace LlamaLauncher.UnitTests.Domain;

public sealed class ChatParametersValidatorTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        var issues = ChatParametersValidator.Validate(ChatParameters.CreateDefaults());
        Assert.DoesNotContain(issues, i => i.IsBlocking);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(5.1)]
    public void InvalidTemperature_IsError(double value)
    {
        var parameters = new ChatParameters { Temperature = value };
        Assert.Contains(
            ChatParametersValidator.Validate(parameters),
            i => i.Field == nameof(ChatParameters.Temperature) && i.IsBlocking);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void InvalidTopP_IsError(double value)
    {
        var parameters = new ChatParameters { TopP = value };
        Assert.Contains(
            ChatParametersValidator.Validate(parameters),
            i => i.Field == nameof(ChatParameters.TopP) && i.IsBlocking);
    }

    [Fact]
    public void MaxTokens_BelowMinusOne_IsError()
    {
        var parameters = new ChatParameters { MaxTokens = -2 };
        Assert.Contains(
            ChatParametersValidator.Validate(parameters),
            i => i.Field == nameof(ChatParameters.MaxTokens) && i.IsBlocking);
    }

    [Fact]
    public void EmptyStopSequence_IsError()
    {
        var parameters = new ChatParameters { StopSequences = { string.Empty } };
        Assert.Contains(
            ChatParametersValidator.Validate(parameters),
            i => i.Field == nameof(ChatParameters.StopSequences) && i.IsBlocking);
    }
}
