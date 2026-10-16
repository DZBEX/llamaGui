using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Exchange;
using LlamaLauncher.Application.Services;
using LlamaLauncher.App.Services;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace LlamaLauncher.App.ViewModels;

/// <summary>备份行。</summary>
public sealed record BackupRow(BackupFileInfo Info)
{
    public string CreatedText => Formatting.LocalTime(Info.CreatedUtc);

    public string SizeText => Formatting.Bytes(Info.SizeBytes);

    public string PathText => Info.FilePath;
}

/// <summary>设置页。</summary>
public sealed partial class SettingsViewModel : ViewModelBase, IPageViewModel
{
    private readonly ISettingsService _settings;
    private readonly IThemeService _themeService;
    private readonly IResourceMonitorService _resources;
    private readonly IInstanceLogStore _logStore;
    private readonly ISecretStore _secrets;
    private readonly IBackupRestoreService _backups;
    private readonly IDiagnosticsService _diagnostics;
    private readonly IAppPaths _paths;
    private readonly ILogger<SettingsViewModel> _logger;

    [ObservableProperty]
    private string _theme = "dark";

    [ObservableProperty]
    private bool _closeToTray = true;

    [ObservableProperty]
    private bool _startMinimizedToTray;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _stopServicesOnExit = true;

    [ObservableProperty]
    private UpdateChannel _updateChannel = UpdateChannel.Stable;

    [ObservableProperty]
    private bool _autoCheckUpdates;

    [ObservableProperty]
    private string _updateIntervalText = "24";

    [ObservableProperty]
    private string _resourceIntervalText = "2";

    [ObservableProperty]
    private string _logRetentionText = "14";

    [ObservableProperty]
    private string _logMaxSizeText = "20";

    [ObservableProperty]
    private string _logRotatedCountText = "3";

    [ObservableProperty]
    private string _apiKeyStatus = "（未配置）";

    [ObservableProperty]
    private string _gitHubTokenStatus = "（未配置，使用未登录配额：每小时 60 次）";

    [ObservableProperty]
    private string _dataRootText = string.Empty;

    [ObservableProperty]
    private string _runtimesDirectoryText = string.Empty;

    [ObservableProperty]
    private string _downloadsDirectoryText = string.Empty;

    [ObservableProperty]
    private string _modelsDirectoryText = string.Empty;

    [ObservableProperty]
    private string _runtimesDirectoryHint = string.Empty;

    [ObservableProperty]
    private string _downloadsDirectoryHint = string.Empty;

    [ObservableProperty]
    private string _modelsDirectoryHint = string.Empty;

    [ObservableProperty]
    private bool _isPortableMode;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private BackupRow? _selectedBackup;

