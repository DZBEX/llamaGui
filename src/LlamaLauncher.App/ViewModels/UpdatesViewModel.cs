using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Models;
using LlamaLauncher.Application.Services;
using LlamaLauncher.Application.Updates;
using LlamaLauncher.App.Services;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.App.ViewModels;

/// <summary>候选资产行。</summary>
public sealed record AssetRow(AssetCandidate Candidate)
{
    public string Name => Candidate.Asset.Name;

    public string BackendText => Candidate.IsSupportFile
        ? Formatting.BackendText(Candidate.Backend) + "（运行时辅助包）"
        : Formatting.BackendText(Candidate.Backend);

    public string CudaText => Candidate.CudaVersion ?? "—";

    public string SizeText => Formatting.Bytes(Candidate.Asset.SizeBytes);

    public bool Recommended => Candidate.Recommended;

    public string Note => Candidate.Note;
}

/// <summary>已安装版本行。</summary>
public sealed record InstallationRow(LlamaCppInstallation Installation)
{
    public string Version => Installation.VersionLabel;

    public string Directory => Installation.Directory;

    public string BackendText => Installation.BackendTags.Count == 0
        ? "未探测"
        : string.Join(" / ", Installation.BackendTags.Select(Formatting.BackendText));

    public string StatusText => Installation.Status switch
    {
        InstallationStatus.Available => "可用",
        InstallationStatus.Unhealthy => "不健康",
        InstallationStatus.Missing => "文件缺失",
        _ => "未知",
    };

    public string HashText => Installation.Sha256 is null
        ? "无记录"
        : Installation.Sha256FromOfficialDigest
            ? "官方 digest 已校验"
            : "本地指纹（非来源校验）";

    public bool IsDefault => Installation.IsDefault;
}

/// <summary>版本管理页（llama.cpp 程序更新）。</summary>
public sealed partial class UpdatesViewModel : ViewModelBase, IPageViewModel
{
    private readonly ILlamaUpdateService _updates;
    private readonly IReleaseFeedService _feed;
    private readonly ILlamaInstallationService _installations;
    private readonly ISettingsService _settings;
    private readonly ILogger<UpdatesViewModel> _logger;
    private CancellationTokenSource? _downloadCts;

    [ObservableProperty]
    private UpdateChannel _channel = UpdateChannel.Stable;

    [ObservableProperty]
    private string _currentVersionText = "未安装";

    [ObservableProperty]
    private string _checkStatusText = "尚未检查更新";

    [ObservableProperty]
    private string _lastCheckText = string.Empty;

    [ObservableProperty]
    private ReleaseInfo? _selectedRelease;

    [ObservableProperty]
    private AssetRow? _selectedAsset;

    [ObservableProperty]
    private InstallationRow? _selectedInstallation;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double? _downloadFraction;

    [ObservableProperty]
    private string _downloadStatusText = string.Empty;

    [ObservableProperty]
    private string? _releaseNotesPreview;

    public UpdatesViewModel(
        ILlamaUpdateService updates,
        IReleaseFeedService feed,
        ILlamaInstallationService installations,
        ISettingsService settings,
        IDialogService dialogs,
        INotificationService notifications,
        ILogger<UpdatesViewModel> logger)
    {
        _updates = updates;
        _feed = feed;
        _installations = installations;
        _settings = settings;
        _logger = logger;
        Dialogs = dialogs;
        Notifications = notifications;
    }

    public ObservableCollection<ReleaseInfo> Releases { get; } = new();

    public ObservableCollection<AssetRow> Assets { get; } = new();

    public ObservableCollection<InstallationRow> InstalledVersions { get; } = new();

    public ObservableCollection<UpdateOperation> History { get; } = new();

    public string OfficialPageUrl => _updates.OfficialReleasesPageUrl;

    /// <summary>更新通道选项（本地化显示）。</summary>
    public ChoiceOption<UpdateChannel>[] ChannelChoices { get; } =
    {
        new(UpdateChannel.Stable, "稳定版"),
        new(UpdateChannel.Prerelease, "预发布版"),
    };

