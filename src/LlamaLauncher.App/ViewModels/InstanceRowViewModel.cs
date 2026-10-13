using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Instances;
using LlamaLauncher.App.Services;
using LlamaLauncher.Domain;

namespace LlamaLauncher.App.ViewModels;

/// <summary>单个实例的行视图模型（供概览/实例/聊天共用）。</summary>
public sealed partial class InstanceRowViewModel : ObservableObject
{
    private readonly IInstanceManager _manager;
    private readonly IClipboardService _clipboard;
    private readonly IUiActions _navigation;
    private readonly INotificationService? _notifications;
    private readonly ManagedInstance _instance;

    public InstanceRowViewModel(
        ManagedInstance instance,
        IInstanceManager manager,
        IClipboardService clipboard,
        IUiActions navigation,
        INotificationService? notifications)
    {
        _instance = instance;
        _manager = manager;
        _clipboard = clipboard;
        _navigation = navigation;
        _notifications = notifications;
    }

    public Guid Id => _instance.Id;

    public string Name => _instance.Name;

    public string ModelName => _instance.Profile?.ModelSnapshot?.Name
        ?? (_instance.Record.ModelId is { } id ? "模型 " + id.ToString("N")[..8] : "—");

    public string VersionLabel => _instance.Profile?.InstallationSnapshot?.VersionLabel ?? "—";

    public int? ProcessId => _instance.Record.ProcessId;

    public string ProcessIdText => ProcessId?.ToString() ?? "—";

    public int Port => _instance.Record.Port;

    public string Url => _instance.LocalBaseUrl;

    public InstanceState State => _instance.State;

    public string StateText => Formatting.InstanceStateText(_instance.State);

    public string HealthText => _instance.Health switch
    {
        HealthStatusKind.Ready => "正常",
        HealthStatusKind.Loading => "加载中" + (_instance.HealthDetail is { Length: > 0 } d ? $"（{d}）" : ""),
        HealthStatusKind.Error => "错误" + (_instance.HealthDetail is { Length: > 0 } d2 ? $"（{d2}）" : ""),
        _ => "未知",
    };

    public string StartedText => Formatting.LocalTime(_instance.Record.StartedUtc);

    public string UptimeText => _instance.Record.StartedUtc is { } started && _instance.IsActive
        ? Formatting.Duration(DateTimeOffset.UtcNow - started)
        : "—";

    public string CpuText { get; private set; } = "—";

    public string MemoryText { get; private set; } = "—";

    public string? LastError => _instance.Record.LastError;

    public bool IsActive => _instance.IsActive;

    public bool CanStart => _instance.State is InstanceState.Stopped or InstanceState.Failed or InstanceState.ExitedUnexpectedly;

    public bool CanStop => _instance.IsActive;

    public bool CanOpenChat => _instance.State == InstanceState.Ready;

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
    }

    public void ApplySnapshot(ResourceSnapshot snapshot)
    {
        var metrics = snapshot.Processes.FirstOrDefault(p => p.InstanceId == Id);
        CpuText = metrics is null ? "—" : Formatting.Percent(metrics.CpuPercent);
        MemoryText = metrics is null ? "—" : Formatting.Bytes(metrics.WorkingSetBytes);
        OnPropertyChanged(nameof(CpuText));
        OnPropertyChanged(nameof(MemoryText));
        OnPropertyChanged(nameof(UptimeText));
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        // 局域网确认在实例页统一处理；此处用于概览的快捷启动
        var lanRequired = HostAddress.IsNonLocal(HostAddress.Classify(_instance.Record.Host));
        var confirmed = !lanRequired;
        if (lanRequired && _notifications is not null)
        {
            _notifications.Notify("该实例监听局域网地址，请到“服务实例”页在确认风险后启动。");
            return;
        }

        var result = await _manager.StartAsync(Id, waitForReady: false, lanAccessConfirmed: confirmed);
        _notifications?.Notify(result.Success ? $"实例“{Name}”正在启动。" : "启动失败：" + result.Message);
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        await _manager.StopAsync(Id);
        _notifications?.Notify($"实例“{Name}”已停止。");
    }

    [RelayCommand]
    private void OpenChat() => _navigation.NavigateToChat(Id);

    [RelayCommand]
    private void OpenLogs() => _navigation.NavigateToLogs(Id);

    [RelayCommand]
    private async Task CopyUrlAsync()
    {
        var (success, error) = await _clipboard.SetTextAsync(Url);
        _notifications?.Notify(success ? "API 地址已复制。" : error ?? "复制失败。");
    }
}
