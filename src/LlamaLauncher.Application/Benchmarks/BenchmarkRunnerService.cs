using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Instances;
using LlamaLauncher.Domain;
using LlamaLauncher.Domain.Benchmarks;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Application.Benchmarks;

/// <summary>运行进度（每次样本开始/完成与冷却时上报）。</summary>
public sealed record BenchmarkProgress(
    BenchmarkRunRecord Run,
    int CompletedSamples,
    int TotalSamples,
    string StageText,
    BenchmarkSample? LastSample);

/// <summary>
/// 基准测试编排：校验配置与实例 → 预热 + 重复测量（可取消、样本级增量保存）→ 聚合状态落库。
/// 不引用 WPF；进度通过回调上报，由 UI 自行切回 Dispatcher。
/// </summary>
public sealed class BenchmarkRunnerService
{
    /// <summary>单次调用的时限（提示处理 + 生成；超时按失败样本处理）。</summary>
    private static readonly TimeSpan PerCallTimeout = TimeSpan.FromMinutes(15);

    /// <summary>连续失败达到该次数时提前中止（服务不可用的典型信号）。</summary>
    private const int MaxConsecutiveFailures = 2;

    private readonly IBenchmarkClient _client;
    private readonly IBenchmarkRunStore _store;
    private readonly IInstanceManager _instances;
    private readonly IModelRepository _models;
    private readonly IInstallationRepository _installations;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BenchmarkRunnerService> _logger;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public BenchmarkRunnerService(
        IBenchmarkClient client,
        IBenchmarkRunStore store,
        IInstanceManager instances,
        IModelRepository models,
        IInstallationRepository installations,
        TimeProvider timeProvider,
        ILogger<BenchmarkRunnerService> logger)
    {
        _client = client;
        _store = store;
        _instances = instances;
        _models = models;
        _installations = installations;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// 执行一次基准测试。配置非法或实例不可用时抛出 <see cref="InvalidOperationException"/>；
    /// 用户取消返回“已取消”记录（保留已完成样本）；其余情况始终返回记录（状态为已完成或失败）。
    /// </summary>
    public async Task<BenchmarkRunRecord> RunAsync(
        Guid instanceId,
        BenchmarkOptions options,
        Action<BenchmarkProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var issues = BenchmarkLimits.Validate(options);
        if (issues.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", issues));
        }

        var instance = _instances.Get(instanceId)
            ?? throw new InvalidOperationException("实例不存在或已被移除。");
        if (instance.State != InstanceState.Ready)
        {
            throw new InvalidOperationException(
                $"实例“{instance.Name}”当前状态为 {instance.State}，只有“运行中”的实例才能执行基准测试。");
        }

        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("已有一次基准测试正在运行，请等待完成或先取消。");
        }

        try
        {
            return await ExecuteAsync(instance, options.Clone(), onProgress, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task<BenchmarkRunRecord> ExecuteAsync(
        ManagedInstance instance,
        BenchmarkOptions options,
        Action<BenchmarkProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        var record = await CreateRecordAsync(instance, options, cancellationToken).ConfigureAwait(false);
        var targets = options.PromptTokens.Distinct().OrderBy(v => v).ToList();
        var perTarget = options.WarmupRuns + options.Repetitions;
        var totalSamples = targets.Count * perTarget;
        var cooldown = TimeSpan.FromSeconds(options.CooldownSeconds);
        var completed = 0;
        var consecutiveFailures = 0;
        string? lastError = null;
        string? stopReason = null;
        var apiKey = await TryGetApiKeyAsync(instance.Id, cancellationToken).ConfigureAwait(false);

        try
        {
            for (var targetIndex = 0; targetIndex < targets.Count && stopReason is null; targetIndex++)
            {
                var target = targets[targetIndex];
                for (var i = 0; i < perTarget; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var isWarmup = i < options.WarmupRuns;
                    var repetition = isWarmup ? 0 : i - options.WarmupRuns + 1;
                    var stage = isWarmup
                        ? $"预热：目标 {target} tokens 提示"
                        : $"测量：目标 {target} tokens 提示（第 {repetition}/{options.Repetitions} 次）";
                    onProgress?.Invoke(new BenchmarkProgress(record, completed, totalSamples, stage, null));

                    var prompt = BenchmarkPromptFactory.Build(target, BenchmarkPromptFactory.CreateSalt());
                    var result = await _client.RunOnceAsync(
                            new BenchmarkCallRequest(
                                instance.LocalBaseUrl,
                                apiKey,
                                prompt,
                                options.GenerateTokens,
                                PerCallTimeout),
                            cancellationToken)
                        .ConfigureAwait(false);

                    var sample = MapSample(target, repetition, isWarmup, result);
                    record.Samples.Add(sample);
                    completed++;
                    if (result.Success)
                    {
                        consecutiveFailures = 0;
                    }
                    else
                    {
                        consecutiveFailures++;
                        lastError = result.Error ?? "未知错误。";
                    }

                    onProgress?.Invoke(new BenchmarkProgress(record, completed, totalSamples, stage, sample));
                    await PersistAsync(record).ConfigureAwait(false);

                    if (consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        stopReason = $"连续 {consecutiveFailures} 次调用失败：{lastError}";
                        break;
                    }

                    var isLastSample = targetIndex == targets.Count - 1 && i == perTarget - 1;
                    if (cooldown > TimeSpan.Zero && !isLastSample)
                    {
                        onProgress?.Invoke(new BenchmarkProgress(
                            record,
                            completed,
                            totalSamples,
                            $"冷却 {options.CooldownSeconds} 秒…",
                            null));
                        await Task.Delay(cooldown, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            var successCount = record.Samples.Count(s => s.Succeeded && !s.IsWarmup);
            if (successCount > 0)
            {
                record.State = BenchmarkRunState.Succeeded;
                record.ErrorSummary = stopReason is null ? null : "提前中止：" + stopReason;
            }
            else
            {
                record.State = BenchmarkRunState.Failed;
                record.ErrorSummary = stopReason ?? lastError ?? "没有产生任何成功的测量样本。";
            }
        }
        catch (OperationCanceledException)
        {
            record.State = BenchmarkRunState.Canceled;
            record.ErrorSummary = "已取消（保留已完成的样本）。";
            _logger.LogInformation("基准测试已取消（RunId={RunId}，已完成 {Count} 个样本）。", record.RunId, completed);
        }
        finally
        {
            var ended = _timeProvider.GetUtcNow();
            record.EndedUtc = ended;
            record.DurationMs = (ended - record.StartedUtc).TotalMilliseconds;
            await PersistAsync(record).ConfigureAwait(false);
            onProgress?.Invoke(new BenchmarkProgress(
                record,
                completed,
                totalSamples,
                DescribeFinalStage(record),
                null));
        }

        return record;
    }

    private async Task<BenchmarkRunRecord> CreateRecordAsync(
        ManagedInstance instance,
        BenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        var record = new BenchmarkRunRecord
        {
            InstanceId = instance.Id,
            InstanceNameSnapshot = instance.Name,
            ProfileNameSnapshot = instance.Profile?.Name,
            StartedUtc = _timeProvider.GetUtcNow(),
            Options = options,
            AppVersion = typeof(BenchmarkRunnerService).Assembly.GetName().Version?.ToString(3) ?? "unknown",
        };

        if (instance.Record.ModelId is { } modelId)
        {
            try
            {
                var model = await _models.GetByIdAsync(modelId, cancellationToken).ConfigureAwait(false);
                record.ModelNameSnapshot = model?.FilePath is { } path ? Path.GetFileName(path) : model?.Name;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "读取实例模型信息失败（InstanceId={InstanceId}）。", instance.Id);
            }
        }

        if (instance.Record.InstallationId is { } installationId)
        {
            try
            {
                var installation = await _installations.GetByIdAsync(installationId, cancellationToken)
                    .ConfigureAwait(false);
                record.VersionLabel = installation?.VersionLabel;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "读取实例 llama.cpp 版本失败（InstanceId={InstanceId}）。", instance.Id);
            }
        }

        return record;
    }

    private async Task PersistAsync(BenchmarkRunRecord record)
    {
        try
        {
            await _store.UpsertAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 落盘失败不影响测量继续进行；仅记录警告。
            _logger.LogWarning(ex, "写入基准测试记录失败（RunId={RunId}）。", record.RunId);
        }
    }

    private async Task<string?> TryGetApiKeyAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        try
        {
            return await _instances.GetInstanceApiKeyAsync(instanceId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "读取实例 API Key 失败（InstanceId={InstanceId}）。", instanceId);
            return null;
        }
    }

    private static BenchmarkSample MapSample(
        int target,
        int repetition,
        bool isWarmup,
        BenchmarkCallResult result) => new()
    {
        TargetPromptTokens = target,
        Repetition = repetition,
        IsWarmup = isWarmup,
        PromptTokens = result.PromptTokens,
        PromptMs = result.PromptMs,
        PromptTokensPerSecond = result.PromptTokensPerSecond,
        GeneratedTokens = result.GeneratedTokens,
        GeneratedMs = result.GeneratedMs,
        GeneratedTokensPerSecond = result.GeneratedTokensPerSecond,
        TimeToFirstTokenMs = result.TimeToFirstTokenMs,
        TotalMs = result.TotalMs,
        Error = result.Success ? null : result.Error ?? "未知错误。",
    };

    private static string DescribeFinalStage(BenchmarkRunRecord record) => record.State switch
    {
        BenchmarkRunState.Succeeded => "测试完成。",
        BenchmarkRunState.Failed => "测试失败。",
        BenchmarkRunState.Canceled => "已取消。",
        _ => "已结束。",
    };
}
