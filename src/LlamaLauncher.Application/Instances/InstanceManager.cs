using System.Collections.Concurrent;
using System.Text.Json;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.CommandLine;
using LlamaLauncher.Application.Services;
using LlamaLauncher.Domain;
using LlamaLauncher.Domain.StateMachine;
using LlamaLauncher.Domain.Validation;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Application.Instances;

/// <summary>实例编排：启动前校验 → 命令行构建 → 进程启动 → 日志采集 → 健康轮询 → 停止/重启/接管。</summary>
public sealed class InstanceManager : IInstanceManager, IAsyncDisposable
{
    private static readonly TimeSpan ProcessExitWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LoadingPollInterval = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SlowLoadingPollInterval = TimeSpan.FromSeconds(5);

    private readonly IInstanceRepository _instances;
    private readonly IProfileRepository _profiles;
    private readonly IModelRepository _models;
    private readonly IInstallationRepository _installations;
    private readonly ILlamaBinaryProbeService _probeService;
    private readonly IServerProcessRunner _processRunner;
    private readonly IProcessInspector _processInspector;
    private readonly IPortProbeService _portProbe;
    private readonly IServerHealthService _health;
    private readonly ISecretStore _secretStore;
    private readonly IInstanceLogStore _logStore;
    private readonly IStartupHistoryRepository _history;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InstanceManager> _logger;

    private readonly ConcurrentDictionary<Guid, ManagedInstance> _cache = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly CancellationTokenSource _shutdownCts = new();

    public InstanceManager(
        IInstanceRepository instances,
        IProfileRepository profiles,
        IModelRepository models,
        IInstallationRepository installations,
        ILlamaBinaryProbeService probeService,
        IServerProcessRunner processRunner,
        IProcessInspector processInspector,
        IPortProbeService portProbe,
        IServerHealthService health,
        ISecretStore secretStore,
        IInstanceLogStore logStore,
        IStartupHistoryRepository history,
        TimeProvider timeProvider,
        ILogger<InstanceManager> logger)
    {
        _instances = instances;
        _profiles = profiles;
        _models = models;
        _installations = installations;
        _probeService = probeService;
        _processRunner = processRunner;
        _processInspector = processInspector;
        _portProbe = portProbe;
        _health = health;
        _secretStore = secretStore;
        _logStore = logStore;
        _history = history;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public event EventHandler<Guid>? InstanceChanged;

    public IReadOnlyList<ManagedInstance> GetInstances() =>
        _cache.Values.OrderBy(i => i.Record.CreatedUtc).ToList();

    public ManagedInstance? Get(Guid instanceId) =>
        _cache.TryGetValue(instanceId, out var instance) ? instance : null;

    public async Task<RecoveryReport> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var markedStale = 0;
        var adopted = 0;

        var stored = await _instances.GetAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in stored)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instance = new ManagedInstance(record);
            _cache[record.Id] = instance;

            if (!InstanceStateMachine.IsActive(record.State))
            {
                continue;
            }

            // 上次运行遗留的活跃状态：先做 PID + 启动时间 + 路径三重验证。
            var verified = record is
            {
                ProcessId: { } pid,
                ExecutablePathSnapshot: { } exe,
                ProcessStartTimeUtc: { } startTime,
            } && _processInspector.Matches(pid, exe, startTime, TimeSpan.FromSeconds(2), out _);

