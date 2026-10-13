using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Instances;
using LlamaLauncher.App.Services;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.App.ViewModels;

/// <summary>概览页：实例统计、默认版本、模型数量与资源摘要。</summary>
public sealed partial class DashboardViewModel : ViewModelBase, IPageViewModel, IDisposable
{
    private readonly IInstanceManager _instances;
    private readonly IModelCatalogService _models;
    private readonly ILlamaInstallationService _installations;
    private readonly IResourceMonitorService _resources;
    private readonly IStartupHistoryRepository _history;
    private readonly IClipboardService _clipboard;
    private readonly IUiActions _navigation;
    private readonly ILogger<DashboardViewModel> _logger;

    [ObservableProperty]
    private int _runningInstances;

    [ObservableProperty]
    private int _stoppedInstances;

    [ObservableProperty]
    private int _failedInstances;

    [ObservableProperty]
    private int _modelCount;

    [ObservableProperty]
    private string _defaultVersionLabel = "未安装";

    [ObservableProperty]
    private string _defaultVersionPath = "尚未选择 llama.cpp 程序";

    [ObservableProperty]
    private string _defaultVersionBackends = "—";

    [ObservableProperty]
    private string _systemCpuText = "—";

    [ObservableProperty]
    private string _systemMemoryText = "—";

    [ObservableProperty]
    private string _gpuText = "不可用（未检测到可用的 GPU 指标来源）";

    [ObservableProperty]
    private string _sampleTimeText = "尚未采样";

    public DashboardViewModel(
        IInstanceManager instances,
        IModelCatalogService models,
        ILlamaInstallationService installations,
        IResourceMonitorService resources,
        IStartupHistoryRepository history,
        IClipboardService clipboard,
        IUiActions navigation,
        INotificationService notifications,
        ILogger<DashboardViewModel> logger)
    {
        _instances = instances;
        _models = models;
        _installations = installations;
        _resources = resources;
        _history = history;
        _clipboard = clipboard;
        _navigation = navigation;
        _logger = logger;
        Notifications = notifications;

        instances.InstanceChanged += (_, _) => OnInstanceChanged();
        resources.SnapshotAvailable += OnSnapshot;
    }

    private void OnInstanceChanged()
    {
        // 事件可能来自后台线程（进程/健康轮询回调），ObservableCollection 只能在 UI 线程更新。
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            _ = RefreshAsync();
        }
        else
        {
            dispatcher.BeginInvoke(() => _ = RefreshAsync());
        }
    }

    public ObservableCollection<InstanceRowViewModel> ActiveInstances { get; } = new();

    public ObservableCollection<ModelRowViewModel> RecentModels { get; } = new();

    public ObservableCollection<string> RecentFailures { get; } = new();

    public Task OnActivatedAsync() => RefreshAsync();

    public async Task RefreshAsync()
    {
        try
        {
            var all = _instances.GetInstances();
            RunningInstances = all.Count(i => i.IsActive);
            FailedInstances = all.Count(i => i.State is InstanceState.Failed or InstanceState.ExitedUnexpectedly);
            StoppedInstances = all.Count(i => i.State == InstanceState.Stopped);

            ActiveInstances.Clear();
            foreach (var instance in all.OrderByDescending(i => i.IsActive).ThenBy(i => i.Record.CreatedUtc))
            {
                ActiveInstances.Add(new InstanceRowViewModel(instance, _instances, _clipboard, _navigation, Notifications));
            }

            var models = await _models.GetAllAsync().ConfigureAwait(true);
            ModelCount = models.Count;
            RecentModels.Clear();
            foreach (var model in models.OrderByDescending(m => m.UpdatedUtc).Take(5))
            {
                RecentModels.Add(new ModelRowViewModel(model));
            }

            var defaultInstallation = await _installations.GetDefaultAsync().ConfigureAwait(true);
            if (defaultInstallation is null)
            {
                DefaultVersionLabel = "未安装";
                DefaultVersionPath = "尚未安装或选择 llama.cpp 程序";
                DefaultVersionBackends = "—";
            }
            else
            {
                DefaultVersionLabel = defaultInstallation.VersionLabel;
                DefaultVersionPath = defaultInstallation.ExecutablePath;
                DefaultVersionBackends = defaultInstallation.BackendTags.Count == 0
                    ? "未探测到设备信息"
                    : string.Join(" / ", defaultInstallation.BackendTags.Select(Formatting.BackendText));
            }

            var history = await _history.QueryAsync(new HistoryQuery { Limit = 30 }).ConfigureAwait(true);
            RecentFailures.Clear();
            foreach (var entry in history
                         .Where(h => h.FinalState is InstanceState.Failed or InstanceState.ExitedUnexpectedly)
                         .Take(5))
            {
                RecentFailures.Add(
                    $"{Formatting.LocalTime(entry.StartedUtc)} · {entry.InstanceNameSnapshot} · " +
                    $"{(entry.ErrorSummary ?? "启动失败")}");
            }

            if (_resources.Latest is { } latest)
            {
                OnSnapshot(latest);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "刷新概览失败");
        }
    }

    private void OnSnapshot(ResourceSnapshot snapshot)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            SystemCpuText = snapshot.SystemCpuPercent is null ? "—" : $"{snapshot.SystemCpuPercent:0.#}%";
            SystemMemoryText = snapshot.SystemMemoryUsedBytes is null
                ? "—"
                : $"{Formatting.Bytes(snapshot.SystemMemoryUsedBytes)} / {Formatting.Bytes(snapshot.SystemMemoryTotalBytes)}";

            var availableGpus = snapshot.Gpus.Where(g => g.IsAvailable).ToList();
            GpuText = availableGpus.Count == 0
                ? "不可用（" + (snapshot.Gpus.FirstOrDefault()?.Note ?? "没有可用的指标来源") + "）"
                : string.Join("；", availableGpus.Select(g =>
                    $"{g.Name}：{(g.UtilizationPercent is null ? "利用率 —" : $"利用率 {g.UtilizationPercent:0.#}%")}，" +
                    $"显存 {Formatting.Bytes(g.MemoryUsedBytes)}/{Formatting.Bytes(g.MemoryTotalBytes)}（{g.Source}）"));

            SampleTimeText = "采样时间：" + snapshot.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");

            foreach (var row in ActiveInstances)
            {
                row.ApplySnapshot(snapshot);
            }
        });
    }

    [RelayCommand]
    private void OpenInstances() => _navigation.NavigateToInstances();

    public void Dispose()
    {
        _resources.SnapshotAvailable -= OnSnapshot;
    }
}
