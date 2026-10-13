using LlamaLauncher.Application.Security;
using Xunit;

namespace LlamaLauncher.UnitTests.Security;

public sealed class SecretMaskerTests
{
    [Fact]
    public void MasksApiKeyArgument()
    {
        var masked = SecretMasker.MaskText("llama-server --api-key sk-1234567890 --port 8080");
        Assert.DoesNotContain("sk-1234567890", masked);
        Assert.Contains("--api-key " + SecretMasker.Mask, masked);
        Assert.Contains("--port 8080", masked);
    }

    [Fact]
    public void MasksApiKeyArgumentWithEquals()
    {
        var masked = SecretMasker.MaskText("llama-server --api-key=secret-value-1");
        Assert.DoesNotContain("secret-value-1", masked);
    }

    [Fact]
    public void MasksJsonApiKey()
    {
        var masked = SecretMasker.MaskText("""{"api_key": "abc123def456", "model": "x"}""");
        Assert.DoesNotContain("abc123def456", masked);
        Assert.Contains("\"api_key\": \"******\"", masked);
    }

    [Fact]
    public void MasksBearerToken()
    {
        var masked = SecretMasker.MaskText("Authorization: Bearer abcdef123456");
        Assert.DoesNotContain("abcdef123456", masked);
        Assert.Contains("Bearer " + SecretMasker.Mask, masked);
    }

    [Fact]
    public void MasksQueryStringApiKey()
    {
        var masked = SecretMasker.MaskText("GET /health?api_key=zzz999&x=1");
        Assert.DoesNotContain("zzz999", masked);
        Assert.Contains("x=1", masked);
    }

    [Fact]
    public void ReplacesKnownSecretValues()
    {
        var masked = SecretMasker.MaskText(
            "text with my-super-secret inside",
            new[] { "my-super-secret" });
        Assert.DoesNotContain("my-super-secret", masked);
    }

    [Fact]
    public void ShortSecretsAreNotReplaced()
    {
        // 少于 4 个字符的秘密值容易被误替换，不做全局替换（由模式规则覆盖）。
        var masked = SecretMasker.MaskText("abc", new[] { "ab" });
        Assert.Equal("abc", masked);
    }

    [Theory]
    [InlineData("apiKey", true)]
    [InlineData("Api-Key", true)]
    [InlineData("authorization", true)]
    [InlineData("access_token", true)]
    [InlineData("clientSecret", true)]
    [InlineData("password", true)]
    [InlineData("modelPath", false)]
    [InlineData("Port", false)]
    public void IsSensitiveName_Classifies(string name, bool sensitive)
        => Assert.Equal(sensitive, SecretMasker.IsSensitiveName(name));

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, SecretMasker.MaskText(null));
        Assert.Equal(string.Empty, SecretMasker.MaskText(string.Empty));
    }
}
