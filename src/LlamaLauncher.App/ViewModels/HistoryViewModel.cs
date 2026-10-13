using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.App.Services;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.App.ViewModels;

/// <summary>启动历史行。</summary>
public sealed record HistoryRow(StartupHistoryEntry Entry)
{
    public string StartedText => Formatting.LocalTime(Entry.StartedUtc);

    public string EndedText => Formatting.LocalTime(Entry.EndedUtc);

    public string InstanceName => Entry.InstanceNameSnapshot ?? Entry.InstanceId.ToString("N")[..8];

    /// <summary>仍在运行的记录显示“运行中”（异常退出/停止后的记录在关闭时会写入结束时间）。</summary>
    public string FinalStateText => Entry.EndedUtc is null
        ? "运行中"
        : Formatting.InstanceStateText(Entry.FinalState);

    public string ExitCodeText => Entry.ExitCode?.ToString() ?? "—";

    public string PidText => Entry.ProcessId?.ToString() ?? "—";

    public string ErrorText => Entry.ErrorSummary ?? string.Empty;

    public string FailureStageText => Entry.FailureStage ?? string.Empty;
}

/// <summary>启动历史页。</summary>
public sealed partial class HistoryViewModel : ViewModelBase, IPageViewModel
{
    private readonly IStartupHistoryRepository _history;
    private readonly ILogger<HistoryViewModel> _logger;

    [ObservableProperty]
    private HistoryRow? _selectedRow;

    [ObservableProperty]
    private string? _detailMaskedCommand;

    [ObservableProperty]
    private string? _detailError;

    [ObservableProperty]
    private string? _detailModelSnapshot;

    [ObservableProperty]
    private string? _detailInstallationSnapshot;

    [ObservableProperty]
    private string? _detailLogFilePath;

    [ObservableProperty]
    private string _instanceFilterText = string.Empty;

    [ObservableProperty]
    private bool _onlyFailures;

    public HistoryViewModel(
        IStartupHistoryRepository history,
        IDialogService dialogs,
        INotificationService notifications,
        ILogger<HistoryViewModel> logger)
    {
        _history = history;
        _logger = logger;
        Dialogs = dialogs;
        Notifications = notifications;
    }

    public ObservableCollection<HistoryRow> Rows { get; } = new();

    public Task OnActivatedAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RunBusyAsync("正在加载启动历史…", async () =>
        {
            var entries = await _history.QueryAsync(new HistoryQuery { Limit = 500 });
            var filter = InstanceFilterText?.Trim();
            Rows.Clear();
            foreach (var entry in entries)
            {
                if (OnlyFailures
                    && entry.FinalState is not (InstanceState.Failed or InstanceState.ExitedUnexpectedly))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(filter)
                    && !(entry.InstanceNameSnapshot?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    continue;
                }

                Rows.Add(new HistoryRow(entry));
            }
        });
    }

    [RelayCommand]
    private void OpenLogFile()
    {
        if (DetailLogFilePath is not { Length: > 0 } path || !File.Exists(path))
        {
            Notifications?.Notify("该记录的日志文件不存在。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notifications?.Notify("打开日志文件失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private Task CopyCommandAsync()
    {
        if (DetailMaskedCommand is not { Length: > 0 } command)
        {
            return Task.CompletedTask;
        }

        try
        {
            System.Windows.Clipboard.SetText(command);
            Notifications?.Notify("已复制脱敏命令行。");
        }
        catch (Exception ex)
        {
            Notifications?.Notify("复制失败：" + ex.Message);
        }

        return Task.CompletedTask;
    }

    partial void OnSelectedRowChanged(HistoryRow? value)
    {
        if (value is null)
        {
            DetailMaskedCommand = null;
            DetailError = null;
            DetailModelSnapshot = null;
            DetailInstallationSnapshot = null;
            DetailLogFilePath = null;
            return;
        }

        DetailMaskedCommand = value.Entry.CommandLineMasked;
        DetailError = string.IsNullOrWhiteSpace(value.ErrorText)
            ? null
            : $"{value.ErrorText}" + (string.IsNullOrEmpty(value.FailureStageText) ? "" : $"（阶段：{value.FailureStageText}）");
        DetailModelSnapshot = PrettyPrint(value.Entry.ModelSnapshotJson);
        DetailInstallationSnapshot = PrettyPrint(value.Entry.InstallationSnapshotJson);
        DetailLogFilePath = value.Entry.LogFilePath;
    }

    private static string? PrettyPrint(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
