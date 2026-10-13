using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Models;
using LlamaLauncher.Application.Services;
using LlamaLauncher.App.Services;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.App.ViewModels;

/// <summary>模型行视图模型。</summary>
public sealed partial class ModelRowViewModel : ObservableObject
{
    public ModelRowViewModel(ModelRecord record) => Record = record;

    public ModelRecord Record { get; }

    public Guid Id => Record.Id;

    public string DisplayName => Record.DisplayName;

    public string FilePath => Record.FilePath;

    public string SizeText => Formatting.Bytes(Record.FileSizeBytes);

    public string StatusText => Formatting.ModelStatusText(Record.Status);

    public ModelFileStatus Status => Record.Status;

    public string Architecture => Record.Metadata?.Architecture ?? "未知/未提供";

    public string Quantization => Record.Metadata?.QuantizationLabel ?? "未知/未提供";

    public string ContextLengthText => Record.Metadata?.ContextLength is { } length
        ? length.ToString("N0")
        : "未知/未提供";

    public string ParameterCountText => Record.Metadata?.ParameterCount is { } count
        ? FormatParameterCount(count)
        : "未知/未提供";

    public string FileTimeText => Formatting.LocalTime(Record.FileLastWriteUtc);

    public string TagsText => Record.Tags.Count == 0 ? "—" : string.Join(", ", Record.Tags);

    public string? Notes => Record.Notes;

    public string? Alias => Record.Alias;

    private static string FormatParameterCount(long count)
        => count >= 1_000_000_000
            ? $"{count / 1_000_000_000.0:0.##}B"
            : count >= 1_000_000
                ? $"{count / 1_000_000.0:0.##}M"
                : count.ToString("N0");
}

/// <summary>模型库页。</summary>
public sealed partial class ModelsViewModel : ViewModelBase, IPageViewModel
{
    private readonly IModelCatalogService _catalog;
    private readonly IClipboardService _clipboard;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly ILogger<ModelsViewModel> _logger;
    private CancellationTokenSource? _scanCts;

    [ObservableProperty]
    private ModelRowViewModel? _selectedModel;

    [ObservableProperty]
    private string? _editAlias;

    [ObservableProperty]
    private string? _editTags;

    [ObservableProperty]
    private string? _editNotes;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string? _scanProgressText;

    [ObservableProperty]
    private string _metadataSummary = "选择一个模型查看详情";

    [ObservableProperty]
    private bool _includeSubdirectories;

    public ModelsViewModel(
        IModelCatalogService catalog,
        IClipboardService clipboard,
        ISettingsService settings,
        IDialogService dialogs,
        INotificationService notifications,
        ILogger<ModelsViewModel> logger)
    {
        _catalog = catalog;
        _clipboard = clipboard;
        _settings = settings;
        _notifications = notifications;
        _logger = logger;
        Dialogs = dialogs;
        Notifications = notifications;
    }

    public ObservableCollection<ModelRowViewModel> Models { get; } = new();

    public async Task OnActivatedAsync() => await ReloadAsync();

