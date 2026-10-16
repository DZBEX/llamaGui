using System.Text.Json;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Application.Services;

/// <summary>应用设置模型（版本化 JSON 存储）。</summary>
public sealed class AppSettingsModel
{
    /// <summary>主题：dark / light。</summary>
    public string Theme { get; set; } = "dark";

    public string Language { get; set; } = "zh-CN";

    /// <summary>关闭主窗口时最小化到托盘（否则退出应用）。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>启动时直接进入托盘（不显示主窗口）。默认关闭。</summary>
    public bool StartMinimizedToTray { get; set; }

    /// <summary>开机自启动（默认关闭，用户显式开启）。</summary>
    public bool StartWithWindows { get; set; }

    public bool AutoCheckUpdates { get; set; }

    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;

    public int UpdateCheckIntervalHours { get; set; } = 24;

    public int ResourceSampleIntervalSeconds { get; set; } = 2;

    public int LogRetentionDays { get; set; } = 14;

    public long LogMaxFileBytes { get; set; } = 20 * 1024 * 1024;

    public int LogMaxRotatedFiles { get; set; } = 3;

    public int LogMemoryBufferLines { get; set; } = 5000;

    /// <summary>退出应用时停止由本应用启动的服务（安全默认）。</summary>
    public bool StopServicesOnExit { get; set; } = true;

    /// <summary>llama.cpp 版本目录（null = 数据目录下 runtimes\llama.cpp）。</summary>
    public string? RuntimesDirectory { get; set; }

    /// <summary>更新包下载目录（null = 数据目录下 downloads）。</summary>
    public string? DownloadsDirectory { get; set; }

    /// <summary>模型目录（模型库“添加文件/扫描目录”的默认起始目录；null = 沿用上次使用目录）。</summary>
    public string? ModelsDirectory { get; set; }

    public string? LastModelScanDirectory { get; set; }

    public string? LastUpdateCheckUtc { get; set; }

    /// <summary>最近一次使用的启动配置（用于快速启动）。</summary>
    public string? LastUsedProfileId { get; set; }
}

/// <summary>应用设置读写。</summary>
public interface ISettingsService
{
    Task<AppSettingsModel> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettingsModel settings, CancellationToken cancellationToken = default);
}

/// <summary>设置服务实现（单键 JSON 存储，加载失败回退默认值）。</summary>
public sealed class SettingsService : ISettingsService
{
    private const string SettingsKey = "app.settings.v1";

    private readonly ISettingsRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SettingsService> _logger;

    public SettingsService(
        ISettingsRepository repository,
        TimeProvider timeProvider,
        ILogger<SettingsService> logger)
    {
        _repository = repository;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<AppSettingsModel> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var record = await _repository.GetAsync(SettingsKey, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                return new AppSettingsModel();
            }

            return JsonSerializer.Deserialize<AppSettingsModel>(record.ValueJson, JsonDefaults.Storage)
                ?? new AppSettingsModel();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取设置失败，使用默认设置");
            return new AppSettingsModel();
        }
    }

    public async Task SaveAsync(AppSettingsModel settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.Serialize(settings, JsonDefaults.Storage);
        await _repository
            .SetAsync(SettingsKey, json, _timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
    }
}
