using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.App.Services;
using LlamaLauncher.Domain;
using LlamaLauncher.Domain.Validation;

namespace LlamaLauncher.App.ViewModels;

/// <summary>环境变量编辑行。</summary>
public sealed partial class EnvironmentVariableRow : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;
}

/// <summary>校验问题展示行。</summary>
public sealed record ValidationIssueRow(string SeverityText, string Message)
{
    public bool IsBlocking => SeverityText == "错误";
}

/// <summary>
/// 启动配置编辑器：结构化参数面板（按能力快照启用/禁用）、预设、高级额外参数与命令预览。
/// </summary>
public sealed partial class ProfileEditorViewModel : ObservableObject
{
    /// <summary>Flash Attention 下拉选项。</summary>
    public static readonly string[] FlashAttentionOptions = { "默认（不指定）", "启用 (on)", "关闭 (off)", "自动 (auto)" };

    /// <summary>连续批处理三态选项。</summary>
    public static readonly string[] TriStateOptions = { "默认（不指定）", "启用", "禁用" };

    /// <summary>分割模式选项。</summary>
    public static readonly string[] SplitModeOptions = { "（不指定）", "none", "layer", "row" };

    /// <summary>KV cache 类型选项（未知类型可直接输入）。</summary>
    public static readonly string[] CacheTypeOptions = { "（不指定）", "f16", "f32", "bf16", "q8_0", "q4_0", "q4_1", "iq4_nl", "q5_0", "q5_1" };

    private readonly ILaunchProfileService _profiles;
    private readonly IModelCatalogService _models;
    private readonly ILlamaInstallationService _installations;
    private readonly ILlamaBinaryProbeService _probeService;
    private readonly INotificationService _notifications;
    private LaunchProfile _profile = new();

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private ModelRecord? _selectedModel;

    [ObservableProperty]
    private LlamaCppInstallation? _selectedInstallation;

    [ObservableProperty]
    private string _host = "127.0.0.1";

    [ObservableProperty]
    private string _portText = "8080";

    [ObservableProperty]
    private string _contextSizeText = string.Empty;

    [ObservableProperty]
    private string _threadsText = string.Empty;

    [ObservableProperty]
    private string _threadsBatchText = string.Empty;

    [ObservableProperty]
    private string _batchSizeText = string.Empty;

    [ObservableProperty]
    private string _uBatchSizeText = string.Empty;

    [ObservableProperty]
    private string _gpuLayersText = string.Empty;

    [ObservableProperty]
    private string _devicesText = string.Empty;

    [ObservableProperty]
    private string _splitMode = SplitModeOptions[0];

    [ObservableProperty]
    private string _tensorSplitText = string.Empty;

    [ObservableProperty]
    private string _mainGpuText = string.Empty;

    [ObservableProperty]
    private string _cacheTypeK = CacheTypeOptions[0];

    [ObservableProperty]
    private string _cacheTypeV = CacheTypeOptions[0];

    [ObservableProperty]
    private string _flashAttention = FlashAttentionOptions[0];

    [ObservableProperty]
    private string _alias = string.Empty;

    [ObservableProperty]
    private string _parallelText = string.Empty;

    [ObservableProperty]
    private string _continuousBatching = TriStateOptions[0];

    [ObservableProperty]
    private bool _verboseLogging;

    [ObservableProperty]
    private bool _noMemoryMap;

    [ObservableProperty]
    private bool _apiKeyEnabled;

    [ObservableProperty]
    private string _apiKeySecretName = SecretNames.DefaultApiKey;

    [ObservableProperty]
    private string _startupTimeoutText = "600";

    [ObservableProperty]
    private string _stopGraceText = "5";

    [ObservableProperty]
    private string _extraArguments = string.Empty;

    [ObservableProperty]
    private bool _allowOverride;

    [ObservableProperty]
    private string _notes = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _commandPreview = "（点击“校验并预览”生成脱敏命令预览）";

    [ObservableProperty]
    private bool _isValidated;

    // 能力状态
    [ObservableProperty]
    private string _capabilitySummary = "尚未选择 llama.cpp 程序。";

    [ObservableProperty]
    private bool _supportsCtxSize = true;

    [ObservableProperty]
    private bool _supportsThreads = true;

