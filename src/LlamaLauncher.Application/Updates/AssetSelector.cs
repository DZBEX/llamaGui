using System.Text.RegularExpressions;
using LlamaLauncher.Application.Models;
using LlamaLauncher.Domain;

namespace LlamaLauncher.Application.Updates;

/// <summary>
/// 从 Release 资产列表筛选适用于 Windows x64 的 llama.cpp 程序包。
/// 该逻辑是纯函数：只依据文件名与发布元数据分类；不假设任何 GPU 一定兼容某个 CUDA 版本。
/// </summary>
public static partial class AssetSelector
{
    // 兼容官方两代命名：旧式 "cu12.4" / "cu13"，新式 "cuda-12.4" / "cuda-13.4"（也有 "cuda12.4" 的变体）。
    [GeneratedRegex(@"(?:cu|cuda)[-._]?(\d{1,2})(?:[.-](\d{1,2}))?", RegexOptions.IgnoreCase)]
    private static partial Regex CudaVersionPattern();

    /// <summary>筛选候选资产（保持发布顺序），并给出推荐项。</summary>
    public static IReadOnlyList<AssetCandidate> SelectCandidates(
        ReleaseInfo release,
        IReadOnlyCollection<BackendKind>? localBackendHints = null)
    {
        ArgumentNullException.ThrowIfNull(release);
        var candidates = new List<AssetCandidate>();
        foreach (var asset in release.Assets)
        {
            var candidate = Classify(asset, release.TagName);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        MarkRecommended(candidates, localBackendHints ?? Array.Empty<BackendKind>());
        return candidates;
    }

    /// <summary>单个资产分类；不适用返回 null。</summary>
    public static AssetCandidate? Classify(ReleaseAsset asset, string releaseTag)
    {
        var name = asset.Name;
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var lower = name.ToLowerInvariant();
        if (!lower.Contains("win", StringComparison.Ordinal))
        {
            return null;
        }

        if (!(lower.Contains("x64") || lower.Contains("x86_64") || lower.Contains("amd64")))
        {
            return null;
        }

        if (lower.Contains("arm64") || lower.Contains("aarch64"))
        {
            return null;
        }

        var isSupport = lower.Contains("cudart", StringComparison.Ordinal);
        var backend = ClassifyBackend(lower);
        string? cudaVersion = null;
        if (backend == BackendKind.Cuda || isSupport)
        {
            var match = CudaVersionPattern().Match(lower);
            if (match.Success)
            {
                cudaVersion = match.Groups[2].Success
                    ? $"{match.Groups[1].Value}.{match.Groups[2].Value}"
                    : match.Groups[1].Value;
            }
        }

        var note = backend == BackendKind.Unknown && !isSupport
            ? "未识别构建类型（文件名未包含已知后端标记），请查看发布说明确认。"
            : isSupport
                ? "CUDA 运行时辅助包（需与 CUDA 主程序包配合）。"
                : string.Empty;

        return new AssetCandidate
        {
            Asset = asset,
            ReleaseTag = releaseTag,
            Backend = isSupport ? BackendKind.Cuda : backend,
            CudaVersion = cudaVersion,
            IsSupportFile = isSupport,
            Note = note,
        };
    }

    private static BackendKind ClassifyBackend(string loweredName)
    {
        if (loweredName.Contains("cuda", StringComparison.Ordinal)
            || Regex.IsMatch(loweredName, @"(^|[^a-z])cu\d", RegexOptions.IgnoreCase))
        {
            return BackendKind.Cuda;
        }

        if (loweredName.Contains("vulkan", StringComparison.Ordinal))
        {
            return BackendKind.Vulkan;
        }

        if (loweredName.Contains("sycl", StringComparison.Ordinal))
        {
            return BackendKind.Sycl;
        }

        if (loweredName.Contains("hip", StringComparison.Ordinal)
            || loweredName.Contains("rocm", StringComparison.Ordinal))
        {
            return BackendKind.Rocm;
        }

        if (loweredName.Contains("openvino", StringComparison.Ordinal))
        {
            return BackendKind.OpenVino;
        }

        if (loweredName.Contains("cpu", StringComparison.Ordinal)
            || loweredName.Contains("avx", StringComparison.Ordinal))
        {
            return BackendKind.Cpu;
        }

        return BackendKind.Unknown;
    }

    private static void MarkRecommended(
        List<AssetCandidate> candidates,
        IReadOnlyCollection<BackendKind> localHints)
    {
        var mainCandidates = candidates.Where(c => !c.IsSupportFile).ToList();
        if (mainCandidates.Count == 0)
        {
            return;
        }

        // 优先级：本机提示的后端 > CUDA > Vulkan > SYCL > ROCm > CPU > 未识别
        static int Priority(BackendKind backend, IReadOnlyCollection<BackendKind> hints)
        {
            if (hints.Contains(backend))
            {
                return 0;
            }

            return backend switch
            {
                BackendKind.Cuda => 1,
                BackendKind.Vulkan => 2,
                BackendKind.Sycl => 3,
                BackendKind.Rocm => 4,
                BackendKind.Cpu => 5,
                _ => 6,
            };
        }

        var best = mainCandidates
            .OrderBy(c => Priority(c.Backend, localHints))
            .ThenByDescending(c => ParseCudaForOrder(c.CudaVersion))
            .First();

        var index = candidates.IndexOf(best);
        candidates[index] = new AssetCandidate
        {
            Asset = best.Asset,
            ReleaseTag = best.ReleaseTag,
            Backend = best.Backend,
            CudaVersion = best.CudaVersion,
            IsSupportFile = best.IsSupportFile,
            Recommended = true,
            Note = best.Note,
        };
    }

    private static double ParseCudaForOrder(string? cudaVersion)
        => cudaVersion is not null
            && double.TryParse(cudaVersion, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;

    /// <summary>
    /// 为 CUDA 主程序包匹配同一发行版中的 cudart 运行时辅助包。
    /// 只做可靠匹配：CUDA 版本号完全一致优先；其次“同一主版本且唯一”；主包未标注版本时仅有一个辅助包才匹配；
    /// 其余情况返回 null（不猜测，避免装错运行时）。
    /// </summary>
    public static AssetCandidate? FindCudartForMainAsset(ReleaseInfo release, ReleaseAsset mainAsset)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(mainAsset);

        var main = Classify(mainAsset, release.TagName);
        if (main is null || main.IsSupportFile || main.Backend != BackendKind.Cuda)
        {
            return null;
        }

        var supports = release.Assets
            .Select(asset => Classify(asset, release.TagName))
            .Where(candidate => candidate is { IsSupportFile: true })
            .Select(candidate => candidate!)
            .ToList();
        if (supports.Count == 0)
        {
            return null;
        }

        if (main.CudaVersion is { Length: > 0 } version)
        {
            var exact = supports.FirstOrDefault(
                candidate => string.Equals(candidate.CudaVersion, version, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }

            var major = version.Split('.')[0];
            var sameMajor = supports
                .Where(candidate => candidate.CudaVersion is { Length: > 0 }
                    && string.Equals(candidate.CudaVersion.Split('.')[0], major, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return sameMajor.Count == 1 ? sameMajor[0] : null;
        }

        // 主包文件名没有 CUDA 版本号：只有唯一辅助包时才认为可以自动匹配。
        return supports.Count == 1 ? supports[0] : null;
    }
}
