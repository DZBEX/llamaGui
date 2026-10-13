using LlamaLauncher.Application.Models;
using LlamaLauncher.Application.Updates;
using LlamaLauncher.Domain;
using Xunit;

namespace LlamaLauncher.UnitTests.Updates;

public sealed class AssetSelectorTests
{
    private static ReleaseInfo CreateRelease(params string[] assetNames)
        => new()
        {
            TagName = "b4567",
            PublishedAtUtc = DateTimeOffset.UtcNow,
            Assets = assetNames.Select(n => new ReleaseAsset
            {
                Name = n,
                DownloadUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b4567/" + n,
                SizeBytes = 100_000_000,
            }).ToList(),
        };

    [Fact]
    public void FiltersNonWindowsAndNonX64Assets()
    {
        var release = CreateRelease(
            "llama-b4567-bin-macos-arm64.zip",
            "llama-b4567-bin-macos-x64.zip",
            "llama-b4567-bin-win-arm64.zip",
            "llama-b4567-bin-win-cpu-x64.zip",
            "llama-b4567-source.zip",
            "llama-b4567-bin-win-cuda-cu12.4-x64.zip");

        var candidates = AssetSelector.SelectCandidates(release);

        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, c => c.Asset.Name.Contains("cpu-x64"));
        Assert.Contains(candidates, c => c.Asset.Name.Contains("cuda-cu12.4-x64"));
    }

    [Fact]
    public void ClassifiesBackendsAndCudaVersion()
    {
        var release = CreateRelease(
            "llama-b4567-bin-win-cuda-cu12.4-x64.zip",
            "llama-b4567-bin-win-vulkan-x64.zip",
            "llama-b4567-bin-win-sycl-x64.zip",
            "llama-b4567-bin-win-hip-x64.zip",
            "llama-b4567-bin-win-cpu-x64.zip",
            "llama-b4567-bin-win-avx2-x64.zip",
            "cudart-llama-bin-win-cu12.4-x64.zip");

        var candidates = AssetSelector.SelectCandidates(release);
        var byName = candidates.ToDictionary(c => c.Asset.Name, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(BackendKind.Cuda, byName["llama-b4567-bin-win-cuda-cu12.4-x64.zip"].Backend);
        Assert.Equal("12.4", byName["llama-b4567-bin-win-cuda-cu12.4-x64.zip"].CudaVersion);
        Assert.Equal(BackendKind.Vulkan, byName["llama-b4567-bin-win-vulkan-x64.zip"].Backend);
        Assert.Equal(BackendKind.Sycl, byName["llama-b4567-bin-win-sycl-x64.zip"].Backend);
        Assert.Equal(BackendKind.Rocm, byName["llama-b4567-bin-win-hip-x64.zip"].Backend);
        Assert.Equal(BackendKind.Cpu, byName["llama-b4567-bin-win-cpu-x64.zip"].Backend);
        Assert.Equal(BackendKind.Cpu, byName["llama-b4567-bin-win-avx2-x64.zip"].Backend);
        Assert.True(byName["cudart-llama-bin-win-cu12.4-x64.zip"].IsSupportFile);
    }

    [Fact]
    public void Recommended_PrefersLocalBackendHints()
    {
        var release = CreateRelease(
            "llama-b4567-bin-win-cuda-cu12.4-x64.zip",
            "llama-b4567-bin-win-vulkan-x64.zip",
            "llama-b4567-bin-win-cpu-x64.zip");

        var candidates = AssetSelector.SelectCandidates(release, new[] { BackendKind.Vulkan });

        var recommended = Assert.Single(candidates, c => c.Recommended);
        Assert.Equal(BackendKind.Vulkan, recommended.Backend);
    }

    [Fact]
    public void Recommended_DefaultsToCudaWithoutHints()
    {
        var release = CreateRelease(
            "llama-b4567-bin-win-vulkan-x64.zip",
            "llama-b4567-bin-win-cuda-cu12.4-x64.zip",
            "llama-b4567-bin-win-cpu-x64.zip");

        var candidates = AssetSelector.SelectCandidates(release);

        var recommended = Assert.Single(candidates, c => c.Recommended);
        Assert.Equal(BackendKind.Cuda, recommended.Backend);
    }

    [Fact]
    public void UnknownBuild_IsMarkedWithNote()
    {
        var release = CreateRelease("llama-b4567-bin-win-x64.zip");

        var candidates = AssetSelector.SelectCandidates(release);

        var candidate = Assert.Single(candidates);
        Assert.Equal(BackendKind.Unknown, candidate.Backend);
        Assert.Contains("未识别", candidate.Note);
    }

    [Fact]
    public void SupportFile_IsNotRecommendedOverMainPackage()
    {
        var release = CreateRelease("cudart-llama-bin-win-cu12.4-x64.zip");

        var candidates = AssetSelector.SelectCandidates(release);

        var candidate = Assert.Single(candidates);
        Assert.False(candidate.Recommended);
        Assert.True(candidate.IsSupportFile);
    }

    [Fact]
    public void CudartMatching_PrefersExactCudaVersion()
    {
        var release = CreateRelease(
            "llama-b4567-bin-win-cuda-cu12.4-x64.zip",
            "cudart-llama-bin-win-cu13.0-x64.zip",
            "cudart-llama-bin-win-cu12.4-x64.zip");

        var pack = AssetSelector.FindCudartForMainAsset(release, release.Assets[0]);

        Assert.NotNull(pack);
        Assert.Equal("cudart-llama-bin-win-cu12.4-x64.zip", pack!.Asset.Name);
    }

    [Fact]
    public void CudartMatching_UsesSameMajorWhenUnique()
    {
        var release = CreateRelease(
            "llama-b4567-bin-win-cuda-cu12.4-x64.zip",
            "cudart-llama-bin-win-cu12.6-x64.zip");

        var pack = AssetSelector.FindCudartForMainAsset(release, release.Assets[0]);

        Assert.NotNull(pack);
        Assert.Equal("12.6", pack!.CudaVersion);
    }

    [Fact]
    public void CudartMatching_GenericMainWithSingleSupport_Matches()
    {
        var release = CreateRelease(
            "llama-b4567-bin-win-cuda-x64.zip",
            "cudart-llama-bin-win-cu12.4-x64.zip");

        var pack = AssetSelector.FindCudartForMainAsset(release, release.Assets[0]);

        Assert.NotNull(pack);
        Assert.Equal("12.4", pack!.CudaVersion);
    }

    [Fact]
    public void CudartMatching_AmbiguousOrInapplicable_ReturnsNull()
    {
        // 主包未标 CUDA 版本 + 多个辅助包 → 不猜测
        var ambiguous = CreateRelease(
            "llama-b4567-bin-win-cuda-x64.zip",
            "cudart-llama-bin-win-cu12.4-x64.zip",
            "cudart-llama-bin-win-cu13.0-x64.zip");
        Assert.Null(AssetSelector.FindCudartForMainAsset(ambiguous, ambiguous.Assets[0]));

        // 非 CUDA 主包 → null
        var cpu = CreateRelease(
            "llama-b4567-bin-win-cpu-x64.zip",
            "cudart-llama-bin-win-cu12.4-x64.zip");
        Assert.Null(AssetSelector.FindCudartForMainAsset(cpu, cpu.Assets[0]));

        // 没有辅助包 → null
        var none = CreateRelease("llama-b4567-bin-win-cuda-cu12.4-x64.zip");
        Assert.Null(AssetSelector.FindCudartForMainAsset(none, none.Assets[0]));
    }

    [Fact]
    public void CudartMatching_SupportsNewCudaDashNaming()
    {
        var release = CreateRelease(
            "llama-b4567-bin-win-cuda-12.4-x64.zip",
            "cudart-llama-bin-win-cuda-12.4-x64.zip",
            "cudart-llama-bin-win-cuda-13.4-x64.zip");

        var byName = AssetSelector.SelectCandidates(release)
            .ToDictionary(c => c.Asset.Name, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("12.4", byName["llama-b4567-bin-win-cuda-12.4-x64.zip"].CudaVersion);
        Assert.Equal("12.4", byName["cudart-llama-bin-win-cuda-12.4-x64.zip"].CudaVersion);
        Assert.Equal("13.4", byName["cudart-llama-bin-win-cuda-13.4-x64.zip"].CudaVersion);

        var pack = AssetSelector.FindCudartForMainAsset(release, release.Assets[0]);

        Assert.NotNull(pack);
        Assert.Equal("cudart-llama-bin-win-cuda-12.4-x64.zip", pack!.Asset.Name);
    }
}