    [ObservableProperty]
    private bool _supportsBatch = true;

    [ObservableProperty]
    private bool _supportsGpuLayers = true;

    [ObservableProperty]
    private bool _supportsGpuAutoLayers;

    [ObservableProperty]
    private bool _hasGpuDevices;

    [ObservableProperty]
    private bool _supportsDevices = true;

    [ObservableProperty]
    private bool _supportsKvCache = true;

    [ObservableProperty]
    private bool _supportsFlashAttention = true;

    [ObservableProperty]
    private bool _flashAttentionValueForm = true;

    [ObservableProperty]
    private bool _supportsParallel = true;

    [ObservableProperty]
    private bool _supportsContBatching = true;

    [ObservableProperty]
    private bool _supportsApiKey = true;

    [ObservableProperty]
    private bool _supportsVerbose = true;

    [ObservableProperty]
    private bool _supportsNoMmap = true;

    public ProfileEditorViewModel(
        ILaunchProfileService profiles,
        IModelCatalogService models,
        ILlamaInstallationService installations,
        ILlamaBinaryProbeService probeService,
        INotificationService notifications)
    {
        _profiles = profiles;
        _models = models;
        _installations = installations;
        _probeService = probeService;
        _notifications = notifications;
    }

    public ObservableCollection<ModelRecord> AvailableModels { get; } = new();

    public ObservableCollection<LlamaCppInstallation> AvailableInstallations { get; } = new();

    public ObservableCollection<EnvironmentVariableRow> EnvironmentVariables { get; } = new();

    public ObservableCollection<ValidationIssueRow> Issues { get; } = new();

    public string[] FlashAttentionChoices => FlashAttentionOptions;

    public string[] TriStateChoices => TriStateOptions;

    public string[] SplitModeChoices => SplitModeOptions;

    public string[] CacheTypeChoices => CacheTypeOptions;

    public bool IsNew => _profile.Id == Guid.Empty || _profile.CreatedUtc == default;

    public event Action? Saved;

    public event Action? CloseRequested;

    /// <summary>按是否支持某项设置生成字段提示（绑定在 ToolTip 上）。</summary>
    public string UnsupportedHint(string optionName) => $"当前 llama.cpp 版本的能力快照未包含 {optionName}，无法启用该设置。";

    public async Task InitializeAsync(LaunchProfile? profile)
    {
        var models = await _models.GetAllAsync();
        AvailableModels.Clear();
        foreach (var model in models)
        {
            AvailableModels.Add(model);
        }

        var installations = await _installations.GetAllAsync();
        AvailableInstallations.Clear();
        foreach (var installation in installations)
        {
            AvailableInstallations.Add(installation);
        }

        _profile = profile ?? new LaunchProfile
        {
            Id = Guid.Empty,
            Options = new ServerLaunchOptions(),
            CreatedUtc = default,
        };

        Name = profile?.Name ?? "新配置";
        SelectedModel = profile?.ModelId is { } modelId ? AvailableModels.FirstOrDefault(m => m.Id == modelId) : null;
        SelectedInstallation = profile?.InstallationId is { } installationId
            ? AvailableInstallations.FirstOrDefault(i => i.Id == installationId)
            : AvailableInstallations.FirstOrDefault(i => i.IsDefault);

        var options = profile?.Options ?? new ServerLaunchOptions();
        Host = options.Host;
        PortText = options.Port.ToString(CultureInfo.InvariantCulture);
        ContextSizeText = options.ContextSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ThreadsText = options.Threads?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ThreadsBatchText = options.ThreadsBatch?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        BatchSizeText = options.BatchSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        UBatchSizeText = options.UBatchSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        GpuLayersText = options.GpuLayers ?? string.Empty;
        DevicesText = string.Join(", ", options.Devices);
        SplitMode = options.SplitMode is { Length: > 0 } split
            ? (SplitModeOptions.Contains(split, StringComparer.OrdinalIgnoreCase) ? split : SplitModeOptions[0])
            : SplitModeOptions[0];
        TensorSplitText = options.TensorSplit ?? string.Empty;
        MainGpuText = options.MainGpu?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        CacheTypeK = options.CacheTypeK ?? CacheTypeOptions[0];
        CacheTypeV = options.CacheTypeV ?? CacheTypeOptions[0];
        FlashAttention = options.FlashAttention switch
        {
            FlashAttentionMode.On => FlashAttentionOptions[1],
            FlashAttentionMode.Off => FlashAttentionOptions[2],
            FlashAttentionMode.Auto => FlashAttentionOptions[3],
            _ => FlashAttentionOptions[0],
        };
        Alias = options.Alias ?? string.Empty;
        ParallelText = options.ParallelSlots?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ContinuousBatching = options.ContinuousBatching switch
        {
            true => TriStateOptions[1],
            false => TriStateOptions[2],
            _ => TriStateOptions[0],
        };
        VerboseLogging = options.VerboseLogging;
        NoMemoryMap = options.NoMemoryMap;
        ApiKeyEnabled = options.ApiKeyEnabled;
        ApiKeySecretName = options.ApiKeySecretName ?? SecretNames.DefaultApiKey;
        StartupTimeoutText = options.StartupTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        StopGraceText = options.StopGraceSeconds.ToString(CultureInfo.InvariantCulture);
        ExtraArguments = profile?.ExtraArguments ?? string.Empty;
        AllowOverride = profile?.AllowExtraArgsOverrideManaged ?? false;
        Notes = profile?.Notes ?? string.Empty;

        EnvironmentVariables.Clear();
        if (profile is not null)
        {
            foreach (var (key, value) in profile.EnvironmentVariables)
            {
                EnvironmentVariables.Add(new EnvironmentVariableRow { Name = key, Value = value });
            }
        }

        await UpdateCapabilitiesAsync();
    }

