using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Instances;
using LlamaLauncher.Application.Services;
using LlamaLauncher.App.Services;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.App.ViewModels;

/// <summary>左侧导航项。</summary>
public sealed record NavItem(string Title, string Glyph, object Page);

/// <summary>Shell：导航、状态栏与全局刷新。</summary>
public sealed partial class ShellViewModel : ViewModelBase, IDisposable
{
    private readonly IInstanceManager _instances;
    private readonly IResourceMonitorService _resources;
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly ILogger<ShellViewModel> _logger;
    private readonly DispatcherTimer _statusTimer;

    [ObservableProperty]
    private NavItem? _currentNav;

    [ObservableProperty]
    private string _statusBarText = "就绪";

    [ObservableProperty]
    private string _instancesSummary = "运行中 0 / 共 0";

    [ObservableProperty]
    private string _resourceSummary = "CPU — · 内存 —";

    [ObservableProperty]
    private string _defaultVersionText = "默认 llama.cpp：未安装";

    [ObservableProperty]
    private string _appVersionText = "LlamaLauncher " + (typeof(ShellViewModel).Assembly.GetName().Version?.ToString(3) ?? "");

    [ObservableProperty]
    private bool _isBusyOverlay;

    [ObservableProperty]
    private string? _busyOverlayText;

    public ShellViewModel(
        DashboardViewModel dashboard,
        InstancesViewModel instancesPage,
        ModelsViewModel modelsPage,
        ProfilesViewModel profilesPage,
        ChatViewModel chatPage,
        HardwareViewModel hardwarePage,
        UpdatesViewModel updatesPage,
        LogsViewModel logsPage,
        HistoryViewModel historyPage,
        SettingsViewModel settingsPage,
        IInstanceManager instances,
        IResourceMonitorService resources,
        ISettingsService settings,
        IThemeService theme,
        INotificationService notifications,
        ILogger<ShellViewModel> logger)
    {
        _instances = instances;
        _resources = resources;
        _settings = settings;
        _theme = theme;
        _logger = logger;
        Notifications = notifications;
        Dialogs = null;

        Pages = new ObservableCollection<NavItem>
        {
            new("概览", "▦", dashboard),
            new("服务实例", "◧", instancesPage),
            new("模型库", "▤", modelsPage),
            new("启动配置", "▥", profilesPage),
            new("内置聊天", "◈", chatPage),
            new("硬件与后端", "▣", hardwarePage),
            new("版本管理", "⟳", updatesPage),
            new("日志与诊断", "≡", logsPage),
            new("启动历史", "◷", historyPage),
            new("设置", "⚙", settingsPage),
        };
        CurrentNav = Pages[0];

        notifications.StatusChanged += OnStatusChanged;
        _instances.InstanceChanged += OnInstanceChanged;
        _resources.SnapshotAvailable += OnSnapshot;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateSummaries();
        _statusTimer.Start();
        UpdateSummaries();
    }

    public ObservableCollection<NavItem> Pages { get; }

    public object? CurrentPage => CurrentNav?.Page;

    partial void OnCurrentNavChanged(NavItem? value)
    {
        OnPropertyChanged(nameof(CurrentPage));
        if (value?.Page is IPageViewModel page)
        {
            _ = page.OnActivatedAsync();
        }
    }

    [RelayCommand]
    private void Navigate(NavItem? item)
    {
        if (item is not null)
        {
            CurrentNav = item;
        }
    }

    /// <summary>其他页面请求导航（如实例页跳转到聊天）。</summary>
    public void NavigateTo<T>() where T : class
    {
        var target = Pages.FirstOrDefault(p => p.Page is T);
        if (target is not null)
        {
            Navigate(target);
        }
    }

    public async Task ApplyThemeFromSettingsAsync()
    {
        try
        {
            var settings = await _settings.LoadAsync().ConfigureAwait(true);
            _theme.Apply(settings.Theme);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "应用主题失败");
        }
    }

    private void OnStatusChanged(string? message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() => StatusBarText = message ?? "就绪");
    }

    private void OnInstanceChanged(object? sender, Guid instanceId)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(UpdateSummaries);
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
            var cpu = snapshot.SystemCpuPercent is null ? "—" : $"{snapshot.SystemCpuPercent:0.#}%";
            var memory = snapshot.SystemMemoryUsedBytes is null
                ? "—"
                : $"{Formatting.Bytes(snapshot.SystemMemoryUsedBytes)} / {Formatting.Bytes(snapshot.SystemMemoryTotalBytes)}";
            ResourceSummary = $"CPU {cpu} · 内存 {memory}";
        });
    }

    private void UpdateSummaries()
    {
        try
        {
            var all = _instances.GetInstances();
            var running = all.Count(i => i.IsActive);
            InstancesSummary = $"运行中 {running} / 共 {all.Count}";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "更新实例摘要失败");
        }
    }

    public void SetDefaultVersion(string? versionLabel)
        => DefaultVersionText = versionLabel is null
            ? "默认 llama.cpp：未安装"
            : $"默认 llama.cpp：{versionLabel}";

    public void Dispose()
    {
        _statusTimer.Stop();
        if (Notifications is not null)
        {
            Notifications.StatusChanged -= OnStatusChanged;
        }

        _instances.InstanceChanged -= OnInstanceChanged;
        _resources.SnapshotAvailable -= OnSnapshot;
    }
}