    public async Task OnActivatedAsync()
    {
        var settings = await _settings.LoadAsync();
        Channel = settings.UpdateChannel;
        await RefreshInstalledAsync();
        await RefreshHistoryAsync();
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        await RunBusyAsync("正在检查更新（GitHub 官方 Releases）…", async () =>
        {
            var result = await _updates.CheckForUpdatesAsync(Channel);
            LastCheckText = "最近检查：" + Formatting.LocalTime(result.CheckedUtc);

            if (!result.Success)
            {
                CheckStatusText = result.Error ?? "检查失败。";
                return;
            }

            var settings = await _settings.LoadAsync();
            settings.UpdateChannel = Channel;
            settings.LastUpdateCheckUtc = result.CheckedUtc.ToString("O");
            await _settings.SaveAsync(settings);

            if (result.LatestRelease is { } latest)
            {
                await LoadReleaseListAsync(latest.TagName);
                CheckStatusText = result.UpdateAvailable
                    ? $"发现新版本：{latest.TagName}（当前：{result.CurrentVersionLabel ?? "未安装"}）"
                    : $"已是最新版本：{result.CurrentVersionLabel}";
            }
            else
            {
                CheckStatusText = result.Error ?? "没有可用的 Windows x64 资产。";
            }

            // 频率上限/离线等场景下回退缓存数据时，明确告知用户（避免把缓存误认为实时结果）
            if (result.Warning is { Length: > 0 } warning)
            {
                CheckStatusText += " " + warning;
            }

            await RefreshHistoryAsync();
        });
    }

    private async Task LoadReleaseListAsync(string? preselectTag)
    {
        var feed = await _feed.GetReleasesAsync(Channel, 30);
        Releases.Clear();
        if (!feed.Success)
        {
            CheckStatusText = feed.Error ?? "无法获取发行列表。";
            return;
        }

        foreach (var release in feed.Releases.OrderByDescending(r => r.PublishedAtUtc))
        {
            if (AssetSelector.SelectCandidates(release).Count > 0)
            {
                Releases.Add(release);
            }
        }

        SelectedRelease = preselectTag is null
            ? Releases.FirstOrDefault()
            : Releases.FirstOrDefault(r => r.TagName == preselectTag) ?? Releases.FirstOrDefault();
    }

    partial void OnSelectedReleaseChanged(ReleaseInfo? value)
    {
        Assets.Clear();
        if (value is null)
        {
            ReleaseNotesPreview = null;
            return;
        }

        var candidates = AssetSelector.SelectCandidates(value);
        foreach (var candidate in candidates)
        {
            Assets.Add(new AssetRow(candidate));
        }

        SelectedAsset = Assets.FirstOrDefault(a => a.Candidate.Recommended) ?? Assets.FirstOrDefault();
        ReleaseNotesPreview = value.BodyMarkdown is { Length: > 0 } body
            ? (body.Length > 4000 ? body[..4000] + "…" : body)
            : "（该发行没有提供说明文本）";
    }