    public SettingsViewModel(
        ISettingsService settings,
        IThemeService theme,
        IResourceMonitorService resources,
        IInstanceLogStore logStore,
        ISecretStore secrets,
        IBackupRestoreService backups,
        IDiagnosticsService diagnostics,
        IAppPaths paths,
        IProfileExchangeService exchange,
        IDialogService dialogs,
        INotificationService notifications,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _themeService = theme;
        _resources = resources;
        _logStore = logStore;
        _secrets = secrets;
        _backups = backups;
        _diagnostics = diagnostics;
        _paths = paths;
        _exchange = exchange;
        _logger = logger;
        Dialogs = dialogs;
        Notifications = notifications;

        DataRootText = paths.DataRoot;
        IsPortableMode = string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.DataRoot)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)),
            StringComparison.OrdinalIgnoreCase);
    }

    private readonly IProfileExchangeService _exchange;

    public ChoiceOption<string>[] ThemeChoices { get; } =
    {
        new("dark", "深色"),
        new("light", "浅色"),
    };

    public ChoiceOption<UpdateChannel>[] ChannelChoices { get; } =
    {
        new(UpdateChannel.Stable, "稳定版"),
        new(UpdateChannel.Prerelease, "预发布版"),
    };

    public ObservableCollection<BackupRow> Backups { get; } = new();

    private string? _runtimesDirectorySetting;

    private string? _downloadsDirectorySetting;

    private string? _modelsDirectorySetting;

    public string AppVersionText =>
        "LlamaLauncher " + (typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    public string AboutText =>
        "llama.cpp 的 Windows 桌面启动器：多实例管理、能力探测、内置流式聊天、程序版本更新与回滚。\n" +
        "本应用不下载 GGUF 模型；llama.cpp 程序更新仅信任官方 ggml-org/llama.cpp 发布源。\n" +
        "密钥使用 Windows DPAPI 保护，不写入日志、导出与诊断包。";

    public async Task OnActivatedAsync()
    {
        var settings = await _settings.LoadAsync();
        Theme = settings.Theme;
        CloseToTray = settings.CloseToTray;
        StartMinimizedToTray = settings.StartMinimizedToTray;
        StopServicesOnExit = settings.StopServicesOnExit;
        UpdateChannel = settings.UpdateChannel;
        AutoCheckUpdates = settings.AutoCheckUpdates;
        UpdateIntervalText = settings.UpdateCheckIntervalHours.ToString();
        ResourceIntervalText = settings.ResourceSampleIntervalSeconds.ToString();
        LogRetentionText = settings.LogRetentionDays.ToString();
        LogMaxSizeText = (settings.LogMaxFileBytes / 1024 / 1024).ToString();
        LogRotatedCountText = settings.LogMaxRotatedFiles.ToString();
        StartWithWindows = IsStartWithWindowsEnabled();

        _runtimesDirectorySetting = string.IsNullOrWhiteSpace(settings.RuntimesDirectory) ? null : settings.RuntimesDirectory;
        _downloadsDirectorySetting = string.IsNullOrWhiteSpace(settings.DownloadsDirectory) ? null : settings.DownloadsDirectory;
        _modelsDirectorySetting = string.IsNullOrWhiteSpace(settings.ModelsDirectory) ? null : settings.ModelsDirectory;
        RuntimesDirectoryText = _paths.RuntimesDirectory;
        DownloadsDirectoryText = _paths.DownloadsDirectory;
        ModelsDirectoryText = _modelsDirectorySetting ?? "（未设置：默认使用上次扫描/添加的目录）";
        RefreshDirectoryHints();

        await RefreshApiKeyStatusAsync();
        await RefreshGitHubTokenStatusAsync();
        RefreshBackups();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        await RunBusyAsync("正在保存设置…", async () =>
        {
            var settings = await _settings.LoadAsync();
            settings.Theme = Theme;
            settings.CloseToTray = CloseToTray;
            settings.StartMinimizedToTray = StartMinimizedToTray;
            settings.StopServicesOnExit = StopServicesOnExit;
            settings.UpdateChannel = UpdateChannel;
            settings.AutoCheckUpdates = AutoCheckUpdates;
            if (int.TryParse(UpdateIntervalText, out var intervalHours) && intervalHours is >= 1 and <= 168)
            {
                settings.UpdateCheckIntervalHours = intervalHours;
            }

            if (int.TryParse(ResourceIntervalText, out var resourceSeconds) && resourceSeconds is >= 1 and <= 60)
            {
                settings.ResourceSampleIntervalSeconds = resourceSeconds;
            }

            if (int.TryParse(LogRetentionText, out var retentionDays) && retentionDays is >= 1 and <= 365)
            {
                settings.LogRetentionDays = retentionDays;
            }

            if (long.TryParse(LogMaxSizeText, out var maxMb) && maxMb is >= 1 and <= 1024)
            {
                settings.LogMaxFileBytes = maxMb * 1024 * 1024;
            }

            if (int.TryParse(LogRotatedCountText, out var rotated) && rotated is >= 0 and <= 20)
            {
                settings.LogMaxRotatedFiles = rotated;
            }

            settings.RuntimesDirectory = _runtimesDirectorySetting;
            settings.DownloadsDirectory = _downloadsDirectorySetting;
            settings.ModelsDirectory = _modelsDirectorySetting;

            await _settings.SaveAsync(settings);

            // 目录覆盖立即生效；若自定义目录不可用则回退默认并把设置改回，避免“显示与实际不一致”
            var warnings = _paths.ApplyDirectoryOverrides(settings.RuntimesDirectory, settings.DownloadsDirectory);
            var settingsAdjusted = false;
            if (_runtimesDirectorySetting is not null
                && !PathsEqual(_paths.RuntimesDirectory, _runtimesDirectorySetting))
            {
                _runtimesDirectorySetting = null;
                settings.RuntimesDirectory = null;
                settingsAdjusted = true;
            }

            if (_downloadsDirectorySetting is not null
                && !PathsEqual(_paths.DownloadsDirectory, _downloadsDirectorySetting))
            {
                _downloadsDirectorySetting = null;
                settings.DownloadsDirectory = null;
                settingsAdjusted = true;
            }

            if (settingsAdjusted)
            {
                await _settings.SaveAsync(settings);
            }

            foreach (var warning in warnings)
            {
                Notifications?.Notify(warning);
            }

            RuntimesDirectoryText = _paths.RuntimesDirectory;
            DownloadsDirectoryText = _paths.DownloadsDirectory;
            RefreshDirectoryHints();

            _themeService.Apply(settings.Theme);
            _resources.Interval = TimeSpan.FromSeconds(settings.ResourceSampleIntervalSeconds);
            if (_logStore is Infrastructure.Logging.InstanceLogStore store)
            {
                store.Options.MaxFileBytes = settings.LogMaxFileBytes;
                store.Options.MaxRotatedFiles = settings.LogMaxRotatedFiles;
            }

            SetStartWithWindows(StartWithWindows);
            StatusText = "设置已保存。";
            Notifications?.Notify("设置已保存。");
        });
    }

    [RelayCommand]
    private async Task ResetToDefaultsAsync()
    {
        var confirmed = await Dialogs!.ConfirmAsync(new ConfirmDialogOptions
        {
            Title = "恢复默认设置",
            Message = "将把应用设置恢复为默认值（不影响模型文件、已安装版本、聊天记录与实例）。",
            PrimaryButtonText = "恢复默认",
            SecondaryButtonText = "取消",
            IsDestructive = true,
        });
        if (!confirmed)
        {
            return;
        }

        var defaults = new AppSettingsModel();
        await _settings.SaveAsync(defaults);
        _paths.ApplyDirectoryOverrides(null, null);
        _themeService.Apply(defaults.Theme);
        await OnActivatedAsync();
        Notifications?.Notify("设置已恢复默认值。");
    }

    [RelayCommand]
    private async Task SetApiKeyAsync()
    {
        var value = await Dialogs!.PromptWithConfirmationAsync(
            "配置默认 API Key",
            "输入要保存的 API Key（将使用 Windows DPAPI 加密存储，仅本机当前用户可解密）。\n" +
            "为防误操作，请在下方再次输入该 Key 以确认。",
            "确认输入：请粘贴同一个 API Key");
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        await RunBusyAsync("正在保存 API Key…", async () =>
        {
            await _secrets.SetAsync(SecretNames.DefaultApiKey, value.Trim());
            await RefreshApiKeyStatusAsync();
            Notifications?.Notify("API Key 已保存（DPAPI 保护）。");
        });
    }

    [RelayCommand]
    private async Task DeleteApiKeyAsync()
    {
        var confirmed = await Dialogs!.ConfirmAsync(new ConfirmDialogOptions
        {
            Title = "删除 API Key",
            Message = "将删除本机保存的默认 API Key。使用该密钥的局域网实例将无法再启动，除非重新配置。",
            PrimaryButtonText = "删除",
            SecondaryButtonText = "取消",
            IsDestructive = true,
        });
        if (!confirmed)
        {
            return;
        }

        await _secrets.DeleteAsync(SecretNames.DefaultApiKey);
        await RefreshApiKeyStatusAsync();
        Notifications?.Notify("默认 API Key 已删除。");
    }

    /// <summary>可选：配置 GitHub 令牌，提高更新检查的 API 频率上限（未登录 60 次/小时 → 登录 5000 次/小时）。</summary>
    [RelayCommand]
    private async Task SetGitHubTokenAsync()
    {
        var value = await Dialogs!.PromptAsync(
            "配置 GitHub 令牌（可选）",
            "令牌仅用于只读查询 llama.cpp 官方 Releases（api.github.com），用于提高更新检查的频率上限：\n" +
            "未登录 60 次/小时 → 登录 5000 次/小时，且数据未变化（304）时不计入配额。\n" +
            "可在 GitHub → Settings → Developer settings → Personal access tokens 创建；\n" +
            "公共仓库只读查询不需要勾选任何权限（classic 令牌不选 scope、或 fine-grained 令牌的 Public Repositories 只读）。\n" +
            "令牌将使用 Windows DPAPI 加密存储，仅本机当前用户可解密，不写入日志、导出与诊断包。");
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        await RunBusyAsync("正在保存 GitHub 令牌…", async () =>
        {
            await _secrets.SetAsync(SecretNames.GitHubToken, value.Trim());
            await RefreshGitHubTokenStatusAsync();
            Notifications?.Notify("GitHub 令牌已保存（DPAPI 保护）。");
        });
    }

    [RelayCommand]
    private async Task DeleteGitHubTokenAsync()
    {
        var confirmed = await Dialogs!.ConfirmAsync(new ConfirmDialogOptions
        {
            Title = "删除 GitHub 令牌",
            Message = "将删除本机保存的 GitHub 令牌，更新检查恢复为未登录请求（每小时 60 次上限）。",
            PrimaryButtonText = "删除",
            SecondaryButtonText = "取消",
            IsDestructive = true,
        });
        if (!confirmed)
        {
            return;
        }

        await _secrets.DeleteAsync(SecretNames.GitHubToken);
        await RefreshGitHubTokenStatusAsync();
        Notifications?.Notify("GitHub 令牌已删除。");
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenInExplorer(_paths.DataRoot);

    [RelayCommand]
    private void OpenLogsFolder() => OpenInExplorer(_paths.LogsDirectory);

    [RelayCommand]
    private void OpenRuntimesFolder() => OpenInExplorer(_paths.RuntimesDirectory);

    [RelayCommand]
    private void OpenDownloadsFolder() => OpenInExplorer(_paths.DownloadsDirectory);

    [RelayCommand]
    private void OpenModelsFolder()
    {
        if (string.IsNullOrWhiteSpace(_modelsDirectorySetting))
        {
            Notifications?.Notify("尚未设置模型目录，请先点击“更改…”选择目录。");
            return;
        }

        OpenInExplorer(_modelsDirectorySetting);
    }

    [RelayCommand]
    private async Task PickRuntimesDirectoryAsync()
    {
        var folder = await PickDirectoryAsync("选择 llama.cpp 版本目录", RuntimesDirectoryText);
        if (folder is null)
        {
            return;
        }

        _runtimesDirectorySetting = folder;
        RuntimesDirectoryText = folder;
        RefreshDirectoryHints();
        StatusText = "版本目录已更改，点击“保存设置”生效（已安装版本不受影响）。";
    }

    [RelayCommand]
    private void ResetRuntimesDirectory()
    {
        _runtimesDirectorySetting = null;
        RuntimesDirectoryText = _paths.DefaultRuntimesDirectory;
        RefreshDirectoryHints();
        StatusText = "版本目录已恢复默认，点击“保存设置”生效。";
    }

    [RelayCommand]
    private async Task PickDownloadsDirectoryAsync()
    {
        var folder = await PickDirectoryAsync("选择更新下载目录", DownloadsDirectoryText);
        if (folder is null)
        {
            return;
        }

        _downloadsDirectorySetting = folder;
        DownloadsDirectoryText = folder;
        RefreshDirectoryHints();
        StatusText = "下载目录已更改，点击“保存设置”生效。";
    }

    [RelayCommand]
    private void ResetDownloadsDirectory()
    {
        _downloadsDirectorySetting = null;
        DownloadsDirectoryText = _paths.DefaultDownloadsDirectory;
        RefreshDirectoryHints();
        StatusText = "下载目录已恢复默认，点击“保存设置”生效。";
    }

    [RelayCommand]
    private async Task PickModelsDirectoryAsync()
    {
        var folder = await PickDirectoryAsync(
            "选择模型目录",
            string.IsNullOrWhiteSpace(_modelsDirectorySetting) ? _paths.DataRoot : _modelsDirectorySetting);
        if (folder is null)
        {
            return;
        }

        _modelsDirectorySetting = folder;
        ModelsDirectoryText = folder;
        RefreshDirectoryHints();
        StatusText = "模型目录已更改，点击“保存设置”生效。";
    }

    [RelayCommand]
    private void ResetModelsDirectory()
    {
        _modelsDirectorySetting = null;
        ModelsDirectoryText = "（未设置：默认使用上次扫描/添加的目录）";
        RefreshDirectoryHints();
        StatusText = "模型目录已清除，点击“保存设置”生效。";
    }

    private async Task<string?> PickDirectoryAsync(string title, string initialDirectory)
    {
        var folder = Dialogs!.PickFolder(title, initialDirectory);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (Path.GetDirectoryName(fullPath) is null)
        {
            await Dialogs.ShowWarningAsync("目录无效", "请选择具体目录，而不是盘符根目录。");
            return null;
        }

        try
        {
            Directory.CreateDirectory(fullPath);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowErrorAsync("目录不可用", $"无法创建或访问目录：\n{fullPath}\n\n{ex.Message}");
            return null;
        }

        return fullPath;
    }

    private void RefreshDirectoryHints()
    {
        RuntimesDirectoryHint = _runtimesDirectorySetting is null
            ? "默认目录（数据目录下）：新安装与本地发现的版本会放入此目录。"
            : PathsEqual(RuntimesDirectoryText, _runtimesDirectorySetting)
                ? $"自定义目录（默认：{_paths.DefaultRuntimesDirectory}）：新安装与本地发现的版本会放入此目录，已安装版本不受影响。"
                : $"配置的自定义目录当前不可用，本次已回退默认：{_paths.DefaultRuntimesDirectory}。";

        DownloadsDirectoryHint = _downloadsDirectorySetting is null
            ? "默认目录（数据目录下）：更新包在此临时下载。"
            : $"自定义目录（默认：{_paths.DefaultDownloadsDirectory}）：更新包在此临时下载。";

        ModelsDirectoryHint = "模型库“添加文件/扫描目录”的默认起始目录；未设置时沿用上次使用的目录。";
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void OpenBackupsFolder() => OpenInExplorer(_paths.BackupsDirectory);

    [RelayCommand]
    private async Task ChangeDataRootAsync()
    {
        var folder = Dialogs!.PickFolder("选择新的数据目录（重启后生效）", _paths.DataRoot);
        if (folder is null)
        {
            return;
        }

        var confirmed = await Dialogs.ConfirmAsync(new ConfirmDialogOptions
        {
            Title = "更改数据目录",
            Message = $"新的数据目录：\n{folder}\n\n" +
                      "现有数据不会自动迁移；请确认是否需要手动复制 data/logs 等目录。\n" +
                      "更改将在重启应用后生效。",
            PrimaryButtonText = "写入引导配置",
            SecondaryButtonText = "取消",
        });
        if (!confirmed)
        {
            return;
        }

        try
        {
            Infrastructure.AppPaths.WriteBootstrap(AppContext.BaseDirectory, folder);
            DataRootText = folder;
            Notifications?.Notify("数据目录已更新，请重启应用生效。");
        }
        catch (Exception ex)
        {
            await Dialogs.ShowErrorAsync("写入引导配置失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task TogglePortableModeAsync()
    {
        // 便携（默认）：所有文件保存在程序目录；“关闭”则改用 %AppData%\LlamaLauncher。
        var enable = !IsPortableMode;
        var appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LlamaLauncher");
        var confirmed = await Dialogs!.ConfirmAsync(new ConfirmDialogOptions
        {
            Title = enable ? "数据保存到程序目录（默认）" : "数据改用 AppData 目录",
            Message = enable
                ? $"将删除自定义数据目录引导配置，恢复为默认：所有文件保存到程序目录。\n数据目录：{AppContext.BaseDirectory}\n\n重启后生效。"
                : $"将在程序目录写入 LlamaLauncher.bootstrap.json，数据改存：\n{appDataRoot}\n\n重启后生效。",
            PrimaryButtonText = enable ? "使用程序目录" : "改用 AppData",
            SecondaryButtonText = "取消",
        });
        if (!confirmed)
        {
            return;
        }

        try
        {
            Infrastructure.AppPaths.WriteBootstrap(AppContext.BaseDirectory, enable ? null : appDataRoot);
            IsPortableMode = enable;
            Notifications?.Notify(enable
                ? "已恢复默认：数据保存在程序目录，重启后生效。"
                : "已切换为 AppData 数据目录，重启后生效。");
        }
        catch (Exception ex)
        {
            await Dialogs.ShowErrorAsync("切换数据目录失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task CreateBackupAsync()
    {
        await RunBusyAsync("正在创建数据库备份…", async () =>
        {
            var path = await _backups.CreateBackupAsync();
            RefreshBackups();
            await Dialogs!.ShowInfoAsync("备份完成", "备份已创建：\n" + path);
        });
    }

    [RelayCommand]
    private async Task RestoreBackupAsync()
    {
        if (SelectedBackup is not { } row)
        {
            await Dialogs!.ShowWarningAsync("请选择备份", "请先在列表中选择要恢复的备份文件。");
            return;
        }

        var confirmed = await Dialogs!.ConfirmAsync(new ConfirmDialogOptions
        {
            Title = "恢复数据库备份",
            Message = $"将使用以下备份覆盖当前数据库：\n{row.PathText}\n（创建于 {row.CreatedText}）\n\n" +
                      "恢复前会自动备份当前数据库；恢复完成后需要重启应用。",
            PrimaryButtonText = "确认恢复",
            SecondaryButtonText = "取消",
            IsDestructive = true,
        });
        if (!confirmed)
        {
            return;
        }

        await RunBusyAsync("正在恢复数据库…", async () =>
        {
            var result = await _backups.RestoreAsync(row.Info.FilePath);
            await Dialogs!.ShowInfoAsync(result.Success ? "恢复完成" : "恢复失败", result.Message);
            RefreshBackups();
        });
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        var path = Dialogs!.PickSaveFile(
            "导出诊断包",
            "Zip 压缩包|*.zip",
            $"llamalauncher-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (path is null)
        {
            return;
        }

        await RunBusyAsync("正在导出诊断包…", async () =>
        {
            var result = await _diagnostics.ExportBundleAsync(path, includeUserPaths: false);
            await Dialogs!.ShowInfoAsync(
                result.Success ? "导出完成" : "导出失败",
                result.Success ? "诊断包已保存到：\n" + result.FilePath : result.Message);
        });
    }

    [RelayCommand]
    private void OpenExportsFolder()
    {
        try
        {
            Directory.CreateDirectory(_paths.ExportsDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "创建导出目录失败");
        }

        OpenInExplorer(_paths.ExportsDirectory);
    }

    private async Task RefreshApiKeyStatusAsync()
    {
        try
        {
            var exists = await _secrets.ExistsAsync(SecretNames.DefaultApiKey);
            ApiKeyStatus = exists ? "已配置（DPAPI 加密存储，值为 ******）" : "（未配置）";
        }
        catch (Exception ex)
        {
            ApiKeyStatus = "无法读取密钥状态：" + ex.Message;
        }
    }

    private async Task RefreshGitHubTokenStatusAsync()
    {
        try
        {
            var exists = await _secrets.ExistsAsync(SecretNames.GitHubToken);
            GitHubTokenStatus = exists
                ? "已配置（DPAPI 加密存储，值为 ******）"
                : "（未配置，使用未登录配额：每小时 60 次）";
        }
        catch (Exception ex)
        {
            GitHubTokenStatus = "无法读取令牌状态：" + ex.Message;
        }
    }

    private void RefreshBackups()
    {
        Backups.Clear();
        foreach (var backup in _backups.ListBackups())
        {
            Backups.Add(new BackupRow(backup));
        }

        if (Backups.Count > 0)
        {
            SelectedBackup = Backups[0];
        }
    }

    private static bool IsStartWithWindowsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
            return key?.GetValue("LlamaLauncher") is string;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void SetStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue("LlamaLauncher", $"\"{exePath}\"");
                }
            }
            else
            {
                key.DeleteValue("LlamaLauncher", throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "设置开机自启动失败");
            Notifications?.Notify("设置开机自启动失败：" + ex.Message);
        }
    }

    private void OpenInExplorer(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notifications?.Notify("打开目录失败：" + ex.Message);
        }
    }

}
