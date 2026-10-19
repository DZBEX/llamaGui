using LlamaLauncher.Domain;

namespace LlamaLauncher.Application.Services;

/// <summary>解析后的 llama.cpp 程序引用。</summary>
public sealed record InstallationResolution(
    LlamaCppInstallation? Installation,
    string? ExecutablePath,
    bool UsedFallback);

/// <summary>
/// 启动时解析可用的 llama.cpp 程序。
/// 配置可能来自另一台电脑（绝对路径已失效），或引用的安装记录已被移除；
/// 此时按“同版本可执行文件 → 当前默认版本 → 任一可用版本”的顺序回退。
/// </summary>
public static class InstallationResolver
{
    public static InstallationResolution Resolve(
        LlamaCppInstallation? bound,
        string? snapshotExecutablePath,
        IReadOnlyList<LlamaCppInstallation> allInstallations)
    {
        ArgumentNullException.ThrowIfNull(allInstallations);

        if (bound is not null && File.Exists(bound.ExecutablePath))
        {
            return new InstallationResolution(bound, bound.ExecutablePath, false);
        }

        if (!string.IsNullOrWhiteSpace(snapshotExecutablePath) && File.Exists(snapshotExecutablePath))
        {
            var matching = allInstallations.FirstOrDefault(
                i => PathEquals(i.ExecutablePath, snapshotExecutablePath));
            return new InstallationResolution(matching ?? bound, snapshotExecutablePath, true);
        }

        var candidates = allInstallations
            .Where(i => !string.IsNullOrWhiteSpace(i.ExecutablePath) && File.Exists(i.ExecutablePath))
            .OrderByDescending(i => i.Status == InstallationStatus.Available)
            .ThenByDescending(i => i.IsDefault)
            .ToList();
        if (candidates.Count == 0)
        {
            return new InstallationResolution(null, null, true);
        }

        var versionLabel = bound?.VersionLabel;
        var selected = candidates.FirstOrDefault(i =>
                           !string.IsNullOrWhiteSpace(versionLabel)
                           && string.Equals(i.VersionLabel, versionLabel, StringComparison.OrdinalIgnoreCase))
                       ?? candidates.FirstOrDefault(i => i.IsDefault)
                       ?? candidates[0];

        return new InstallationResolution(selected, selected.ExecutablePath, true);
    }

    public static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
