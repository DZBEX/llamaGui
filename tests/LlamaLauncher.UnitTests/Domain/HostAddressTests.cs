using LlamaLauncher.Domain;
using Xunit;

namespace LlamaLauncher.UnitTests.Domain;

public sealed class HostAddressTests
{
    [Theory]
    [InlineData("127.0.0.1", AddressScope.Localhost)]
    [InlineData("localhost", AddressScope.Localhost)]
    [InlineData("::1", AddressScope.Localhost)]
    [InlineData("0.0.0.0", AddressScope.Lan)]
    [InlineData("192.168.1.10", AddressScope.Lan)]
    [InlineData("10.0.0.5", AddressScope.Lan)]
    [InlineData("172.16.5.5", AddressScope.Lan)]
    [InlineData("172.32.5.5", AddressScope.Public)]
    [InlineData("8.8.8.8", AddressScope.Public)]
    [InlineData("some-machine-name", AddressScope.Lan)]
    [InlineData("", AddressScope.Invalid)]
    [InlineData("   ", AddressScope.Invalid)]
    public void Classify_ReturnsExpectedScope(string host, AddressScope expected)
        => Assert.Equal(expected, HostAddress.Classify(host));

    [Fact]
    public void IsNonLocal_CoversLanAndPublic()
    {
        Assert.True(HostAddress.IsNonLocal(AddressScope.Lan));
        Assert.True(HostAddress.IsNonLocal(AddressScope.Public));
        Assert.False(HostAddress.IsNonLocal(AddressScope.Localhost));
        Assert.False(HostAddress.IsNonLocal(AddressScope.Invalid));
    }
}
