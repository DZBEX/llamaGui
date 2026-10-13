using LlamaLauncher.Application.CommandLine;
using LlamaLauncher.Application.Security;
using LlamaLauncher.Domain;
using LlamaLauncher.Domain.Validation;
using LlamaLauncher.UnitTests.TestSupport;
using Xunit;

namespace LlamaLauncher.UnitTests.CommandLine;

public sealed class CommandLineBuilderTests
{
    private const string ExePath = "C:\\llama\\llama-server.exe";

    private static ServerLaunchOptions CreateOptions() => new()
    {
        ModelPath = "C:\\模型 目录\\test.gguf",
        Host = "127.0.0.1",
        Port = 8080,
        ContextSize = 4096,
        Threads = 8,
        ThreadsBatch = 8,
        BatchSize = 2048,
        UBatchSize = 512,
        GpuLayers = "12",
        Devices = { "CUDA0" },
        SplitMode = "layer",
        TensorSplit = "3,1",
        MainGpu = 0,
        CacheTypeK = "q8_0",
        CacheTypeV = "q8_0",
        FlashAttention = FlashAttentionMode.On,
        Alias = "my-model",
        ParallelSlots = 2,
        ContinuousBatching = true,
        VerboseLogging = true,
        NoMemoryMap = true,
        ApiKeyEnabled = true,
    };

    [Fact]
    public void FullBuild_EmitsAllSupportedOptions()
    {
        var capabilities = HelpFixtures.CreateCapabilities(HelpFixtures.Modern);
        var request = new CommandLineBuildRequest
        {
            Options = CreateOptions(),
            Capabilities = capabilities,
            ResolvedApiKey = "secret-api-key-123",
        };

        var result = CommandLineBuilder.Build(ExePath, request);

        Assert.False(result.HasErrors, string.Join("; ", result.Issues.Select(i => i.Message)));
        Assert.Equal("C:\\模型 目录\\test.gguf", result.Tokens[1]);
        Assert.Contains("--host", result.Tokens);
        Assert.Contains("127.0.0.1", result.Tokens);
        Assert.Contains("--port", result.Tokens);
        Assert.Contains("--ctx-size", result.Tokens);
        Assert.Contains("--n-gpu-layers", result.Tokens); // 目录首选长格式
        Assert.Contains("--device", result.Tokens);
        Assert.Contains("CUDA0", result.Tokens);
        Assert.Contains("--flash-attn", result.Tokens);
        Assert.Contains("on", result.Tokens);
        Assert.Contains("--no-mmap", result.Tokens);
        Assert.Contains("--api-key", result.Tokens);
        Assert.Contains("secret-api-key-123", result.Tokens);
    }

    [Fact]
    public void Preview_MasksApiKeyButTokensKeepIt()
    {
        var capabilities = HelpFixtures.CreateCapabilities(HelpFixtures.Modern);
        var result = CommandLineBuilder.Build(ExePath, new CommandLineBuildRequest
        {
            Options = CreateOptions(),
            Capabilities = capabilities,
            ResolvedApiKey = "super-secret-value",
        });

        Assert.Contains("super-secret-value", result.Tokens);
        Assert.DoesNotContain("super-secret-value", result.MaskedPreview);
        Assert.Contains(SecretMasker.Mask, result.MaskedPreview);
        Assert.Contains("--api-key", result.MaskedPreview);
    }

    [Fact]
    public void Preview_QuotesPathsWithSpaces()
    {
        var capabilities = HelpFixtures.CreateCapabilities(HelpFixtures.Modern);
        var result = CommandLineBuilder.Build(
            "C:\\Program Files\\llama.cpp\\llama-server.exe",
            new CommandLineBuildRequest
            {
                Options = CreateOptions(),
                Capabilities = capabilities,
                ResolvedApiKey = "k",
            });

        Assert.Contains("\"C:\\Program Files\\llama.cpp\\llama-server.exe\"", result.MaskedPreview);
        Assert.Contains("\"C:\\模型 目录\\test.gguf\"", result.MaskedPreview);
    }

