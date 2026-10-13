using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Chat;
using LlamaLauncher.Application.Instances;
using LlamaLauncher.Application.Services;
using LlamaLauncher.App.Services;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.App.ViewModels;

/// <summary>服务实例页：多实例生命周期管理。</summary>
public sealed partial class InstancesViewModel : ViewModelBase, IPageViewModel, IDisposable
{
    private readonly IInstanceManager _manager;
    private readonly ILaunchProfileService _profiles;
    private readonly IResourceMonitorService _resources;
    private readonly IClipboardService _clipboard;
    private readonly IChatSessionService _chat;
    private readonly IDiagnosticsService _diagnostics;
    private readonly ISettingsService _settings;
    private readonly IUiActions _navigation;
    private readonly ILogger<InstancesViewModel> _logger;
    private readonly DispatcherTimer _timer;

    [ObservableProperty]
    private InstanceRowViewModel? _selectedInstance;

    [ObservableProperty]
    private string? _detailCommandLine;

    public InstancesViewModel(
        IInstanceManager manager,
        ILaunchProfileService profiles,
        IResourceMonitorService resources,
        IClipboardService clipboard,
        IChatSessionService chat,
        IDiagnosticsService diagnostics,
        ISettingsService settings,
        IUiActions navigation,
        IDialogService dialogs,
        INotificationService notifications,
        ILogger<InstancesViewModel> logger)
    {
        _manager = manager;
        _profiles = profiles;
        _resources = resources;
        _clipboard = clipboard;
        _chat = chat;
        _diagnostics = diagnostics;
        _settings = settings;
        _navigation = navigation;
        _logger = logger;
        Dialogs = dialogs;
        Notifications = notifications;

        _manager.InstanceChanged += OnInstanceChanged;
        _resources.SnapshotAvailable += OnSnapshot;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshRows();
        _timer.Start();
        RefreshRows();
    }

    public ObservableCollection<InstanceRowViewModel> Instances { get; } = new();