            if (verified)
            {
                var session = _processRunner.Adopt(record.ProcessId!.Value);
                instance.Session = session;
                instance.StopRequested = false;
                instance.LifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
                var generation = record.Generation;
                session.Exited += exitCode => HandleUnexpectedExit(instance, generation, exitCode);
                SetState(instance, InstanceState.LoadingModel, "已接管上次运行中的进程，等待健康检查确认状态。");
                _ = RunHealthLoopAsync(instance, null);
                adopted++;
                messages.Add($"已接管进程 PID {record.ProcessId}（{record.Name}）。");
            }
            else
            {
                record.State = InstanceState.ExitedUnexpectedly;
                record.LastError = "上次运行未正常结束，且进程已不存在（PID 重用校验未通过或进程已退出）。";
                record.StoppedUtc ??= _timeProvider.GetUtcNow();
                record.UpdatedUtc = _timeProvider.GetUtcNow();
                await _instances.UpsertAsync(record, cancellationToken).ConfigureAwait(false);

                // 同步关闭对应的启动历史记录，避免历史页把上次的遗留活跃记录显示为“运行中”。
                var recentHistory = await _history
                    .QueryAsync(new HistoryQuery { InstanceId = record.Id, Limit = 5 }, cancellationToken)
                    .ConfigureAwait(false);
                var openEntry = recentHistory.FirstOrDefault(h => h.EndedUtc is null);
                if (openEntry is not null)
                {
                    openEntry.EndedUtc = _timeProvider.GetUtcNow();
                    openEntry.FinalState = InstanceState.ExitedUnexpectedly;
                    openEntry.FailureStage ??= "Recovery";
                    openEntry.ErrorSummary ??= record.LastError;
                    await _history.UpdateAsync(openEntry, cancellationToken).ConfigureAwait(false);
                }

                markedStale++;
                messages.Add($"实例“{record.Name}”上次运行未正常结束，已标记为异常退出。");
            }
        }

        return new RecoveryReport(markedStale, adopted, messages);
    }

    public async Task<ManagedInstance> CreateAsync(
        string name,
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("实例名称不能为空。", nameof(name));
        }

        var profile = await _profiles.GetByIdAsync(profileId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("配置档案不存在。");

        var now = _timeProvider.GetUtcNow();
        var record = new ServerInstance
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            ProfileId = profile.Id,
            ProfileSnapshotJson = JsonSerializer.Serialize(profile, JsonDefaults.Storage),
            ModelId = profile.ModelId,
            InstallationId = profile.InstallationId,
            State = InstanceState.Stopped,
            Host = profile.Options.Host,
            Port = profile.Options.Port,
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        await _instances.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        var instance = new ManagedInstance(record) { Profile = profile };
        _cache[record.Id] = instance;
        RaiseChanged(record.Id);
        return instance;
    }

    public async Task<StartResult> StartAsync(
        Guid instanceId,
        bool waitForReady = false,
        bool lanAccessConfirmed = false,
        CancellationToken cancellationToken = default)
    {
        var semaphore = _locks.GetOrAdd(instanceId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var instance = Get(instanceId) ?? throw new InvalidOperationException("实例不存在。");
            if (!InstanceStateMachine.IsTerminal(instance.State))
            {
                return StartResult.Fail(instance.State, $"实例当前状态为 {instance.State}，无法重复启动。");
            }

            return await StartCoreAsync(instance, waitForReady, lanAccessConfirmed, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<StartResult> StartCoreAsync(
        ManagedInstance instance,
        bool waitForReady,
        bool lanAccessConfirmed,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        instance.StopRequested = false;
        instance.FailureStage = null;
        instance.Health = HealthStatusKind.Unknown;
        instance.HealthDetail = null;
        instance.Record.Generation++;
        var generation = instance.Record.Generation;

        InstanceStateMachine.EnsureTransition(instance.State, InstanceState.Starting);
        instance.Record.State = InstanceState.Starting;
        instance.Record.UpdatedUtc = now;
        await _instances.UpsertAsync(instance.Record, cancellationToken).ConfigureAwait(false);
        RaiseChanged(instance.Id);

        // 载入配置
        var profile = instance.Profile
            ?? (instance.Record.ProfileId is { } profileId
                ? await _profiles.GetByIdAsync(profileId, cancellationToken).ConfigureAwait(false)
                : null);
        if (profile is null)
        {
            return await FailAsync(instance, "ProfileValidation", "实例引用的启动配置不存在。", null, cancellationToken)
                .ConfigureAwait(false);
        }

        instance.Profile = profile;
        instance.StartupTimeoutSeconds = profile.Options.StartupTimeoutSeconds;
        instance.StopGraceSeconds = profile.Options.StopGraceSeconds;
        instance.StartRequestedUtc = now;

        var history = new StartupHistoryEntry
        {
            Id = Guid.NewGuid(),
            InstanceId = instance.Id,
            InstanceNameSnapshot = instance.Name,
            ProfileId = profile.Id,
            ModelSnapshotJson = profile.ModelSnapshot is null
                ? null
                : JsonSerializer.Serialize(profile.ModelSnapshot, JsonDefaults.Storage),
            InstallationSnapshotJson = profile.InstallationSnapshot is null
                ? null
                : JsonSerializer.Serialize(profile.InstallationSnapshot, JsonDefaults.Storage),
            StartedUtc = now,
            FinalState = InstanceState.Starting,
            LogFilePath = _logStore.GetLogFilePath(instance.Id, instance.Name),
        };
        await _history.AddAsync(history, cancellationToken).ConfigureAwait(false);

        // 解析模型与可执行文件
        var options = CloneOptions(profile.Options);
        var model = profile.ModelId is { } modelId
            ? await _models.GetByIdAsync(modelId, cancellationToken).ConfigureAwait(false)
            : null;
        var modelPath = model?.FilePath ?? options.ModelPath;
        options.ModelPath = modelPath;

        var selectedInstallation = profile.InstallationId is { } installationId
            ? await _installations.GetByIdAsync(installationId, cancellationToken).ConfigureAwait(false)
            : null;
        var installations = await _installations.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var resolution = InstallationResolver.Resolve(
            selectedInstallation,
            profile.InstallationSnapshot?.ExecutablePath,
            installations);
        var installation = resolution.Installation;
        var executablePath = resolution.ExecutablePath;

        // 数据目录可能整体拷贝自另一台电脑：原绑定的绝对路径失效时，自动回退到本机可用版本并修复配置，
        // 避免用户必须手工重建启动配置。
        if (resolution.UsedFallback && !string.IsNullOrWhiteSpace(executablePath))
        {
            if (installation is not null && profile.InstallationId != installation.Id)
            {
                profile.InstallationId = installation.Id;
                profile.InstallationSnapshot = new InstallationReferenceSnapshot
                {
                    InstallationId = installation.Id,
                    VersionLabel = installation.VersionLabel,
                    ExecutablePath = installation.ExecutablePath,
                };
                await _profiles.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);
                instance.Record.InstallationId = installation.Id;
                instance.Profile = profile;
            }

            var fallbackMessage =
                $"原 llama.cpp 程序路径不可用，已自动改用 {installation?.VersionLabel ?? "本机可用版本"}：" +
                executablePath;
            _logStore.Append(instance.Id, instance.Name, LogStreamKind.Application, fallbackMessage);
            _logger.LogWarning("实例 {Name} {Message}", instance.Name, fallbackMessage);
        }

        var modelExists = !string.IsNullOrWhiteSpace(modelPath) && File.Exists(modelPath);
        var executableExists = !string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath);

        // 值域与安全校验（端口预检查）
        var portCheck = options.Port is >= 1 and <= 65535
            ? await _portProbe.CheckAsync(options.Host, options.Port, cancellationToken).ConfigureAwait(false)
            : PortCheckResult.Available();

        string? apiKey = null;
        var apiKeyName = options.ApiKeySecretName ?? SecretNames.DefaultApiKey;
        if (options.ApiKeyEnabled)
        {
            apiKey = await _secretStore.GetAsync(apiKeyName, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(apiKey))
            {
                _logStore.AddKnownSecret(apiKey!);
            }
        }

        var validationContext = new LaunchValidationContext
        {
            ApiKeyConfigured = apiKey is { Length: > 0 },
            LanAccessConfirmed = lanAccessConfirmed,
            PortInUse = !portCheck.IsAvailable,
            ModelFilePath = modelExists ? modelPath : null,
            ExecutablePath = executableExists ? executablePath : null,
            ModelFileSizeBytes = model?.FileSizeBytes,
        };

        var issues = LaunchOptionsValidator.ValidateValues(options, validationContext).ToList();
        if (options.ApiKeyEnabled && string.IsNullOrEmpty(apiKey))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "ApiKey",
                $"已启用 API Key，但密钥“{apiKeyName}”未在密钥存储中配置。"));
        }

        if (issues.Any(i => i.IsBlocking))
        {
            return await FailAsync(
                instance,
                "Validation",
                "启动前校验未通过：" + issues.First(i => i.IsBlocking).Message,
                issues,
                cancellationToken,
                history).ConfigureAwait(false);
        }

        // 能力快照（必须成功探测，禁止未知能力启动）
        var capabilities = await _probeService.ProbeAsync(executablePath!, forceRefresh: false, cancellationToken)
            .ConfigureAwait(false);
        instance.Capabilities = capabilities;
        if (capabilities.Outcome == ProbeOutcome.Failed)
        {
            var detail = capabilities.Warnings.FirstOrDefault() ?? "能力探测失败。";
            return await FailAsync(
                instance,
                "Probe",
                $"llama.cpp 程序能力探测失败：{detail}",
                capabilities.Warnings.Select(w => new ValidationIssue(ValidationSeverity.Error, "Probe", w)).ToList(),
                cancellationToken,
                history).ConfigureAwait(false);
        }

        // 命令行构建
        var build = CommandLineBuilder.Build(executablePath!, new CommandLineBuildRequest
        {
            Options = options,
            Capabilities = capabilities,
            ResolvedApiKey = apiKey,
            AllowExtraArgsOverrideManaged = profile.AllowExtraArgsOverrideManaged,
            ExtraArguments = profile.ExtraArguments,
            EnvironmentVariables = profile.EnvironmentVariables,
        });
        if (build.HasErrors)
        {
            return await FailAsync(
                instance,
                "CommandLine",
                "参数校验未通过：\n" + string.Join("\n", build.Issues.Where(i => i.IsBlocking).Select(i => "· " + i.Message)),
                build.Issues,
                cancellationToken,
                history).ConfigureAwait(false);
        }

        foreach (var warning in build.Issues.Where(i => !i.IsBlocking))
        {
            _logStore.Append(instance.Id, instance.Name, LogStreamKind.Application, "警告：" + warning.Message);
        }

        instance.MaskedCommandLine = build.MaskedPreview;
        instance.Record.Host = options.Host;
        instance.Record.Port = options.Port;
        instance.Record.ModelId = model?.Id ?? profile.ModelId;
        instance.Record.InstallationId = installation?.Id ?? profile.InstallationId;
        instance.Record.ProfileSnapshotJson = JsonSerializer.Serialize(profile, JsonDefaults.Storage);
        history.CommandLineMasked = build.MaskedPreview;
        history.ModelSnapshotJson = profile.ModelSnapshot is null
            ? history.ModelSnapshotJson
            : JsonSerializer.Serialize(profile.ModelSnapshot, JsonDefaults.Storage);
        history.InstallationSnapshotJson = profile.InstallationSnapshot is null
            ? history.InstallationSnapshotJson
            : JsonSerializer.Serialize(profile.InstallationSnapshot, JsonDefaults.Storage);
        await _history.UpdateAsync(history, cancellationToken).ConfigureAwait(false);

        _logStore.Append(
            instance.Id,
            instance.Name,
            LogStreamKind.Application,
            $"启动命令（脱敏）：{build.MaskedPreview}");

        // 进程启动
        IServerProcessSession session;
        try
        {
            session = _processRunner.Start(new ServerProcessStartSpec
            {
                ExecutablePath = executablePath!,
                Arguments = build.Tokens,
                WorkingDirectory = Path.GetDirectoryName(executablePath),
                Environment = profile.EnvironmentVariables.Count == 0 ? null : profile.EnvironmentVariables,
            });
        }
        catch (ProcessStartException ex)
        {
            return await FailAsync(instance, "ProcessStart", $"进程启动失败：{ex.Message}", null, cancellationToken, history)
                .ConfigureAwait(false);
        }

        instance.Session = session;
        instance.LifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        var startupTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        instance.StartupCompletion = startupTcs;

        session.StandardOutputLine += line =>
            _logStore.Append(instance.Id, instance.Name, LogStreamKind.StandardOutput, line);
        session.StandardErrorLine += line =>
            _logStore.Append(instance.Id, instance.Name, LogStreamKind.StandardError, line);
        session.Exited += exitCode => OnProcessExited(instance, generation, exitCode, startupTcs);

        instance.Record.ProcessId = session.ProcessId;
        instance.Record.ProcessStartTimeUtc = session.StartTimeUtc;
        instance.Record.ExecutablePathSnapshot = executablePath;
        instance.Record.StartedUtc = _timeProvider.GetUtcNow();
        instance.Record.ExitCode = null;
        instance.Record.LastError = null;
        instance.Record.LogFilePath = _logStore.GetLogFilePath(instance.Id, instance.Name);
        instance.ReadyUtc = null;
        history.ProcessId = session.ProcessId;
        await _history.UpdateAsync(history, cancellationToken).ConfigureAwait(false);

        InstanceStateMachine.EnsureTransition(instance.State, InstanceState.LoadingModel);
        instance.Record.State = InstanceState.LoadingModel;
        instance.Record.UpdatedUtc = _timeProvider.GetUtcNow();
        await _instances.UpsertAsync(instance.Record, cancellationToken).ConfigureAwait(false);
        RaiseChanged(instance.Id);
        _logger.LogInformation(
            "实例 {Name} 进程已启动：PID {Pid}，端口 {Port}，等待健康检查。",
            instance.Name, session.ProcessId, options.Port);

        _ = RunHealthLoopAsync(instance, startupTcs);

        if (waitForReady)
        {
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(Math.Min(instance.StartupTimeoutSeconds + 30, 3600)));
            var completed = await Task.WhenAny(startupTcs.Task, timeoutTask).ConfigureAwait(false);
            if (completed == timeoutTask)
            {
                return StartResult.Ok(instance.State, "已启动，健康检查仍在进行（等待超时，请查看实例状态）。");
            }

            _ = await startupTcs.Task.ConfigureAwait(false);
            return instance.State == InstanceState.Ready
                ? StartResult.Ok(instance.State, "服务已就绪。")
                : StartResult.Fail(instance.State, instance.Record.LastError ?? "启动未完成。");
        }

        return StartResult.Ok(instance.State, "进程已启动，正在等待服务就绪。");
    }

    public async Task StopAsync(Guid instanceId, bool force = false, CancellationToken cancellationToken = default)
    {
        var semaphore = _locks.GetOrAdd(instanceId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var instance = Get(instanceId) ?? throw new InvalidOperationException("实例不存在。");
            var session = instance.Session;
            if (session is null)
            {
                if (!InstanceStateMachine.IsTerminal(instance.State))
                {
                    SetState(instance, InstanceState.Stopping, "实例没有正在运行的进程。");
                    SetState(instance, InstanceState.Stopped, null);
                }

                instance.Record.UpdatedUtc = _timeProvider.GetUtcNow();
                await _instances.UpsertAsync(instance.Record, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (session.HasExited)
            {
                await session.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await WaitForTerminalStateAsync(instance).ConfigureAwait(false);
                await AwaitExitHandlingAsync(instance).ConfigureAwait(false);
                return;
            }

            instance.StopRequested = true;
            SetState(instance, InstanceState.Stopping, "正在停止实例…");
            await _instances.UpsertAsync(instance.Record, cancellationToken).ConfigureAwait(false);
            instance.LifetimeCts?.Cancel();

            var grace = force ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(0, instance.StopGraceSeconds));
            if (grace > TimeSpan.Zero)
            {
                var exitTask = session.WaitForExitAsync(CancellationToken.None);
                var delayTask = Task.Delay(grace);
                if (await Task.WhenAny(exitTask, delayTask).ConfigureAwait(false) == exitTask)
                {
                    await WaitForTerminalStateAsync(instance).ConfigureAwait(false);
                    await AwaitExitHandlingAsync(instance).ConfigureAwait(false);
                    return; // 进程已自行退出，Exited 事件负责最终状态
                }
            }

            try
            {
                await session.KillAsync(entireProcessTree: true).ConfigureAwait(false);
                try
                {
                    await session.WaitForExitAsync(CancellationToken.None).WaitAsync(ProcessExitWait)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logStore.Append(
                        instance.Id,
                        instance.Name,
                        LogStreamKind.Application,
                        "警告：终止进程后未在预期时间内收到退出确认。");
                }
            }
            catch (Exception ex)
            {
                instance.Record.LastError = $"终止进程失败：{ex.Message}";
                _logStore.Append(instance.Id, instance.Name, LogStreamKind.Application, "错误：" + instance.Record.LastError);
                await _instances.UpsertAsync(instance.Record, cancellationToken).ConfigureAwait(false);
                RaiseChanged(instance.Id);
            }

            await WaitForTerminalStateAsync(instance).ConfigureAwait(false);
            await AwaitExitHandlingAsync(instance).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async Task AwaitExitHandlingAsync(ManagedInstance instance, int timeoutSeconds = 15)
    {
        var completion = instance.ExitCompletion;
        if (completion is not null)
        {
            try
            {
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds)).ConfigureAwait(false);
                return;
            }
            catch (Exception)
            {
                return;
            }
        }

        var task = instance.ExitHandlingTask;
        if (task is null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 收尾失败已记录日志，不影响停止流程返回
        }
    }

    private static async Task WaitForTerminalStateAsync(ManagedInstance instance, int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!InstanceStateMachine.IsTerminal(instance.State) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    public async Task<StartResult> RestartAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = Get(instanceId) ?? throw new InvalidOperationException("实例不存在。");
        await StopAsync(instanceId, force: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        var result = await StartAsync(instanceId, waitForReady: false, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            result = result with
            {
                Message = (result.Message ?? "重启失败。") + " 可尝试重新启动原配置或检查日志。",
            };
        }

        return result;
    }

    public async Task RemoveAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = Get(instanceId);
        if (instance is null)
        {
            return;
        }
        if (!InstanceStateMachine.IsTerminal(instance.State))
        {
            throw new InvalidOperationException("实例仍在运行，请先停止后再移除。");
        }

        var session = instance.Session;
        instance.Session = null;
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        instance.LifetimeCts?.Dispose();
        await _instances.DeleteAsync(instanceId, cancellationToken).ConfigureAwait(false);
        _cache.TryRemove(instanceId, out _);
        RaiseChanged(instanceId);
    }

    public async Task StopAllAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        foreach (var instance in GetInstances())
        {
            if (!instance.IsActive)
            {
                continue;
            }

            try
            {
                await StopAsync(instance.Id, force, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "停止实例 {Name} 失败", instance.Name);
            }
        }
    }

    public async Task<string?> GetInstanceApiKeyAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = Get(instanceId);
        if (instance?.Profile is not { } profile || !profile.Options.ApiKeyEnabled)
        {
            return null;
        }

        var name = profile.Options.ApiKeySecretName ?? SecretNames.DefaultApiKey;
        return await _secretStore.GetAsync(name, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdownCts.Cancel();
        foreach (var instance in GetInstances())
        {
            var session = instance.Session;
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            instance.LifetimeCts?.Dispose();
        }

        _shutdownCts.Dispose();
    }

    private async Task RunHealthLoopAsync(ManagedInstance instance, TaskCompletionSource<bool>? startupTcs)
    {
        var token = instance.LifetimeCts?.Token ?? _shutdownCts.Token;
        var startTime = instance.StartRequestedUtc ?? _timeProvider.GetUtcNow();

        try
        {
            while (!token.IsCancellationRequested)
            {
                var session = instance.Session;
                if (session is null || session.HasExited)
                {
                    return;
                }

                var elapsed = _timeProvider.GetUtcNow() - startTime;
                var overBudget = elapsed > TimeSpan.FromSeconds(instance.StartupTimeoutSeconds);
                string? apiKey = null;
                if (instance.Profile?.Options.ApiKeyEnabled == true)
                {
                    var name = instance.Profile.Options.ApiKeySecretName ?? SecretNames.DefaultApiKey;
                    apiKey = await _secretStore.GetAsync(name, token).ConfigureAwait(false);
                }

                var result = await _health.CheckHealthAsync(instance.LocalBaseUrl, apiKey, token).ConfigureAwait(false);
                instance.Health = result.Status;
                instance.HealthDetail = result.Detail;

                switch (result.Status)
                {
                    case HealthStatusKind.Ready:
                        if (instance.State == InstanceState.LoadingModel || instance.State == InstanceState.Starting)
                        {
                            InstanceStateMachine.EnsureTransition(instance.State, InstanceState.Ready);
                            instance.Record.State = InstanceState.Ready;
                            instance.ReadyUtc = _timeProvider.GetUtcNow();
                            instance.Record.UpdatedUtc = _timeProvider.GetUtcNow();
                            await _instances.UpsertAsync(instance.Record, CancellationToken.None).ConfigureAwait(false);
                            _logStore.Append(instance.Id, instance.Name, LogStreamKind.Application, "服务已就绪（/health = ok）。");
                            RaiseChanged(instance.Id);
                            startupTcs?.TrySetResult(true);
                        }

                        await Task.Delay(ReadyPollInterval, token).ConfigureAwait(false);
                        break;

                    case HealthStatusKind.Error:
                        await FailAsync(
                            instance,
                            "Health",
                            $"服务报告错误：{result.Detail ?? "未知错误"}",
                            null,
                            CancellationToken.None).ConfigureAwait(false);
                        startupTcs?.TrySetResult(false);
                        // 服务报告错误后主动终止进程（避免残留无法使用的进程）
                        instance.StopRequested = true;
                        var sessionToKill = instance.Session;
                        if (sessionToKill is not null && !sessionToKill.HasExited)
                        {
                            await sessionToKill.KillAsync(true).ConfigureAwait(false);
                        }

                        return;

                    case HealthStatusKind.Loading:
                        await Task.Delay(overBudget ? SlowLoadingPollInterval : LoadingPollInterval, token)
                            .ConfigureAwait(false);
                        break;

                    default:
                        await Task.Delay(overBudget ? SlowLoadingPollInterval : LoadingPollInterval, token)
                            .ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止路径
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "实例 {Name} 健康轮询异常", instance.Name);
            _logStore.Append(instance.Id, instance.Name, LogStreamKind.Application, "健康轮询异常：" + ex.Message);
        }
    }

    private void OnProcessExited(
        ManagedInstance instance,
        long generation,
        int? exitCode,
        TaskCompletionSource<bool>? startupTcs)
    {
        // 运行代次校验：旧进程退出事件不得覆盖新一轮状态。
        if (instance.Record.Generation != generation)
        {
            return;
        }

        instance.LifetimeCts?.Cancel();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        instance.ExitCompletion = completion;
        instance.ExitHandlingTask = HandleExitAsync(instance, exitCode, startupTcs, completion);
    }

    private void HandleUnexpectedExit(ManagedInstance instance, long generation, int? exitCode)
    {
        if (instance.Record.Generation != generation)
        {
            return;
        }

        instance.LifetimeCts?.Cancel();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        instance.ExitCompletion = completion;
        instance.ExitHandlingTask = HandleExitAsync(instance, exitCode, instance.StartupCompletion, completion);
    }

    private async Task HandleExitAsync(
        ManagedInstance instance,
        int? exitCode,
        TaskCompletionSource<bool>? startupTcs,
        TaskCompletionSource<bool>? completion)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            instance.Record.ExitCode = exitCode;
            instance.Record.StoppedUtc = now;
            instance.Record.UpdatedUtc = now;

            var wasStopping = instance.State is InstanceState.Stopping;
            var wasFailed = instance.State is InstanceState.Failed;
            var wasReady = instance.State == InstanceState.Ready;

            if (wasStopping || instance.StopRequested)
            {
                instance.Record.State = InstanceState.Stopped;
                _logStore.Append(
                    instance.Id,
                    instance.Name,
                    LogStreamKind.Application,
                    $"实例已停止（退出代码 {exitCode?.ToString() ?? "未知"}）。");
            }
            else if (wasFailed)
            {
                // 失败后进程退出：保留 Failed 状态与错误信息
            }
            else
            {
                instance.Record.State = InstanceState.ExitedUnexpectedly;
                var tail = _logStore.GetRecent(instance.Id, 5)
                    .Select(l => l.Text)
                    .LastOrDefault(t => !string.IsNullOrWhiteSpace(t));
                instance.Record.LastError =
                    $"进程意外退出（退出代码 {exitCode?.ToString() ?? "未知"}）。" +
                    (tail is null ? string.Empty : " 最近日志：" + tail);
                _logStore.Append(
                    instance.Id,
                    instance.Name,
                    LogStreamKind.Application,
                    instance.Record.LastError);
            }

            await _instances.UpsertAsync(instance.Record, CancellationToken.None).ConfigureAwait(false);
            await UpdateHistoryOnExitAsync(instance, exitCode, wasReady).ConfigureAwait(false);
            RaiseChanged(instance.Id);
            startupTcs?.TrySetResult(instance.Record.State == InstanceState.Ready);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理实例 {Name} 退出事件时出错", instance.Name);
        }
        finally
        {
            completion?.TrySetResult(true);
        }
    }

    private async Task UpdateHistoryOnExitAsync(ManagedInstance instance, int? exitCode, bool wasReady)
    {
        try
        {
            var recent = await _history.QueryAsync(new HistoryQuery { InstanceId = instance.Id, Limit = 5 })
                .ConfigureAwait(false);
            var entry = recent.FirstOrDefault(h => h.EndedUtc is null);
            if (entry is null)
            {
                return;
            }

            entry.EndedUtc = _timeProvider.GetUtcNow();
            entry.ExitCode = exitCode;
            entry.FinalState = instance.Record.State;
            entry.ErrorSummary = instance.Record.State is InstanceState.Stopped
                ? null
                : instance.Record.LastError;
            entry.FailureStage ??= instance.FailureStage;
            await _history.UpdateAsync(entry, CancellationToken.None).ConfigureAwait(false);
            _ = wasReady;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新启动历史失败");
        }
    }

    private async Task<StartResult> FailAsync(
        ManagedInstance instance,
        string stage,
        string message,
        IReadOnlyList<ValidationIssue>? issues,
        CancellationToken cancellationToken,
        StartupHistoryEntry? history = null)
    {
        instance.FailureStage = stage;
        instance.Record.LastError = message;
        instance.Record.StoppedUtc = _timeProvider.GetUtcNow();
        instance.Record.UpdatedUtc = _timeProvider.GetUtcNow();

        if (!InstanceStateMachine.IsTerminal(instance.State))
        {
            instance.Record.State = InstanceState.Failed;
        }
        else if (instance.Record.State == InstanceState.Stopped)
        {
            instance.Record.State = InstanceState.Failed;
        }

        _logStore.Append(instance.Id, instance.Name, LogStreamKind.Application, "启动失败：" + message);
        await _instances.UpsertAsync(instance.Record, cancellationToken).ConfigureAwait(false);

        if (history is not null)
        {
            history.EndedUtc = _timeProvider.GetUtcNow();
            history.FinalState = InstanceState.Failed;
            history.FailureStage = stage;
            history.ErrorSummary = message;
            await _history.UpdateAsync(history, cancellationToken).ConfigureAwait(false);
        }

        instance.StartupCompletion?.TrySetResult(false);
        RaiseChanged(instance.Id);
        _logger.LogWarning("实例 {Name} 启动失败（{Stage}）：{Message}", instance.Name, stage, message);
        return StartResult.Fail(instance.Record.State, message, issues);
    }

    private void SetState(ManagedInstance instance, InstanceState state, string? logMessage)
    {
        if (instance.Record.State != state)
        {
            InstanceStateMachine.EnsureTransition(instance.Record.State, state);
            instance.Record.State = state;
            instance.Record.UpdatedUtc = _timeProvider.GetUtcNow();
        }

        if (logMessage is not null)
        {
            _logStore.Append(instance.Id, instance.Name, LogStreamKind.Application, logMessage);
        }

        RaiseChanged(instance.Id);
    }

    private void RaiseChanged(Guid instanceId)
        => InstanceChanged?.Invoke(this, instanceId);

    private static ServerLaunchOptions CloneOptions(ServerLaunchOptions options)
    {
        var json = JsonSerializer.Serialize(options, JsonDefaults.Storage);
        return JsonSerializer.Deserialize<ServerLaunchOptions>(json, JsonDefaults.Storage) ?? new ServerLaunchOptions();
    }
}
