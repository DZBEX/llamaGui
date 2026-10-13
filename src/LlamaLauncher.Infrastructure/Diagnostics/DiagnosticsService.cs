using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Security;
using LlamaLauncher.Application.Services;
using LlamaLauncher.Domain;
using LlamaLauncher.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.Diagnostics;

/// <summary>
/// 诊断包：应用日志、实例日志、设置、安装/实例/更新历史、硬件信息。
/// 导出时二次脱敏（模式匹配 + 可选路径替换）；不包含密钥明文与数据库文件。
/// </summary>
public sealed class DiagnosticsService : IDiagnosticsService
{
    private readonly IAppPaths _paths;
    private readonly SqliteDatabase _database;
    private readonly IInstallationRepository _installations;
    private readonly IInstanceRepository _instances;
    private readonly IUpdateOperationRepository _updateOperations;
    private readonly ISettingsService _settings;
    private readonly IHardwareProbeService _hardware;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DiagnosticsService> _logger;

    public DiagnosticsService(
        IAppPaths paths,
        SqliteDatabase database,
        IInstallationRepository installations,
        IInstanceRepository instances,
        IUpdateOperationRepository updateOperations,
        ISettingsService settings,
        IHardwareProbeService hardware,
        TimeProvider timeProvider,
        ILogger<DiagnosticsService> logger)
    {
        _paths = paths;
        _database = database;
        _installations = installations;
        _instances = instances;
        _updateOperations = updateOperations;
        _settings = settings;
        _hardware = hardware;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<DiagnosticsResult> ExportBundleAsync(
        string destinationZipPath,
        bool includeUserPaths,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var destination = Path.GetFullPath(destinationZipPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            await using var fileStream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);

            // 1) 日志文件（再次脱敏）
            if (Directory.Exists(_paths.LogsDirectory))
            {
                foreach (var logFile in Directory.EnumerateFiles(_paths.LogsDirectory, "*.log"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var text = await ReadAllTextSharedAsync(logFile, cancellationToken).ConfigureAwait(false);
                    AddTextEntry(archive, "logs/" + Path.GetFileName(logFile), Sanitize(text, includeUserPaths));
                }
            }

            // 2) 结构化报告
            var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
            AddTextEntry(
                archive,
                "reports/settings.json",
                Sanitize(JsonSerializer.Serialize(settings, JsonDefaults.Export), includeUserPaths));

            var installations = await _installations.GetAllAsync(cancellationToken).ConfigureAwait(false);
            AddJsonEntry(archive, "reports/installations.json", installations.Select(i => new
            {
                i.Id,
                i.VersionLabel,
                i.BuildInfo,
                i.AssetName,
                i.SourceKind,
                Directory = SanitizePath(i.Directory, includeUserPaths),
                ExecutablePath = SanitizePath(i.ExecutablePath, includeUserPaths),
                i.Sha256,
                i.Sha256FromOfficialDigest,
                i.Status,
                i.IsDefault,
                i.BackendTags,
                i.InstalledUtc,
                i.LastProbedUtc,
            }));

            var instances = await _instances.GetAllAsync(cancellationToken).ConfigureAwait(false);
            AddJsonEntry(archive, "reports/instances.json", instances.Select(i => new
            {
                i.Id,
                i.Name,
                i.State,
                i.Host,
                i.Port,
                i.ProcessId,
                i.Generation,
                i.StartedUtc,
                i.StoppedUtc,
                i.ExitCode,
                i.LastError,
            }));

            var updates = await _updateOperations.GetRecentAsync(50, cancellationToken).ConfigureAwait(false);
            AddJsonEntry(archive, "reports/update-history.json", updates);

            var hardware = await _hardware.ProbeAsync(cancellationToken).ConfigureAwait(false);
            AddJsonEntry(archive, "reports/hardware.json", hardware);

            var environment = new StringBuilder()
                .AppendLine($"导出时间(UTC)：{_timeProvider.GetUtcNow():O}")
                .AppendLine($"应用版本：{typeof(DiagnosticsService).Assembly.GetName().Version}")
                .AppendLine($"OS：{hardware.OsName} {hardware.OsVersion} build {hardware.OsBuild}")
                .AppendLine($".NET：{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}")
                .AppendLine($"数据目录：{SanitizePath(_paths.DataRoot, includeUserPaths)}")
                .AppendLine($"数据库：{SanitizePath(_database.DatabasePath, includeUserPaths)}")
                .AppendLine("说明：诊断包不包含密钥明文与聊天数据库文件；路径已按用户选择脱敏。")
                .ToString();
            AddTextEntry(archive, "reports/environment.txt", Sanitize(environment, includeUserPaths));

            return new DiagnosticsResult(true, destination, "诊断包已导出。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导出诊断包失败");
            return new DiagnosticsResult(false, null, "导出诊断包失败：" + ex.Message);
        }
    }

    private static void AddTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static async Task<string> ReadAllTextSharedAsync(string path, CancellationToken cancellationToken)
    {
        // 日志文件可能正被实例日志通道以写模式打开（FileShare.ReadWrite），需允许读写共享
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddJsonEntry<T>(ZipArchive archive, string entryName, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonDefaults.Export);
        AddTextEntry(archive, entryName, json);
    }

    private string Sanitize(string text, bool includeUserPaths)
    {
        var masked = SecretMasker.MaskText(text);
        if (includeUserPaths)
        {
            return masked;
        }

        return MaskPaths(masked);
    }

    private string SanitizePath(string path, bool includeUserPaths)
        => includeUserPaths ? path : MaskPaths(path);

    private static string MaskPaths(string text)
    {
        var replacements = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        var result = text;
        foreach (var value in replacements)
        {
            if (!string.IsNullOrEmpty(value))
            {
                result = result.Replace(value, "<user-path>", StringComparison.OrdinalIgnoreCase);
            }
        }

        return result;
    }
}