    public Task OnActivatedAsync()
    {
        RefreshRows();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CreateInstanceAsync()
    {
        var profiles = await _profiles.GetAllAsync();
        if (profiles.Count == 0)
        {
            await Dialogs!.ShowWarningAsync(
                "无法创建实例",
                "请先在“启动配置”页面创建至少一个配置档案。");
            return;
        }

        var picker = new Dialogs.InstanceCreateDialog(profiles)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        if (picker.ShowDialog() != true || picker.SelectedProfile is null)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(picker.InstanceName)
            ? picker.SelectedProfile.Name + " 实例"
            : picker.InstanceName.Trim();

        await RunBusyAsync("正在创建实例…", async () =>
        {
            var instance = await _manager.CreateAsync(name, picker.SelectedProfile.Id);
            RefreshRows();
            SelectedInstance = Instances.FirstOrDefault(r => r.Id == instance.Id);
            Notifications?.Notify($"已创建实例“{name}”，点击“启动”开始加载模型。");
        });
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (SelectedInstance is not { } row)
        {
            return;
        }

        var instance = _manager.Get(row.Id);
        if (instance is null)
        {
            return;
        }

        var lanRequired = HostAddress.IsNonLocal(HostAddress.Classify(instance.Record.Host));
        var lanConfirmed = false;
        if (lanRequired)
        {
            lanConfirmed = await ConfirmLanAccessAsync(instance);
            if (!lanConfirmed)
            {
                return;
            }
        }

        await RunBusyAsync($"正在启动“{row.Name}”…", async () =>
        {
            var result = await _manager.StartAsync(row.Id, waitForReady: false, lanAccessConfirmed: lanConfirmed);
            if (result.Success)
            {
                Notifications?.Notify($"实例“{row.Name}”正在加载模型，就绪后可开始聊天。");
            }
            else
            {
                await Dialogs!.ShowErrorAsync("启动失败", result.Message ?? "未知错误。");
            }
        });
        RefreshRows();
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (SelectedInstance is not { } row)
        {
            return;
        }

        await RunBusyAsync($"正在停止“{row.Name}”…", async () =>
        {
            await _manager.StopAsync(row.Id);
            Notifications?.Notify($"实例“{row.Name}”已停止。");
        });
        RefreshRows();
    }

    [RelayCommand]
    private async Task ForceKillAsync()
    {
        if (SelectedInstance is not { } row)
        {
            return;
        }

        var confirmed = await Dialogs!.ConfirmAsync(new ConfirmDialogOptions
        {
            Title = "强制结束进程",
            Message = $"将立即强制结束实例“{row.Name}”的进程树（PID {row.ProcessIdText}）。\n" +
                      "未保存的推理结果会丢失，可能影响正在进行的请求。",
            PrimaryButtonText = "强制结束",
            SecondaryButtonText = "取消",
            IsDestructive = true,
        });
        if (!confirmed)
        {
            return;
        }

        await RunBusyAsync("正在强制结束…", async () =>
        {
            await _manager.StopAsync(row.Id, force: true);
            Notifications?.Notify($"实例“{row.Name}”已强制结束。");
        });
        RefreshRows();
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        if (SelectedInstance is not { } row)
        {
            return;
        }

        var instance = _manager.Get(row.Id);
        var lanRequired = instance is not null
            && HostAddress.IsNonLocal(HostAddress.Classify(instance.Record.Host));
        var lanConfirmed = false;
        if (lanRequired)
        {
            lanConfirmed = await ConfirmLanAccessAsync(instance!);
            if (!lanConfirmed)
            {
                return;
            }
        }

        await RunBusyAsync($"正在重启“{row.Name}”…", async () =>
        {
            await _manager.StopAsync(row.Id);
            var result = await _manager.StartAsync(row.Id, waitForReady: false, lanAccessConfirmed: lanConfirmed);
            Notifications?.Notify(result.Success
                ? $"实例“{row.Name}”正在重新启动。"
                : "重启失败：" + result.Message);
        });
        RefreshRows();
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (SelectedInstance is not { } row)
        {
            return;
        }

        var confirmed = await Dialogs!.ConfirmAsync(new ConfirmDialogOptions
        {
            Title = "移除实例",
            Message = $"将移除实例“{row.Name}”的记录（不影响模型文件与启动历史）。\n实例必须处于已停止/失败状态。",
            PrimaryButtonText = "移除",
            SecondaryButtonText = "取消",
            IsDestructive = true,
        });
        if (!confirmed)
        {
            return;
        }

        await RunBusyAsync("正在移除实例…", async () =>
        {
            await _manager.RemoveAsync(row.Id);
            RefreshRows();
        });
    }

    [RelayCommand]
    private void OpenChat()
    {
        if (SelectedInstance is { } row && row.CanOpenChat)
        {
            _navigation.NavigateToChat(row.Id);
        }
    }

    [RelayCommand]
    private void OpenLogs()
    {
        if (SelectedInstance is { } row)
        {
            _navigation.NavigateToLogs(row.Id);
        }
    }

    [RelayCommand]
    private async Task CopyUrlAsync()
    {
        if (SelectedInstance is not { } row)
        {
            return;
        }

        var (success, error) = await _clipboard.SetTextAsync(row.Url);
        Notifications?.Notify(success ? "API 地址已复制。" : error ?? "复制失败。");
    }

    [RelayCommand]
    private async Task TestApiAsync()
    {
        if (SelectedInstance is not { } row)
        {
            return;
        }

        await RunBusyAsync("正在测试 API 连通性…", async () =>
        {
            var (success, message) = await _chat.TestConnectionAsync(row.Id);
            await Dialogs!.ShowInfoAsync(success ? "API 测试通过" : "API 测试失败", message);
        });
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        if (SelectedInstance is not { } row)
        {
            return;
        }

        var settings = await _settings.LoadAsync();
        var path = Dialogs!.PickSaveFile(
            "导出诊断包",
            "Zip 压缩包|*.zip",
            $"diagnostics-{row.Name}-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            settings is null ? null : null);
        if (path is null)
        {
            return;
        }

        await RunBusyAsync("正在导出诊断包…", async () =>
        {
            var result = await _diagnostics.ExportBundleAsync(path, includeUserPaths: false);
            await Dialogs!.ShowInfoAsync(
                result.Success ? "导出完成" : "导出失败",
                result.Success
                    ? $"诊断包已保存到：\n{result.FilePath}\n\n（API Key 与路径已脱敏）"
                    : result.Message);
        });
    }

    partial void OnSelectedInstanceChanged(InstanceRowViewModel? value)
    {
        DetailCommandLine = _manager.Get(value?.Id ?? Guid.Empty)?.MaskedCommandLine;
    }

    private async Task<bool> ConfirmLanAccessAsync(ManagedInstance instance)
    {
        var profile = instance.Profile;
        var hasApiKey = profile?.Options.ApiKeyEnabled == true;
        var detail = $"监听地址：{instance.Record.Host}\n端口：{instance.Record.Port}\n" +
                     $"API Key：{(hasApiKey ? "已启用（密钥存储中配置）" : "未启用")}\n\n" +
                     "同一局域网内的其他设备可能访问该服务。API Key 不提供传输加密，" +
                     "请勿在不可信网络中使用明文 HTTP。";

        return await Dialogs!.ConfirmAsync(new ConfirmDialogOptions
        {
            Title = "允许局域网访问",
            Message = detail,
            PrimaryButtonText = "我已了解风险，继续启动",
            SecondaryButtonText = "取消",
            RiskConfirmationText = "我确认仅在可信网络中开放此服务",
            IsDestructive = true,
        });
    }

    private void OnInstanceChanged(object? sender, Guid instanceId)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(RefreshRows);
    }

    private void OnSnapshot(ResourceSnapshot snapshot)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(() =>
        {
            foreach (var row in Instances)
            {
                row.ApplySnapshot(snapshot);
            }
        });
    }

    private void RefreshRows()
    {
        var rows = _manager.GetInstances();
        var selectedId = SelectedInstance?.Id;

        // 同步而不是重建：保持选中与滚动位置稳定
        var existing = Instances.ToDictionary(r => r.Id);
        for (var i = 0; i < rows.Count; i++)
        {
            var instance = rows[i];
            if (existing.TryGetValue(instance.Id, out var row))
            {
                row.Refresh();
                var currentIndex = Instances.IndexOf(row);
                if (currentIndex != i)
                {
                    Instances.Move(currentIndex, i);
                }
            }
            else
            {
                Instances.Insert(i, new InstanceRowViewModel(
                    instance, _manager, _clipboard, _navigation, Notifications));
            }
        }

        for (var i = Instances.Count - 1; i >= rows.Count; i--)
        {
            Instances.RemoveAt(i);
        }

        if (selectedId is { } id)
        {
            SelectedInstance = Instances.FirstOrDefault(r => r.Id == id);
        }
        else if (SelectedInstance is null && Instances.Count > 0)
        {
            SelectedInstance = Instances[0];
        }

        DetailCommandLine = SelectedInstance is { } current
            ? _manager.Get(current.Id)?.MaskedCommandLine
            : null;
    }

    public void Dispose()
    {
        _timer.Stop();
        _manager.InstanceChanged -= OnInstanceChanged;
        _resources.SnapshotAvailable -= OnSnapshot;
    }
}
