using System.Text.Json;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Models;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.Updates;

/// <summary>
/// 原子写入的安装清单文件（manifests/installations.json）：
/// 先写临时文件再替换，并保留 .bak 供崩溃后恢复。
/// </summary>
public sealed class InstallationManifestStore : IInstallationManifestStore
{
    private readonly string _manifestPath;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InstallationManifestStore> _logger;
    private readonly object _gate = new();

    public InstallationManifestStore(
        IAppPaths paths,
        TimeProvider timeProvider,
        ILogger<InstallationManifestStore> logger)
    {
        _manifestPath = paths.InstallationsManifestPath;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string ManifestPath => _manifestPath;

    public InstallationManifestData Load()
    {
        lock (_gate)
        {
            var primary = TryRead(_manifestPath);
            if (primary is not null)
            {
                return primary;
            }

            var backupPath = _manifestPath + ".bak";
            var backup = TryRead(backupPath);
            if (backup is not null)
            {
                _logger.LogWarning("安装清单主文件不可用，已从备份恢复：{Path}", backupPath);
                return backup;
            }

            return new InstallationManifestData { UpdatedUtc = _timeProvider.GetUtcNow() };
        }
    }

    public void Save(InstallationManifestData manifest)
    {
        lock (_gate)
        {
            manifest.UpdatedUtc = _timeProvider.GetUtcNow();
            var directory = Path.GetDirectoryName(_manifestPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = _manifestPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(manifest, JsonDefaults.Export));

            if (File.Exists(_manifestPath))
            {
                File.Replace(tempPath, _manifestPath, _manifestPath + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _manifestPath);
            }
        }
    }

    private static InstallationManifestData? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<InstallationManifestData>(File.ReadAllText(path), JsonDefaults.Storage);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
