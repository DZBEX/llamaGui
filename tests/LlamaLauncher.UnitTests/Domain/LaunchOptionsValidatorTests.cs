using LlamaLauncher.Domain;
using LlamaLauncher.Domain.Validation;
using Xunit;

namespace LlamaLauncher.UnitTests.Domain;

public sealed class LaunchOptionsValidatorTests
{
    private static LaunchValidationContext CreateContext() => new()
    {
        ModelFilePath = "C:\\models\\a.gguf",
        ExecutablePath = "C:\\llama\\llama-server.exe",
        ApiKeyConfigured = true,
        LanAccessConfirmed = true,
        ProcessorCount = 8,
    };

    private static ServerLaunchOptions CreateOptions() => new()
    {
        ModelPath = "C:\\models\\a.gguf",
        Host = "127.0.0.1",
        Port = 8080,
    };

    [Fact]
    public void ValidDefaults_ProduceNoErrors()
    {
        var issues = LaunchOptionsValidator.ValidateValues(CreateOptions(), CreateContext());
        Assert.DoesNotContain(issues, i => i.IsBlocking);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void InvalidPort_IsError(int port)
    {
        var options = CreateOptions();
        options.Port = port;
        var issues = LaunchOptionsValidator.ValidateValues(options, CreateContext());
        Assert.Contains(issues, i => i.Field == nameof(ServerLaunchOptions.Port) && i.IsBlocking);
    }

    [Fact]
    public void PortInUse_IsError()
    {
        var options = CreateOptions();
        var issues = LaunchOptionsValidator.ValidateValues(options, new LaunchValidationContext
        {
            ModelFilePath = "C:\\models\\a.gguf",
            ExecutablePath = "C:\\llama\\llama-server.exe",
            PortInUse = true,
        });
        Assert.Contains(issues, i => i.Field == nameof(ServerLaunchOptions.Port) && i.IsBlocking);
    }

    [Fact]
    public void LanHost_WithoutApiKey_IsError()
    {
        var options = CreateOptions();
        options.Host = "0.0.0.0";
        options.ApiKeyEnabled = false;
        var issues = LaunchOptionsValidator.ValidateValues(options, CreateContext());
        Assert.Contains(issues, i => i.Field == nameof(ServerLaunchOptions.ApiKeyEnabled) && i.IsBlocking);
    }

    [Fact]
    public void LanHost_WithoutConfiguredSecret_IsError()
    {
        var options = CreateOptions();
        options.Host = "192.168.1.10";
        options.ApiKeyEnabled = true;
        var issues = LaunchOptionsValidator.ValidateValues(options, new LaunchValidationContext
        {
            ModelFilePath = "C:\\models\\a.gguf",
            ExecutablePath = "C:\\llama\\llama-server.exe",
            ApiKeyConfigured = false,
            LanAccessConfirmed = true,
        });
        Assert.Contains(issues, i => i.Field == "ApiKey" && i.IsBlocking);
    }

    [Fact]
    public void LanHost_WithoutConfirmation_IsError()
    {
        var options = CreateOptions();
        options.Host = "0.0.0.0";
        options.ApiKeyEnabled = true;
        var issues = LaunchOptionsValidator.ValidateValues(options, new LaunchValidationContext
        {
            ModelFilePath = "C:\\models\\a.gguf",
            ExecutablePath = "C:\\llama\\llama-server.exe",
            ApiKeyConfigured = true,
            LanAccessConfirmed = false,
        });
        Assert.Contains(issues, i => i.Field == "LanAccess" && i.IsBlocking);
    }

    [Fact]
    public void LanHost_WithAllConditions_StillWarns()
    {
        var options = CreateOptions();
        options.Host = "192.168.1.10";
        options.ApiKeyEnabled = true;
        var issues = LaunchOptionsValidator.ValidateValues(options, CreateContext());
        Assert.DoesNotContain(issues, i => i.IsBlocking);
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Warning && i.Field == nameof(ServerLaunchOptions.Host));
    }

    [Fact]
    public void PublicHost_IsError()
    {
        var options = CreateOptions();
        options.Host = "8.8.8.8";
        var issues = LaunchOptionsValidator.ValidateValues(options, CreateContext());
        Assert.Contains(issues, i => i.Field == nameof(ServerLaunchOptions.Host) && i.IsBlocking);
    }