    partial void OnSelectedInstallationChanged(LlamaCppInstallation? value)
        => _ = UpdateCapabilitiesAsync();

    [RelayCommand]
    private async Task UpdateCapabilitiesAsync()
    {
        if (SelectedInstallation is not { } installation)
        {
            CapabilitySummary = "尚未选择 llama.cpp 程序：无法依据能力快照校验参数。";
            return;
        }

        ServerCapabilities? capabilities = null;
        if (installation.CapabilitiesJson is { Length: > 0 } json)
        {
            try
            {
                capabilities = JsonSerializer.Deserialize<ServerCapabilities>(json, JsonDefaults.Storage);
            }
            catch (JsonException)
            {
                capabilities = null;
            }
        }

        if (capabilities is null && File.Exists(installation.ExecutablePath))
        {
            capabilities = await _probeService.ProbeAsync(installation.ExecutablePath);
        }

        if (capabilities is null)
        {
            CapabilitySummary = "无法获取能力快照：参数将不受版本能力约束（不推荐）。";
            return;
        }

        SupportsCtxSize = capabilities.Supports("ctx-size");
        SupportsThreads = capabilities.Supports("threads");
        SupportsBatch = capabilities.Supports("batch-size");
        SupportsGpuLayers = capabilities.Supports("n-gpu-layers");
        SupportsDevices = capabilities.Supports("device");
        SupportsKvCache = capabilities.Supports("cache-type-k");
        SupportsFlashAttention = capabilities.Supports("flash-attn");
        SupportsParallel = capabilities.Supports("parallel");
        SupportsContBatching = capabilities.Supports("cont-batching") || capabilities.Supports("no-cont-batching");
        SupportsApiKey = capabilities.Supports("api-key");
        SupportsVerbose = capabilities.Supports("verbose");
        SupportsNoMmap = capabilities.Supports("no-mmap");
        HasGpuDevices = capabilities.Devices.Any(d => d.Backend != BackendKind.Cpu);
        var gpuOption = capabilities.FindOption("n-gpu-layers");
        SupportsGpuAutoLayers = gpuOption is not null
            && ((gpuOption.ValueHint ?? string.Empty) + " " + (gpuOption.Description ?? string.Empty))
                .Contains("auto", StringComparison.OrdinalIgnoreCase);
        var flash = capabilities.FindOption("flash-attn");
        FlashAttentionValueForm = flash?.ValueKind != OptionValueKind.None;

        var backendText = HasGpuDevices
            ? "探测到推理设备：" + string.Join("、", capabilities.Devices.Select(d => $"{Formatting.BackendText(d.Backend)} {d.Name}"))
            : "未探测到 GPU 推理设备（将按 CPU 参数运行）";
        CapabilitySummary =
            $"{installation.VersionLabel}：支持参数 {capabilities.Options.Count} 个；{backendText}。" +
            (capabilities.Outcome == ProbeOutcome.PartialFailure ? "（部分探测失败，部分参数可能校验不完整）" : string.Empty);
    }

