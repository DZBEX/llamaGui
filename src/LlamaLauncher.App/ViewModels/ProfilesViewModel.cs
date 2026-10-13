using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Exchange;
using LlamaLauncher.App.Services;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.App.ViewModels;

/// <summary>配置档案行。</summary>
public sealed class ProfileRowViewModel
{
    public ProfileRowViewModel(LaunchProfile profile, string? modelName, string? versionLabel)
    {
        Profile = profile;
        ModelName = modelName ?? "（未关联模型）";
        VersionLabel = versionLabel ?? "（未选择程序）";
    }

    public LaunchProfile Profile { get; }

    public Guid Id => Profile.Id;

    public string Name => Profile.Name;

    public string ModelName { get; }

    public string VersionLabel { get; }

    public string EndpointText => $"{Profile.Options.Host}:{Profile.Options.Port}";

    public string ScopeText => HostAddress.Classify(Profile.Options.Host) switch
    {
        AddressScope.Localhost => "仅本机",
        AddressScope.Lan => "局域网",
        AddressScope.Public => "公网（不允许）",
        _ => "无效地址",
    };

    public bool IsDefault => Profile.IsDefault;

    public string UpdatedText => Formatting.LocalTime(Profile.UpdatedUtc);
}

/// <summary>启动配置页。</summary>
public sealed partial class ProfilesViewModel : ViewModelBase, IPageViewModel
{
    private readonly ILaunchProfileService _profiles;
    private readonly IModelCatalogService _models;
    private readonly ILlamaInstallationService _installations;
    private readonly IProfileExchangeService _exchange;
    private readonly Func<ProfileEditorViewModel> _editorFactory;
    private readonly ILogger<ProfilesViewModel> _logger;

    [ObservableProperty]
    private ProfileRowViewModel? _selectedProfile;

    public ProfilesViewModel(
        ILaunchProfileService profiles,
        IModelCatalogService models,
        ILlamaInstallationService installations,
        IProfileExchangeService exchange,
        Func<ProfileEditorViewModel> editorFactory,
        IDialogService dialogs,
        INotificationService notifications,
        ILogger<ProfilesViewModel> logger)
    {
        _profiles = profiles;
        _models = models;
        _installations = installations;
        _exchange = exchange;
        _editorFactory = editorFactory;
        _logger = logger;
        Dialogs = dialogs;
        Notifications = notifications;
    }

    /// <summary>请求打开编辑窗口（由 View 承载）。</summary>
    public event Action<ProfileEditorViewModel>? EditRequested;

    /// <summary>请求展示导入预览（由 View 承载后回调 ApplyImportAsync）。</summary>
    public event Action<ImportPlan>? ImportPreviewRequested;

    public ObservableCollection<ProfileRowViewModel> Profiles { get; } = new();

    public Task OnActivatedAsync() => ReloadAsync();

    public async Task ReloadAsync()
    {
        var selectedId = SelectedProfile?.Id;
        var profiles = await _profiles.GetAllAsync();
        var models = (await _models.GetAllAsync()).ToDictionary(m => m.Id);
        var installations = (await _installations.GetAllAsync()).ToDictionary(i => i.Id);

        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(new ProfileRowViewModel(
                profile,
                profile.ModelId is { } modelId && models.TryGetValue(modelId, out var model) ? model.DisplayName : null,
                profile.InstallationId is { } installationId && installations.TryGetValue(installationId, out var installation)
                    ? installation.VersionLabel
                    : null));
        }