    [Fact]
    public void UBbatchGreaterThanBatch_IsError()
    {
        var options = CreateOptions();
        options.BatchSize = 128;
        options.UBatchSize = 256;
        var issues = LaunchOptionsValidator.ValidateValues(options, CreateContext());
        Assert.Contains(issues, i => i.Field == nameof(ServerLaunchOptions.UBatchSize) && i.IsBlocking);
    }

    [Theory]
    [InlineData("auto", false)]
    [InlineData("all", false)]
    [InlineData("12", false)]
    [InlineData("0", false)]
    [InlineData("-1", true)]
    [InlineData("abc", true)]
    public void GpuLayers_ValueForms(string value, bool expectError)
    {
        var options = CreateOptions();
        options.GpuLayers = value;
        var issues = LaunchOptionsValidator.ValidateValues(options, CreateContext());
        Assert.Equal(
            expectError,
            issues.Any(i => i.Field == nameof(ServerLaunchOptions.GpuLayers) && i.IsBlocking));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("layer")]
    [InlineData("row")]
    public void SplitMode_ValidValues_NoError(string split)
    {
        var options = CreateOptions();
        options.SplitMode = split;
        var issues = LaunchOptionsValidator.ValidateValues(options, CreateContext());
        Assert.DoesNotContain(issues, i => i.IsBlocking);
    }

    [Fact]
    public void SplitMode_InvalidValue_IsError()
    {
        var options = CreateOptions();
        options.SplitMode = "banana";
        var issues = LaunchOptionsValidator.ValidateValues(options, CreateContext());
        Assert.Contains(issues, i => i.Field == nameof(ServerLaunchOptions.SplitMode) && i.IsBlocking);
    }

    [Theory]
    [InlineData("3,1", false)]
    [InlineData("1", false)]
    [InlineData("3,,1", false)]
    [InlineData("0,1", true)]
    [InlineData("a,b", true)]
    public void TensorSplit_Validation(string value, bool expectError)
    {
        var options = CreateOptions();
        options.TensorSplit = value;
        var issues = LaunchOptionsValidator.ValidateValues(options, CreateContext());
        Assert.Equal(
            expectError,
            issues.Any(i => i.Field == nameof(ServerLaunchOptions.TensorSplit) && i.IsBlocking));
    }

    [Fact]
    public void UnknownCacheType_IsWarningNotError()
    {
        var options = CreateOptions();
        options.CacheTypeK = "q4_k_m_experimental";
        var issues = LaunchOptionsValidator.ValidateValues(options, CreateContext());
        Assert.DoesNotContain(issues, i => i.IsBlocking);
        Assert.Contains(issues, i => i.Field == nameof(ServerLaunchOptions.CacheTypeK) && i.Severity == ValidationSeverity.Warning);
    }

    [Fact]
    public void HighThreadCount_IsWarning()
    {
        var options = CreateOptions();
        options.Threads = 100;
        var issues = LaunchOptionsValidator.ValidateValues(options, CreateContext());
        Assert.DoesNotContain(issues, i => i.IsBlocking);
        Assert.Contains(issues, i => i.Field == nameof(ServerLaunchOptions.Threads) && i.Severity == ValidationSeverity.Warning);
    }

    [Fact]
    public void MissingModelAndExecutable_AreErrors()
    {
        var issues = LaunchOptionsValidator.ValidateValues(new ServerLaunchOptions(), new LaunchValidationContext());
        Assert.Contains(issues, i => i.Field == nameof(ServerLaunchOptions.ModelPath) && i.IsBlocking);
        Assert.Contains(issues, i => i.Field == "ExecutablePath" && i.IsBlocking);
    }

    [Fact]
    public void LargeContextWithLargeModel_Warns()
    {
        var options = CreateOptions();
        options.ContextSize = 65536;
        var issues = LaunchOptionsValidator.ValidateValues(options, new LaunchValidationContext
        {
            ModelFilePath = "C:\\models\\a.gguf",
            ExecutablePath = "C:\\llama\\llama-server.exe",
            ModelFileSizeBytes = 40L * 1024 * 1024 * 1024,
            TotalMemoryBytes = 64L * 1024 * 1024 * 1024,
        });
        Assert.Contains(issues, i => i.Field == nameof(ServerLaunchOptions.ContextSize) && i.Severity == ValidationSeverity.Warning);
    }
}