    [RelayCommand]
    private void ApplyCpuPreset()
    {
        var changed = new List<string>();
        GpuLayersText = "0";
        changed.Add("GPU 层数=0（纯 CPU）");
        ThreadsText = Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture);
        changed.Add($"CPU 线程={Environment.ProcessorCount}");
        ContextSizeText = string.IsNullOrWhiteSpace(ContextSizeText) ? "4096" : ContextSizeText;
        changed.Add("上下文=" + ContextSizeText);
        FlashAttention = FlashAttentionOptions[0];
        changed.Add("Flash Attention=默认");
        StatusText = "已应用“CPU 兼容”预设：" + string.Join("；", changed) + "。请检查后保存。";
    }

    [RelayCommand]
    private void ApplyGpuAutoPreset()
    {
        if (!SupportsGpuLayers || !HasGpuDevices)
        {
            StatusText = "当前程序/设备不支持“GPU 自动”预设（需探测到 GPU 推理设备且版本支持 --n-gpu-layers）。";
            return;
        }

        if (SupportsGpuAutoLayers)
        {
            GpuLayersText = "auto";
            StatusText = "已应用“GPU 自动”预设：GPU 层数=auto（交由程序自动选择）。";
        }
        else
        {
            GpuLayersText = "999";
            StatusText = "该版本不支持 auto 取值，已使用 GPU 层数=999（尽量全部卸载到 GPU），请检查后保存。";
        }
    }

    [RelayCommand]
    private void AddEnvironmentVariable()
        => EnvironmentVariables.Add(new EnvironmentVariableRow());

    [RelayCommand]
    private void RemoveEnvironmentVariable(EnvironmentVariableRow? row)
    {
        if (row is not null)
        {
            EnvironmentVariables.Remove(row);
        }
    }

    [RelayCommand]
    private async Task ValidateAndPreviewAsync()
    {
        Issues.Clear();
        var profile = BuildProfile(out var parseIssues);
        foreach (var issue in parseIssues)
        {
            Issues.Add(ToRow(issue));
        }

        var report = await _profiles.ValidateAsync(profile);
        foreach (var issue in report.Issues)
        {
            Issues.Add(ToRow(issue));
        }

        CommandPreview = report.MaskedCommandLinePreview ?? "（无法生成预览：请先选择模型与 llama.cpp 程序）";
        IsValidated = true;
        StatusText = Issues.Any(i => i.IsBlocking)
            ? $"校验不通过：{Issues.Count(i => i.IsBlocking)} 个错误。"
            : Issues.Count == 0
                ? "校验通过，可以保存。"
                : $"校验通过（{Issues.Count} 条提示）。";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var profile = BuildProfile(out var parseIssues);
        if (parseIssues.Any(i => i.IsBlocking))
        {
            await ValidateAndPreviewAsync();
            return;
        }

        var report = await _profiles.ValidateAsync(profile);
        if (report.Issues.Any(i => i.IsBlocking))
        {
            await ValidateAndPreviewAsync();
            StatusText = "存在阻止保存的错误，请先修正（详见问题列表）。";
            return;
        }

        try
        {
            if (IsNew)
            {
                var created = await _profiles.CreateAsync(profile.Name, profile.ModelId, profile.InstallationId);
                profile.Id = created.Id;
                profile.CreatedUtc = created.CreatedUtc;
            }

            await _profiles.SaveAsync(profile);
            _notifications.Notify($"配置“{profile.Name}”已保存。");
            Saved?.Invoke();
            CloseRequested?.Invoke();
        }
        catch (LaunchProfileException ex)
        {
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            StatusText = "保存失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    private ValidationIssueRow ToRow(ValidationIssue issue) => new(
        issue.Severity switch
        {
            ValidationSeverity.Error => "错误",
            ValidationSeverity.Warning => "警告",
            _ => "提示",
        },
        issue.Message);

    private LaunchProfile BuildProfile(out List<ValidationIssue> parseIssues)
    {
        var issues = new List<ValidationIssue>();

        int? ParseInt(string text, string fieldName, bool allowEmpty = true)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            issues.Add(new ValidationIssue(ValidationSeverity.Error, fieldName, $"{fieldName} 必须是整数。"));
            return null;
        }

        string? CleanChoice(string value, string emptyMarker)
            => string.IsNullOrWhiteSpace(value) || value == emptyMarker ? null : value.Trim();

        var options = new ServerLaunchOptions
        {
            ModelPath = SelectedModel?.FilePath,
            Host = Host.Trim(),
            Port = ParseInt(PortText, "端口") ?? 8080,
            ContextSize = ParseInt(ContextSizeText, "上下文长度"),
            Threads = ParseInt(ThreadsText, "CPU 线程"),
            ThreadsBatch = ParseInt(ThreadsBatchText, "批处理线程"),
            BatchSize = ParseInt(BatchSizeText, "批大小"),
            UBatchSize = ParseInt(UBatchSizeText, "物理批大小"),
            GpuLayers = string.IsNullOrWhiteSpace(GpuLayersText) ? null : GpuLayersText.Trim(),
            Devices = DevicesText
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToList(),
            SplitMode = CleanChoice(SplitMode, SplitModeOptions[0]),
            TensorSplit = string.IsNullOrWhiteSpace(TensorSplitText) ? null : TensorSplitText.Trim(),
            MainGpu = ParseInt(MainGpuText, "主 GPU"),
            CacheTypeK = CleanChoice(CacheTypeK, CacheTypeOptions[0]),
            CacheTypeV = CleanChoice(CacheTypeV, CacheTypeOptions[0]),
            FlashAttention = FlashAttention switch
            {
                _ when FlashAttention == FlashAttentionOptions[1] => FlashAttentionMode.On,
                _ when FlashAttention == FlashAttentionOptions[2] => FlashAttentionMode.Off,
                _ when FlashAttention == FlashAttentionOptions[3] => FlashAttentionMode.Auto,
                _ => null,
            },
            Alias = string.IsNullOrWhiteSpace(Alias) ? null : Alias.Trim(),
            ParallelSlots = ParseInt(ParallelText, "并行槽位"),
            ContinuousBatching = ContinuousBatching switch
            {
                _ when ContinuousBatching == TriStateOptions[1] => true,
                _ when ContinuousBatching == TriStateOptions[2] => false,
                _ => null,
            },
            VerboseLogging = VerboseLogging,
            NoMemoryMap = NoMemoryMap,
            ApiKeyEnabled = ApiKeyEnabled,
            ApiKeySecretName = ApiKeyEnabled ? ApiKeySecretName.Trim() : null,
            StartupTimeoutSeconds = ParseInt(StartupTimeoutText, "启动超时") ?? 600,
            StopGraceSeconds = ParseInt(StopGraceText, "停止宽限期") ?? 5,
        };

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in EnvironmentVariables)
        {
            if (string.IsNullOrWhiteSpace(row.Name))
            {
                continue;
            }

            environment[row.Name.Trim()] = row.Value;
        }

        var profile = _profile.Id == Guid.Empty ? new LaunchProfile() : _profile;
        profile.Name = Name.Trim();
        profile.ModelId = SelectedModel?.Id;
        profile.ModelSnapshot = SelectedModel is null
            ? null
            : new ModelReferenceSnapshot
            {
                ModelId = SelectedModel.Id,
                Name = SelectedModel.DisplayName,
                FilePath = SelectedModel.FilePath,
            };
        profile.InstallationId = SelectedInstallation?.Id;
        profile.InstallationSnapshot = SelectedInstallation is null
            ? null
            : new InstallationReferenceSnapshot
            {
                InstallationId = SelectedInstallation.Id,
                VersionLabel = SelectedInstallation.VersionLabel,
                ExecutablePath = SelectedInstallation.ExecutablePath,
            };
        profile.Options = options;
        profile.ExtraArguments = string.IsNullOrWhiteSpace(ExtraArguments) ? null : ExtraArguments;
        profile.AllowExtraArgsOverrideManaged = AllowOverride;
        profile.EnvironmentVariables = environment;
        profile.Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes;
        parseIssues = issues;
        return profile;
    }
}