    [Fact]
    public void UnsupportedManagedOption_BlocksStart()
    {
        // 老版本帮助中没有 --device
        var capabilities = HelpFixtures.CreateCapabilities(HelpFixtures.LegacyInterleaved);
        var options = CreateOptions();
        options.Devices.Clear();
        options.Devices.Add("CUDA0");
        options.FlashAttention = null;
        options.ContinuousBatching = null;

        var result = CommandLineBuilder.Build(ExePath, new CommandLineBuildRequest
        {
            Options = options,
            Capabilities = capabilities,
            ResolvedApiKey = "k",
        });

        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i => i.IsBlocking && i.Message.Contains("--device"));
    }

    [Fact]
    public void GpuLayersAuto_RejectedWhenHelpDoesNotDocumentIt()
    {
        var capabilities = HelpFixtures.CreateCapabilities(HelpFixtures.Modern);
        var options = CreateOptions();
        options.GpuLayers = "auto";

        var result = CommandLineBuilder.Build(ExePath, new CommandLineBuildRequest
        {
            Options = options,
            Capabilities = capabilities,
            ResolvedApiKey = "k",
        });

        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i => i.Field == nameof(ServerLaunchOptions.GpuLayers) && i.IsBlocking);
    }

    [Fact]
    public void GpuLayersAll_AcceptedWhenHelpDocumentsIt()
    {
        var capabilities = HelpFixtures.CreateCapabilities(HelpFixtures.WithGpuLayersAuto);
        var options = CreateOptions();
        options.GpuLayers = "all";
        options.FlashAttention = null;
        options.ContinuousBatching = null;
        options.NoMemoryMap = false;
        options.VerboseLogging = false;

        var result = CommandLineBuilder.Build(ExePath, new CommandLineBuildRequest
        {
            Options = options,
            Capabilities = capabilities,
            ResolvedApiKey = "k",
        });

        Assert.False(result.HasErrors, string.Join("; ", result.Issues.Select(i => i.Message)));
        var index = result.Tokens.IndexOf("--n-gpu-layers");
        Assert.True(index >= 0);
        Assert.Equal("all", result.Tokens[index + 1]);
    }

    [Fact]
    public void FlashAttention_PlainFlagVersion_OnEmitsFlagOnly()
    {
        var capabilities = HelpFixtures.CreateCapabilities(HelpFixtures.LegacyInterleaved);
        var options = new ServerLaunchOptions
        {
            ModelPath = "C:\\m.gguf",
            Host = "127.0.0.1",
            Port = 8080,
            FlashAttention = FlashAttentionMode.On,
        };

        var result = CommandLineBuilder.Build(ExePath, new CommandLineBuildRequest
        {
            Options = options,
            Capabilities = capabilities,
        });

        Assert.False(result.HasErrors);
        var index = result.Tokens.IndexOf("--flash-attn");
        Assert.True(index >= 0);
        Assert.Equal(result.Tokens.Count - 1, index); // 无携带取值
    }

    [Fact]
    public void FlashAttention_PlainFlagVersion_OffWarnsAndSkips()
    {
        var capabilities = HelpFixtures.CreateCapabilities(HelpFixtures.LegacyInterleaved);
        var options = new ServerLaunchOptions
        {
            ModelPath = "C:\\m.gguf",
            Host = "127.0.0.1",
            Port = 8080,
            FlashAttention = FlashAttentionMode.Off,
        };

        var result = CommandLineBuilder.Build(ExePath, new CommandLineBuildRequest
        {
            Options = options,
            Capabilities = capabilities,
        });

        Assert.False(result.HasErrors);
        Assert.DoesNotContain("--flash-attn", result.Tokens);
        Assert.Contains(result.Issues, i => i.Severity == ValidationSeverity.Warning);
    }

    [Fact]
    public void FlashAttention_ValueVersion_OffEmitsOff()
    {
        var capabilities = HelpFixtures.CreateCapabilities(HelpFixtures.Modern);
        var options = new ServerLaunchOptions
        {
            ModelPath = "C:\\m.gguf",
            Host = "127.0.0.1",
            Port = 8080,
            FlashAttention = FlashAttentionMode.Off,
        };

        var result = CommandLineBuilder.Build(ExePath, new CommandLineBuildRequest
        {
            Options = options,
            Capabilities = capabilities,
        });

        var index = result.Tokens.IndexOf("--flash-attn");
        Assert.True(index >= 0);
        Assert.Equal("off", result.Tokens[index + 1]);
    }

    [Fact]
    public void ExtraArguments_ConflictWithManagedPort_BlocksByDefault()
    {
        var capabilities = HelpFixtures.CreateCapabilities(HelpFixtures.Modern);
        var result = CommandLineBuilder.Build(ExePath, new CommandLineBuildRequest
        {
            Options = new ServerLaunchOptions { ModelPath = "C:\\m.gguf", Port = 8080 },
            Capabilities = capabilities,
            ExtraArguments = "--port 9999",
        });

        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i => i.Field == "ExtraArguments" && i.IsBlocking);
    }

    [Fact]
    public void ExtraArguments_ConflictWithOverride_WarnsAndAppends()
    {
        var capabilities = HelpFixtures.CreateCapabilities(HelpFixtures.Modern);
        var result = CommandLineBuilder.Build(ExePath, new CommandLineBuildRequest
        {
            Options = new ServerLaunchOptions { ModelPath = "C:\\m.gguf", Port = 8080 },
            Capabilities = capabilities,
            ExtraArguments = "--port 9999",
            AllowExtraArgsOverrideManaged = true,
        });

        Assert.False(result.HasErrors);
        Assert.Contains(result.Issues, i => i.Severity == ValidationSeverity.Warning && i.Field == "ExtraArguments");
        Assert.Equal("9999", result.Tokens[^1]);
    }

    [Fact]
    public void ExtraArguments_UnknownFlag_ProducesWarning()
    {
        var capabilities = HelpFixtures.CreateCapabilities(HelpFixtures.Modern);
        var result = CommandLineBuilder.Build(ExePath, new CommandLineBuildRequest
        {
            Options = new ServerLaunchOptions { ModelPath = "C:\\m.gguf", Port = 8080 },
            Capabilities = capabilities,
            ExtraArguments = "--brand-new-flag 1",
        });

        Assert.False(result.HasErrors);
        Assert.Contains(result.Issues, i => i.Severity == ValidationSeverity.Warning && i.Message.Contains("--brand-new-flag"));
    }

    [Fact]
    public void ExtraArguments_FlagDocumentedInHelp_NoUnknownWarning()
    {
        var capabilities = HelpFixtures.CreateCapabilities(HelpFixtures.Modern);
        var result = CommandLineBuilder.Build(ExePath, new CommandLineBuildRequest
        {
            Options = new ServerLaunchOptions { ModelPath = "C:\\m.gguf", Port = 8080 },
            Capabilities = capabilities,
            ExtraArguments = "--verbose",
            AllowExtraArgsOverrideManaged = true,
        });

        Assert.DoesNotContain(result.Issues, i => i.Message.Contains("未在当前版本帮助中确认"));
    }

    [Fact]
    public void EnvironmentVariables_ValidatedNames()
    {
        var capabilities = HelpFixtures.CreateCapabilities(HelpFixtures.Modern);
        var result = CommandLineBuilder.Build(ExePath, new CommandLineBuildRequest
        {
            Options = new ServerLaunchOptions { ModelPath = "C:\\m.gguf", Port = 8080 },
            Capabilities = capabilities,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["CUDA_VISIBLE_DEVICES"] = "0",
                ["MY_CUSTOM_VAR"] = "1",
                ["BAD-NAME!"] = "x",
            },
        });

        Assert.Contains(result.Issues, i => i.IsBlocking && i.Message.Contains("BAD-NAME!"));
        Assert.Contains(result.Issues, i => !i.IsBlocking && i.Message.Contains("MY_CUSTOM_VAR"));
        Assert.DoesNotContain(result.Issues, i => i.Message.Contains("CUDA_VISIBLE_DEVICES"));
    }

    [Fact]
    public void NoCapabilities_UsesPreferredFlagsWithWarning()
    {
        var result = CommandLineBuilder.Build(ExePath, new CommandLineBuildRequest
        {
            Options = new ServerLaunchOptions { ModelPath = "C:\\m.gguf", Port = 8080, ContextSize = 1024 },
            Capabilities = null,
        });

        Assert.False(result.HasErrors);
        Assert.Contains("--ctx-size", result.Tokens);
        Assert.Contains(result.Issues, i => i.Severity == ValidationSeverity.Warning && i.Field == "Capabilities");
    }

    [Fact]
    public void ApiKeyEnabledWithoutSecret_IsError()
    {
        var capabilities = HelpFixtures.CreateCapabilities(HelpFixtures.Modern);
        var options = CreateOptions();
        options.ApiKeyEnabled = true;

        var result = CommandLineBuilder.Build(ExePath, new CommandLineBuildRequest
        {
            Options = options,
            Capabilities = capabilities,
            ResolvedApiKey = null,
        });

        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i => i.Field == "ApiKey" && i.IsBlocking);
    }

    [Fact]
    public void ApiKeyUnsupportedByVersion_IsError()
    {
        var capabilities = HelpFixtures.CreateCapabilities(HelpFixtures.LegacyInterleaved);
        var options = new ServerLaunchOptions
        {
            ModelPath = "C:\\m.gguf",
            Port = 8080,
            ApiKeyEnabled = true,
        };

        var result = CommandLineBuilder.Build(ExePath, new CommandLineBuildRequest
        {
            Options = options,
            Capabilities = capabilities,
            ResolvedApiKey = "key",
        });

        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i => i.IsBlocking && i.Message.Contains("--api-key"));
    }

    [Fact]
    public void MissingModelPath_IsError()
    {
        var capabilities = HelpFixtures.CreateCapabilities(HelpFixtures.Modern);
        var result = CommandLineBuilder.Build(ExePath, new CommandLineBuildRequest
        {
            Options = new ServerLaunchOptions { Port = 8080 },
            Capabilities = capabilities,
        });

        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i => i.Field == "model" && i.IsBlocking);
    }
}
