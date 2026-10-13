using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.App.Services;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.App.ViewModels;

/// <summary>显示适配器行。</summary>
public sealed record AdapterRow(string Name, string Vendor, string Driver, string Memory, string Source);

/// <summary>GPU 指标行。</summary>
public sealed record GpuMetricRow(string Name, string Utilization, string Memory, string Source, string Note);

/// <summary>能力探测参数行。</summary>
public sealed record OptionRow(string Canonical, string Flags, string ValueKind, string Description);

/// <summary>设备行（--list-devices）。</summary>
public sealed record DeviceRow(string Backend, string Index, string Name);

/// <summary>硬件与后端页。</summary>
public sealed partial class HardwareViewModel : ViewModelBase, IPageViewModel
{
    private readonly IHardwareProbeService _hardware;
    private readonly ILlamaInstallationService _installations;
    private readonly ILlamaBinaryProbeService _probeService;
    private readonly ILogger<HardwareViewModel> _logger;

    [ObservableProperty]
    private string _osText = "—";

    [ObservableProperty]
    private string _cpuText = "—";

    [ObservableProperty]
    private string _memoryText = "—";

    [ObservableProperty]
    private string _nvidiaSmiText = "检测中…";

    [ObservableProperty]
    private string _probeTargetText = "尚未选择要探测的 llama-server.exe";

    [ObservableProperty]
    private string? _probeVersionText;

    [ObservableProperty]
    private string? _probeWarningsText;

    [ObservableProperty]
    private string? _rawHelpText;

    [ObservableProperty]
    private string? _rawDevicesText;

    [ObservableProperty]
    private LlamaCppInstallation? _selectedInstallation;

    public HardwareViewModel(
        IHardwareProbeService hardware,
        ILlamaInstallationService installations,
        ILlamaBinaryProbeService probeService,
        IDialogService dialogs,
        INotificationService notifications,
        ILogger<HardwareViewModel> logger)
    {
        _hardware = hardware;
        _installations = installations;
        _probeService = probeService;
        _logger = logger;
        Dialogs = dialogs;
        Notifications = notifications;
    }

    public ObservableCollection<AdapterRow> DisplayAdapters { get; } = new();

    public ObservableCollection<GpuMetricRow> GpuMetrics { get; } = new();

    public ObservableCollection<LlamaCppInstallation> Installations { get; } = new();

    public ObservableCollection<OptionRow> SupportedOptions { get; } = new();

    public ObservableCollection<DeviceRow> DetectedDevices { get; } = new();

    public async Task OnActivatedAsync()
    {
        await RefreshSystemAsync();
        await RefreshInstallationsAsync();
    }

    [RelayCommand]
    private async Task RefreshSystemAsync()
    {
        await RunBusyAsync("正在读取系统硬件信息…", async () =>
        {
            var info = await _hardware.ProbeAsync();
            OsText = $"{info.OsName} {info.OsVersion}（Build {info.OsBuild}，{info.Architecture}）";
            CpuText = $"{info.CpuName} · {info.LogicalProcessors} 个逻辑处理器";
            MemoryText = Formatting.Bytes(info.TotalPhysicalMemoryBytes);

            DisplayAdapters.Clear();
            foreach (var adapter in info.DisplayAdapters)
            {
                DisplayAdapters.Add(new AdapterRow(
                    adapter.Name,
                    adapter.Vendor ?? "—",
                    adapter.DriverVersion ?? "—",
                    adapter.ReportedAdapterRamBytes is null ? "—" : Formatting.Bytes(adapter.ReportedAdapterRamBytes),
                    adapter.Source));
            }

            GpuMetrics.Clear();
            if (info.NvidiaMetrics.Count == 0)
            {
                NvidiaSmiText = "nvidia-smi 不可用或未检测到 NVIDIA 设备；其他厂商 GPU 的利用率指标暂不提供（显示“—”，不伪造数值）。";
            }
            else
            {
                NvidiaSmiText = $"nvidia-smi 可用（{info.NvidiaMetrics.Count} 个 GPU）";
                foreach (var metric in info.NvidiaMetrics)
                {
                    GpuMetrics.Add(new GpuMetricRow(
                        metric.Name,
                        Formatting.Percent(metric.UtilizationPercent),
                        $"{Formatting.Bytes(metric.MemoryUsedBytes)} / {Formatting.Bytes(metric.MemoryTotalBytes)}",
                        metric.Source,
                        metric.Note ?? string.Empty));
                }
            }
        });
    }

    [RelayCommand]
    private async Task RefreshInstallationsAsync()
    {
        var installations = await _installations.GetAllAsync();
        Installations.Clear();
        foreach (var installation in installations)
        {
            Installations.Add(installation);
        }

        SelectedInstallation ??= Installations.FirstOrDefault(i => i.IsDefault) ?? Installations.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ProbeSelectedAsync()
    {
        if (SelectedInstallation is not { } installation)
        {
            return;
        }

        await ProbePathAsync(installation.ExecutablePath, installation.VersionLabel);
    }

    [RelayCommand]
    private async Task PickAndProbeExecutableAsync()
    {
        var path = Dialogs!.PickOpenFile("选择 llama-server.exe", "可执行文件 (*.exe)|*.exe|所有文件|*.*");
        if (path is null)
        {
            return;
        }

        await ProbePathAsync(path, "自定义路径");
    }

    private async Task ProbePathAsync(string executablePath, string label)
    {
        await RunBusyAsync("正在探测程序能力（--version / --help / --list-devices）…", async () =>
        {
            ProbeTargetText = $"{label}：{executablePath}";
            var capabilities = await _probeService.ProbeAsync(executablePath, forceRefresh: true);

            ProbeVersionText = capabilities.VersionText ?? "（无版本输出）";
            ProbeWarningsText = capabilities.Warnings.Count == 0
                ? null
                : string.Join("\n", capabilities.Warnings.Select(w => "· " + w));

            SupportedOptions.Clear();
            foreach (var option in capabilities.Options)
            {
                SupportedOptions.Add(new OptionRow(
                    option.CanonicalName,
                    string.Join(", ", option.Flags),
                    option.ValueKind switch
                    {
                        OptionValueKind.Required => "需要取值" + (option.ValueHint is null ? "" : $"（{option.ValueHint}）"),
                        OptionValueKind.Optional => "可选取值" + (option.ValueHint is null ? "" : $"（{option.ValueHint}）"),
                        _ => "开关",
                    },
                    option.Description ?? string.Empty));
            }

            DetectedDevices.Clear();
            foreach (var device in capabilities.Devices)
            {
                DetectedDevices.Add(new DeviceRow(
                    Formatting.BackendText(device.Backend),
                    device.Index?.ToString() ?? "—",
                    device.Name));
            }

            RawHelpText = capabilities.HelpText;
            RawDevicesText = capabilities.ListDevicesText;

            Notifications?.Notify(
                capabilities.Outcome switch
                {
                    ProbeOutcome.Success => $"能力探测完成：支持参数 {capabilities.Options.Count} 个，设备 {capabilities.Devices.Count} 台。",
                    ProbeOutcome.PartialFailure => "能力探测部分成功（部分命令失败，详见警告）。",
                    _ => "能力探测失败：该程序可能不可用，详见警告与原始输出。",
                });
        }, "能力探测失败");
    }
}
