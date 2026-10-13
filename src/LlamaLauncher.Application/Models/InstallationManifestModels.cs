namespace LlamaLauncher.Application.Models;

/// <summary>安装清单数据（指针 + 条目镜像，用于崩溃恢复与审计）。</summary>
public sealed class InstallationManifestData
{
    public int SchemaVersion { get; set; } = 1;

    public Guid? DefaultInstallationId { get; set; }

    public Guid? PreviousDefaultInstallationId { get; set; }

    public List<InstallationManifestEntry> Entries { get; set; } = new();

    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>清单中的单个安装条目。</summary>
public sealed class InstallationManifestEntry
{
    public Guid Id { get; set; }

    public string VersionLabel { get; set; } = string.Empty;

    public string Directory { get; set; } = string.Empty;

    public string ExecutablePath { get; set; } = string.Empty;

    public string? Sha256 { get; set; }
}

/// <summary>安装清单存储（原子写入 + 备份恢复）。</summary>
public interface IInstallationManifestStore
{
    string ManifestPath { get; }

    InstallationManifestData Load();

    void Save(InstallationManifestData manifest);
}