    [RelayCommand]
    private async Task DownloadAndInstallAsync()
    {
        if (SelectedRelease is not { } release || SelectedAsset is not { } asset)
        {
            await Dialogs!.ShowWarningAsync("请选择资产", "请先选择要安装的发行版本与资产。");
            return;
        }

        var confirmed = await Dialogs!.ConfirmAsync(new ConfirmDialogOptions
        {
            Title = "下载并安装 llama.cpp",
            Message = $"将下载并安装：\n{asset.Name}\n（版本 {release.TagName}，约 {asset.SizeText}）\n\n" +
                      "安装流程：下载 → 校验 → 安全解压 → 能力验证 → 注册为独立版本目录。\n" +
                      "不会覆盖当前默认版本；安装后可手动切换。",
            PrimaryButtonText = "开始下载",
            SecondaryButtonText = "取消",
        });
        if (!confirmed)
        {
            return;
        }

        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        DownloadFraction = null;
        DownloadStatusText = "正在连接…";
        var progress = CreateDownloadProgress();

        try
        {
            var installation = await _updates.DownloadAndInstallAsync(
                release,
                asset.Candidate.Asset,
                progress,
                _downloadCts.Token);
            Notifications?.Notify($"llama.cpp {installation.VersionLabel} 安装完成。");
            await Dialogs.ShowInfoAsync(
                "安装完成",
                $"版本 {installation.VersionLabel} 已安装到独立目录：\n{installation.Directory}\n\n" +
                "能力验证已通过。可在下方列表中将其设为默认版本。");
            await RefreshInstalledAsync();
            await RefreshHistoryAsync();

            // CUDA 主程序包安装成功后：自动匹配同发行版的 cudart 辅助包，并询问是否一并安装。
            await OfferMatchingRuntimePackAsync(
                release,
                asset.Candidate.Asset,
                installation,
                progress,
                notifyWhenNoMatch: false);
        }
        catch (OperationCanceledException)
        {
            Notifications?.Notify("下载已取消（临时文件已清理）。");
        }
        catch (UpdateException ex)
        {
            await Dialogs!.ShowErrorAsync("安装失败", ex.Message);
            await RefreshHistoryAsync();
        }
        finally
        {
            IsDownloading = false;
            DownloadStatusText = string.Empty;
            DownloadFraction = null;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    private Progress<DownloadProgressInfo> CreateDownloadProgress()
        => new(p =>
        {
            DownloadFraction = p.Fraction;
            DownloadStatusText = p.TotalBytes is { } total
                ? $"已下载 {Formatting.Bytes(p.BytesReceived)} / {Formatting.Bytes(total)}（{p.BytesPerSecond / 1024 / 1024:0.0} MB/s）"
                : $"已下载 {Formatting.Bytes(p.BytesReceived)}（总大小未知）";
        });

    /// <summary>匹配同发行版的 cudart 辅助包并询问是否下载安装；只做可靠匹配，不猜测。</summary>
    private async Task OfferMatchingRuntimePackAsync(
        ReleaseInfo release,
        ReleaseAsset mainAsset,
        LlamaCppInstallation installation,
        IProgress<DownloadProgressInfo> progress,
        bool notifyWhenNoMatch)
    {
        var pack = AssetSelector.FindCudartForMainAsset(release, mainAsset);
        if (pack is null)
        {
            if (notifyWhenNoMatch)
            {
                Notifications?.Notify("该发行中没有可可靠匹配的 cudart 运行时辅助包（或存在多个候选），请手动处理。");
            }

            return;
        }

        var confirmed = await Dialogs!.ConfirmAsync(new ConfirmDialogOptions
        {
            Title = "安装 CUDA 运行时辅助包",
            Message = $"检测到该 CUDA 构建的配套运行时包：\n{pack.Asset.Name}\n" +
                      $"（CUDA {pack.CudaVersion ?? "未标注"}，约 {Formatting.Bytes(pack.Asset.SizeBytes)}）\n\n" +
                      "将下载并执行同一套安全检查（路径穿越/体积限制），把其中的 DLL 放入该版本目录：\n" +
                      $"{installation.Directory}\n\n来源：官方 {release.TagName} 发行资产。",
            PrimaryButtonText = "下载并安装",
            SecondaryButtonText = "跳过",
        });
        if (!confirmed)
        {
            return;
        }

        DownloadFraction = null;
        DownloadStatusText = "正在下载 CUDA 运行时辅助包…";
        try
        {
            var result = await _updates.InstallRuntimePackAsync(
                release,
                pack.Asset,
                installation,
                progress,
                _downloadCts?.Token ?? CancellationToken.None);
            Notifications?.Notify($"CUDA 运行时辅助包安装完成（{result.DllCount} 个 DLL 已放入版本目录）。");

            // 主包在缺少 cudart 时探测不到 GPU 设备；装完辅助包后重新探测，刷新后端标签。
            try
            {
                await _installations.RefreshProbeAsync(installation.Id);
                await RefreshInstalledAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "运行时辅助包安装后重新探测失败");
            }
        }
        catch (OperationCanceledException)
        {
            Notifications?.Notify("CUDA 运行时辅助包下载已取消（未对版本目录做更改）。");
        }
        catch (UpdateException ex)
        {
            await Dialogs!.ShowErrorAsync("CUDA 运行时辅助包安装失败", ex.Message);
        }

        await RefreshHistoryAsync();
    }

    /// <summary>为“已安装版本”中选中的版本补装 cudart 辅助包（在安装提示中“跳过”后的补救入口）。</summary>
    [RelayCommand]
    private async Task InstallRuntimePackAsync()
    {
        if (SelectedInstallation is not { } row)
        {
            await Dialogs!.ShowWarningAsync(
                "请选择版本",
                "请先在“已安装版本”列表中选择要补装 CUDA 运行时辅助包的版本。");
            return;
        }

        var installation = row.Installation;
        var release = Releases.FirstOrDefault(r =>
            string.Equals(r.TagName, installation.VersionLabel, StringComparison.OrdinalIgnoreCase));
        if (release is null)
        {
            await Dialogs!.ShowWarningAsync(
                "需要发行列表",
                $"请先点击“检查更新”加载发行列表（列表中未找到版本 {installation.VersionLabel}）。");
            return;
        }

        var mainAsset = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, installation.AssetName, StringComparison.OrdinalIgnoreCase));
        if (mainAsset is null)
        {
            await Dialogs!.ShowWarningAsync(
                "无法自动匹配",
                "该版本不是通过自动下载安装的（可能是手动导入），请从官方发布页手动下载对应的 cudart 包，" +
                "解压后把 DLL 放到该版本目录。");
            return;
        }

        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        DownloadFraction = null;
        DownloadStatusText = "正在准备…";
        try
        {
            await OfferMatchingRuntimePackAsync(
                release,
                mainAsset,
                installation,
                CreateDownloadProgress(),
                notifyWhenNoMatch: true);
        }
        finally
        {
            IsDownloading = false;
            DownloadStatusText = string.Empty;
            DownloadFraction = null;
            _downloadCts?.Dispose();
            _downloadCts = null;
            await RefreshHistoryAsync();
        }
    }

    [RelayCommand]
    private async Task ImportLocalAsync()
    {
        var path = Dialogs!.PickOpenFile("选择 llama.cpp 发行压缩包（.zip）", "Zip 压缩包|*.zip");
        if (path is null)
        {
            return;
        }

        var (confirmed, isOfficial) = await Dialogs.ConfirmWithCheckboxAsync(
            new ConfirmDialogOptions
            {
                Title = "导入本地压缩包",
                Message = "将对压缩包执行与自动下载相同的安全检查（路径穿越/符号链接/体积限制）、" +
                          "能力探测与独立目录安装。\n\n" +
                          "请在下方勾选来源：默认按“非官方来源”处理并在安装记录中标注。",
                PrimaryButtonText = "导入",
                SecondaryButtonText = "取消",
            },
            "该压缩包来自官方 GitHub Releases 页面",
            defaultChecked: false);
        if (!confirmed)
        {
            return;
        }

        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        DownloadStatusText = "正在解压并验证…";
        try
        {
            var installation = await _updates.ImportLocalAssetAsync(
                path,
                isOfficial,
                isOfficial ? null : "用户通过“导入本地压缩包”提供的第三方来源",
                null,
                _downloadCts.Token);
            Notifications?.Notify($"已导入 llama.cpp {installation.VersionLabel}。");
            await RefreshInstalledAsync();
            await RefreshHistoryAsync();
        }
        catch (OperationCanceledException)
        {
            Notifications?.Notify("导入已取消。");
        }
        catch (UpdateException ex)
        {
            await Dialogs!.ShowErrorAsync("导入失败", ex.Message);
        }
        finally
        {
            IsDownloading = false;
            DownloadStatusText = string.Empty;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    /// <summary>扫描版本目录，把手动拷入的已解压版本登记到列表。</summary>
    [RelayCommand]
    private async Task ScanLocalVersionsAsync()
    {
        await RunBusyAsync("正在扫描版本目录（runtimes\\llama.cpp）…", async () =>
        {
            var discovered = await _updates.DiscoverLocalVersionsAsync();
            await RefreshInstalledAsync();
            Notifications?.Notify(discovered.Count == 0
                ? "扫描完成：未发现新的本地版本。"
                : $"已发现并登记 {discovered.Count} 个本地版本：" +
                  string.Join("、", discovered.Select(i => i.VersionLabel)) + "。");
        });
    }

    [RelayCommand]
    private async Task SetDefaultAsync()
    {
        if (SelectedInstallation is not { } row)
        {
            return;
        }

        try
        {
            await _updates.SetDefaultAsync(row.Installation.Id);
            await RefreshInstalledAsync();
            await RefreshHistoryAsync();
            Notifications?.Notify($"默认版本已切换为 {row.Version}（可随时回滚）。");
        }
        catch (UpdateException ex)
        {
            await Dialogs!.ShowErrorAsync("切换失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task RollbackAsync()
    {
        var result = await _updates.RollbackAsync();
        await RefreshInstalledAsync();
        await RefreshHistoryAsync();
        await Dialogs!.ShowInfoAsync(result.Success ? "回滚完成" : "无法回滚", result.Message);
    }

    [RelayCommand]
    private async Task DeleteInstallationAsync()
    {
        if (SelectedInstallation is not { } row)
        {
            return;
        }

        var (confirmed, deleteFiles) = await Dialogs!.ConfirmWithCheckboxAsync(
            new ConfirmDialogOptions
            {
                Title = "删除版本",
                Message = $"将移除版本 {row.Version} 的安装记录。\n\n目录：{row.Directory}\n" +
                          "不会影响当前默认版本与运行中的实例（若被引用会阻止删除）。",
                PrimaryButtonText = "删除",
                SecondaryButtonText = "取消",
                IsDestructive = true,
            },
            "同时删除该版本的磁盘文件（不可恢复）",
            defaultChecked: false);
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _updates.DeleteInstallationAsync(row.Installation.Id, deleteFiles);
            await RefreshInstalledAsync();
            await RefreshHistoryAsync();
            Notifications?.Notify("版本已移除。");
        }
        catch (UpdateException ex)
        {
            await Dialogs.ShowErrorAsync("无法删除", ex.Message);
        }
    }

    [RelayCommand]
    private async Task RefreshProbeAsync()
    {
        if (SelectedInstallation is not { } row)
        {
            return;
        }

        await RunBusyAsync("正在重新探测版本能力…", async () =>
        {
            var refreshed = await _installations.RefreshProbeAsync(row.Installation.Id);
            await RefreshInstalledAsync();
            Notifications?.Notify($"版本 {refreshed.VersionLabel} 探测完成（状态：{refreshed.Status}）。");
        });
    }

    [RelayCommand]
    private void OpenOfficialPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_updates.OfficialReleasesPageUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notifications?.Notify("打开浏览器失败：" + ex.Message);
        }
    }

    private async Task RefreshInstalledAsync()
    {
        var installations = await _installations.GetAllAsync();
        InstalledVersions.Clear();
        foreach (var installation in installations)
        {
            InstalledVersions.Add(new InstallationRow(installation));
        }

        var current = installations.FirstOrDefault(i => i.IsDefault);
        CurrentVersionText = current is null
            ? "未安装（默认版本）"
            : $"{current.VersionLabel} · {current.ExecutablePath}";
        SelectedInstallation = current is null
            ? InstalledVersions.FirstOrDefault()
            : InstalledVersions.FirstOrDefault(r => r.Installation.Id == current.Id) ?? InstalledVersions.FirstOrDefault();
    }

    private async Task RefreshHistoryAsync()
    {
        var history = await _updates.GetHistoryAsync(50);
        History.Clear();
        foreach (var operation in history)
        {
            History.Add(operation);
        }
    }

    partial void OnChannelChanged(UpdateChannel value)
    {
        _ = PersistChannelAsync(value);
    }

    private async Task PersistChannelAsync(UpdateChannel channel)
    {
        try
        {
            var settings = await _settings.LoadAsync();
            if (settings.UpdateChannel != channel)
            {
                settings.UpdateChannel = channel;
                await _settings.SaveAsync(settings);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "保存更新通道失败");
        }
    }
}
