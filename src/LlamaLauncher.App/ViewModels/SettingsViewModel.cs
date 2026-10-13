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
    private string _dataRootText = string.Empty;

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
        await RefreshApiKeyStatusAsync();
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

            await _settings.SaveAsync(settings);

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

    [RelayCommand]
    private void OpenDataFolder() => OpenInExplorer(_paths.DataRoot);

    [RelayCommand]
    private void OpenLogsFolder() => OpenInExplorer(_paths.LogsDirectory);

    [RelayCommand]
    private void OpenRuntimesFolder() => OpenInExplorer(_paths.RuntimesDirectory);

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