        if (selectedId is { } id)
        {
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == id);
        }
    }

    [RelayCommand]
    private async Task CreateAsync() => await OpenEditorAsync(null);

    [RelayCommand]
    private async Task EditAsync()
    {
        if (SelectedProfile is { } row)
        {
            await OpenEditorAsync(row.Profile);
        }
    }

    private async Task OpenEditorAsync(LaunchProfile? profile)
    {
        var editor = _editorFactory();
        editor.Saved += () => _ = ReloadAsync();
        await editor.InitializeAsync(profile);
        EditRequested?.Invoke(editor);
    }

    [RelayCommand]
    private async Task DuplicateAsync()
    {
        if (SelectedProfile is not { } row)
        {
            return;
        }

        var name = await Dialogs!.PromptAsync("复制配置", "新配置名称：", row.Name + " 副本");
        if (name is null)
        {
            return;
        }

        await RunBusyAsync("正在复制配置…", async () =>
        {
            var copy = await _profiles.DuplicateAsync(row.Id, name);
            await ReloadAsync();
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == copy.Id);
        });
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedProfile is not { } row)
        {
            return;
        }

        var confirmed = await Dialogs!.ConfirmAsync(new ConfirmDialogOptions
        {
            Title = "删除配置",
            Message = $"将删除配置“{row.Name}”。\n不会删除模型文件或运行中的实例。",
            PrimaryButtonText = "删除",
            SecondaryButtonText = "取消",
            IsDestructive = true,
        });
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _profiles.DeleteAsync(row.Id);
            await ReloadAsync();
            Notifications?.Notify("配置已删除。");
        }
        catch (LaunchProfileException ex)
        {
            await Dialogs.ShowErrorAsync("无法删除", ex.Message);
        }
    }

    [RelayCommand]
    private async Task SetDefaultAsync()
    {
        if (SelectedProfile is not { } row)
        {
            return;
        }

        await RunBusyAsync("正在设为默认…", async () =>
        {
            await _profiles.SetDefaultAsync(row.Id);
            await ReloadAsync();
            Notifications?.Notify($"“{row.Name}”已设为默认配置。");
        });
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var profiles = await _profiles.GetAllAsync();
        if (profiles.Count == 0)
        {
            await Dialogs!.ShowWarningAsync("没有可导出的配置", "请先创建至少一个配置档案。");
            return;
        }

        var (confirmed, includeSecrets) = await Dialogs!.ConfirmWithCheckboxAsync(
            new ConfirmDialogOptions
            {
                Title = "导出配置档案",
                Message = "将把全部配置档案导出为版本化 JSON 文件。\n\n" +
                          "默认不包含密钥引用名，也不会包含任何密钥本体（密钥始终只保存在本机 DPAPI 存储中）。",
                PrimaryButtonText = "选择导出位置",
                SecondaryButtonText = "取消",
            },
            "包含密钥引用名（不含密钥本体）",
            defaultChecked: false);
        if (!confirmed)
        {
            return;
        }

        var path = Dialogs.PickSaveFile(
            "导出配置档案",
            "JSON 配置|*.json",
            $"llamalauncher-profiles-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        if (path is null)
        {
            return;
        }

        await RunBusyAsync("正在导出…", async () =>
        {
            await _exchange.ExportAsync(null, path, includeSecrets);
            await Dialogs!.ShowInfoAsync("导出完成", "配置档案已导出到：\n" + path + "\n\n导出文件不会包含密钥本体。");
        });
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var path = Dialogs!.PickOpenFile("导入配置档案", "JSON 配置|*.json");
        if (path is null)
        {
            return;
        }

        await RunBusyAsync("正在解析导入文件…", async () =>
        {
            var plan = await _exchange.PreviewAsync(path);
            ImportPreviewRequested?.Invoke(plan);
        });
    }

    /// <summary>由 View 在导入预览对话框确认后调用。</summary>
    public async Task<(int Added, int Overwritten, int Skipped)> ApplyImportAsync(
        ImportPlan plan,
        IReadOnlySet<int>? selectedIndexes)
    {
        var result = await _exchange.ApplyAsync(plan, selectedIndexes);
        await ReloadAsync();
        Notifications?.Notify($"导入完成：新增 {result.Added}，覆盖 {result.Overwritten}，跳过 {result.Skipped}。");
        return (result.Added, result.Overwritten, result.Skipped);
    }
}