    public async Task ReloadAsync()
    {
        try
        {
            var models = await _catalog.GetAllAsync();
            var selectedId = SelectedModel?.Id;
            Models.Clear();
            foreach (var model in models)
            {
                Models.Add(new ModelRowViewModel(model));
            }

            if (selectedId is { } id)
            {
                SelectedModel = Models.FirstOrDefault(m => m.Id == id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载模型库失败");
            Notifications?.Notify("加载模型库失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private async Task AddFileAsync()
    {
        var settings = await _settings.LoadAsync();
        var path = Dialogs!.PickOpenFile(
            "选择 GGUF 模型文件",
            "GGUF 模型 (*.gguf)|*.gguf|所有文件|*.*",
            settings.LastModelScanDirectory);
        if (path is null)
        {
            return;
        }

        await RunBusyAsync("正在检查并登记模型…", async () =>
        {
            var model = await _catalog.AddAsync(path);
            await ReloadAsync();
            SelectedModel = Models.FirstOrDefault(m => m.Id == model.Id);
            Notifications?.Notify($"已登记模型“{model.DisplayName}”。");
        }, "无法添加模型");
    }

    [RelayCommand]
    private async Task ScanDirectoryAsync()
    {
        var settings = await _settings.LoadAsync();
        var directory = Dialogs!.PickFolder("选择模型扫描目录", settings.LastModelScanDirectory);
        if (directory is null)
        {
            return;
        }

        settings.LastModelScanDirectory = directory;
        await _settings.SaveAsync(settings);

        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        ScanProgressText = "正在枚举目录…";
        try
        {
            var progress = new Progress<ModelScanProgress>(p =>
            {
                ScanProgressText = $"已发现 {p.Discovered} 个文件，已检查 {p.Examined} 个：{Path.GetFileName(p.CurrentFile ?? string.Empty)}";
            });
            var added = await _catalog.ScanDirectoryAsync(
                directory,
                IncludeSubdirectories,
                progress,
                _scanCts.Token);
            await ReloadAsync();
            Notifications?.Notify($"扫描完成：新增 {added.Count} 个模型（仅扫描所选目录，不删除任何文件）。");
        }
        catch (OperationCanceledException)
        {
            Notifications?.Notify("目录扫描已取消。");
        }
        catch (Exception ex)
        {
            await Dialogs!.ShowErrorAsync("扫描失败", ex.Message);
        }
        finally
        {
            IsScanning = false;
            ScanProgressText = null;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    [RelayCommand]
    private async Task RevalidateSelectedAsync()
    {
        if (SelectedModel is not { } row)
        {
            return;
        }

        await RunBusyAsync("正在重新检查文件…", async () =>
        {
            await _catalog.RevalidateAsync(row.Id);
            await ReloadAsync();
        });
    }

    [RelayCommand]
    private async Task RevalidateAllAsync()
    {
        await RunBusyAsync("正在检查全部模型…", async () =>
        {
            await _catalog.RevalidateAllAsync();
            await ReloadAsync();
            Notifications?.Notify("全部模型检查完成。");
        });
    }

    [RelayCommand]
    private async Task SaveDetailsAsync()
    {
        if (SelectedModel is not { } row)
        {
            return;
        }

        var tags = (EditTags ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        await RunBusyAsync("正在保存…", async () =>
        {
            await _catalog.UpdateDetailsAsync(row.Id, EditAlias, tags, EditNotes);
            await ReloadAsync();
            Notifications?.Notify("模型信息已更新。");
        });
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (SelectedModel is not { } row)
        {
            return;
        }

        var confirmed = await Dialogs!.ConfirmAsync(new ConfirmDialogOptions
        {
            Title = "移除模型登记",
            Message = $"将从模型库中移除“{row.DisplayName}”的登记。\n\n" +
                      "此操作不会删除磁盘上的模型文件：\n" + row.FilePath,
            PrimaryButtonText = "移除登记",
            SecondaryButtonText = "取消",
            IsDestructive = true,
        });
        if (!confirmed)
        {
            return;
        }

        await RunBusyAsync("正在移除…", async () =>
        {
            await _catalog.RemoveAsync(row.Id);
            await ReloadAsync();
        });
    }

    [RelayCommand]
    private async Task CopyPathAsync()
    {
        if (SelectedModel is not { } row)
        {
            return;
        }

        var (success, error) = await _clipboard.SetTextAsync(row.FilePath);
        Notifications?.Notify(success ? "路径已复制。" : error ?? "复制失败。");
    }

    [RelayCommand]
    private void OpenInExplorer()
    {
        if (SelectedModel is not { } row)
        {
            return;
        }

        try
        {
            if (File.Exists(row.FilePath))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{row.FilePath}\"");
            }
            else
            {
                Notifications?.Notify("文件不存在，无法在资源管理器中定位。");
            }
        }
        catch (Exception ex)
        {
            Notifications?.Notify("打开资源管理器失败：" + ex.Message);
        }
    }

    partial void OnSelectedModelChanged(ModelRowViewModel? value)
    {
        EditAlias = value?.Alias;
        EditTags = value is null ? null : string.Join(", ", value.Record.Tags);
        EditNotes = value?.Notes;
        MetadataSummary = value is null
            ? "选择一个模型查看详情"
            : BuildMetadataSummary(value.Record);
    }

    private static string BuildMetadataSummary(ModelRecord record)
    {
        var metadata = record.Metadata;
        if (metadata is null)
        {
            return record.MetadataError is { Length: > 0 } error
                ? "元数据不可用：" + error
                : "元数据未解析。";
        }

        var parts = new List<string>();
        parts.Add($"GGUF v{metadata.GgufVersion?.ToString() ?? "?"}");
        parts.Add($"架构：{metadata.Architecture ?? "未知"}");
        parts.Add($"量化：{metadata.QuantizationLabel ?? "未知"}");
        parts.Add($"上下文：{(metadata.ContextLength?.ToString("N0") ?? "未知")}");
        parts.Add($"张量数：{metadata.TensorCount?.ToString("N0") ?? "未知"}");
        if (metadata.VocabularySize is { } vocab)
        {
            parts.Add($"词表：{vocab:N0}");
        }

        parts.Add($"解析时间：{Formatting.LocalTime(metadata.ParsedUtc)}");
        if (metadata.AdditionalMetadata.Count > 0)
        {
            parts.Add("附加元数据：" + string.Join("；", metadata.AdditionalMetadata.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        return string.Join("\n", parts);
    }
}
