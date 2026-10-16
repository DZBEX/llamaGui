using System.Diagnostics;
using System.Text.Json.Nodes;
using LlamaLauncher.Domain.Workflows;
using LlamaLauncher.Domain.Workflows.Expressions;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Application.Workflows;

/// <summary>
/// 工作流执行引擎：图调度（有向无环）、分支路径、并发上限、暂停/继续/单步/断点、
/// 节点超时与有限重试、取消传播、异常路由、运行记录与事件通知。完全不依赖 WPF。
/// </summary>
public sealed class WorkflowEngine
{
    private readonly IReadOnlyDictionary<string, INodeExecutor> _executors;
    private readonly WorkflowValidator _validator;
    private readonly ILogger<WorkflowEngine> _logger;
    private readonly TimeProvider _time;

    public WorkflowEngine(
        IEnumerable<INodeExecutor> executors,
        WorkflowValidator validator,
        ILogger<WorkflowEngine> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(executors);
        _executors = executors.ToDictionary(e => e.TypeId, StringComparer.Ordinal);
        _validator = validator;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>共享校验器（运行前校验与设计器校验一致）。</summary>
    public WorkflowValidator Validator => _validator;

    /// <summary>已注册的节点执行器类型。</summary>
    public IReadOnlyList<string> ExecutorTypeIds => _executors.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>启动一次运行；返回句柄用于控制（暂停/继续/单步/取消）与等待结果。</summary>
    public WorkflowRunHandle Start(WorkflowRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = new RunContext(request, _executors, _validator, _logger, _time, depth: 0);
        var completion = Task.Run(() => context.ExecuteAsync(CancellationToken.None));
        return new WorkflowRunHandle(
            context.RunId,
            completion,
            context.RequestPause,
            context.RequestResume,
            context.RequestStep,
            context.RequestCancel,
            () => context.CurrentState,
            () => context.CurrentPauseReason);
    }

    /// <summary>运行一次并等待结束（测试与批处理用）。</summary>
    public Task<WorkflowRunResult> RunAsync(WorkflowRunRequest request)
        => Start(request).Completion;

    /// <summary>单次运行的内部上下文（调度状态机）。</summary>
    private sealed class RunContext : IWorkflowChildRunner
    {
        private readonly WorkflowRunRequest _request;
        private readonly WorkflowDefinition _definition;
        private readonly IReadOnlyDictionary<string, INodeExecutor> _executors;
        private readonly WorkflowValidator _validator;
        private readonly ILogger _logger;
        private readonly TimeProvider _time;
        private readonly int _depth;
        private readonly WorkflowExecutionLimits _limits;
        private readonly WorkflowServices _services;

        private readonly JsonObject _inputs = new();
        private readonly JsonObject _variables = new();
        private readonly JsonObject _upstream = new();
        private readonly JsonObject _meta = new();
        private readonly List<NodeRunRecord> _records = new();
        private readonly List<WorkflowLogEntry> _logs = new();
        private readonly Dictionary<string, NodeRuntime> _nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, EdgeRuntime> _edges = new(StringComparer.Ordinal);
        private readonly ExecutionGate _gate = new();
        private readonly CancellationTokenSource _cts;
        private readonly ITimer _runTimer;
        private readonly Stopwatch _stopwatch = new();

        private readonly object _sync = new();
        private int _logSequence;
        private int _nodeSequence;
        private volatile WorkflowRunState _state = WorkflowRunState.Created;
        private string? _errorSummary;
        private string? _validationSummary;
        private JsonObject? _output;
        private WorkflowEndStatus? _endStatus;
        private string? _endMessage;
        private volatile bool _terminateRequested;
        private volatile bool _cancelRequested;
        private volatile bool _completionRequested;
        private volatile bool _runTimedOut;
        private bool _logsCapped;
        private bool _stepMode;
        private bool _stepConsumed;
        private DateTimeOffset _startedUtc;
        private readonly HashSet<string> _consumedBreakpoints = new(StringComparer.Ordinal);

        public RunContext(
            WorkflowRunRequest request,
            IReadOnlyDictionary<string, INodeExecutor> executors,
            WorkflowValidator validator,
            ILogger logger,
            TimeProvider time,
            int depth)
        {
            _request = request;
            _definition = request.Definition.Clone();
            _executors = executors;
            _validator = validator;
            _logger = logger;
            _time = time;
            _depth = depth;
            _limits = request.Limits;
            _services = new WorkflowServices
            {
                DecisionClient = request.Services.DecisionClient,
                ModelResolver = request.Services.ModelResolver,
                DefinitionStore = request.Services.DefinitionStore,
                Algorithms = request.Services.Algorithms,
                Interactions = request.Services.Interactions,
                ChildRunner = this,
            };
            _cts = new CancellationTokenSource();
            _runTimer = _time.CreateTimer(
                _ =>
                {
                    _runTimedOut = true;
                    _cts.Cancel();
                },
                null,
                _limits.MaxRunDuration,
                Timeout.InfiniteTimeSpan);
        }

        public string RunId { get; } = Guid.NewGuid().ToString("N");

        public WorkflowRunState CurrentState => _state;

        public WorkflowPauseReason? CurrentPauseReason => _gate.Reason;

        // ── 控制入口 ─────────────────────────────────────────────

        public void RequestPause()
        {
            if (_terminateRequested || _cancelRequested || _completionRequested || _state is not WorkflowRunState.Running)
            {
                return;
            }

            SetState(WorkflowRunState.Pausing, WorkflowPauseReason.UserRequest, "暂停请求已发出，将在当前节点边界暂停。");
            _gate.Pause(WorkflowPauseReason.UserRequest);
            SetState(WorkflowRunState.Paused, WorkflowPauseReason.UserRequest, "已暂停。");
        }

        public void RequestResume()
        {
            _stepMode = false;
            _gate.Release();
            if (!_cancelRequested && !_terminateRequested && !_completionRequested)
            {
                SetState(WorkflowRunState.Running, null, "已继续执行。");
            }
        }

        public void RequestStep()
        {
            _stepMode = true;
            _stepConsumed = false;
            _gate.Release();
            if (!_cancelRequested && !_terminateRequested && !_completionRequested)
            {
                SetState(WorkflowRunState.Running, null, "单步执行：将在下一个节点后暂停。");
            }
        }

        public void RequestCancel()
        {
            if (_cancelRequested)
            {
                return;
            }

            _cancelRequested = true;
            SetState(WorkflowRunState.Canceling, null, "取消请求已发出，等待活动节点结束。");
            _gate.Release();
            _cts.Cancel();
        }

        // ── 主执行 ─────────────────────────────────────────────

        public async Task<WorkflowRunResult> ExecuteAsync(CancellationToken externalToken)
        {
            using var externalRegistration = externalToken.CanBeCanceled
                ? externalToken.Register(() =>
                {
                    _cancelRequested = true;
                    _cts.Cancel();
                })
                : default;

            var running = new Dictionary<string, Task<NodeOutcome>>(StringComparer.Ordinal);
            _stopwatch.Start();
            _startedUtc = _time.GetUtcNow();
            try
            {
                SetState(WorkflowRunState.Validating, null, "正在校验工作流定义。");
                InitializeRuntime();
                var report = ValidateBeforeRun();
                _validationSummary = report.Summary;
                if (report.HasErrors)
                {
                    foreach (var issue in report.OrderedIssues.Where(i => i.Severity == WorkflowIssueSeverity.Error).Take(20))
                    {
                        Log(WorkflowLogLevel.Error, $"[校验] {issue.Message}", issue.NodeId);
                    }

                    var summary = string.Join(
                        "；",
                        report.OrderedIssues
                            .Where(i => i.Severity == WorkflowIssueSeverity.Error)
                            .Take(3)
                            .Select(i => i.Message));
                    return await FinishAsync(WorkflowRunState.Failed, "运行前校验未通过：" + summary).ConfigureAwait(false);
                }

                foreach (var warning in report.OrderedIssues.Where(i => i.Severity == WorkflowIssueSeverity.Warning).Take(10))
                {
                    Log(WorkflowLogLevel.Warning, $"[校验] {warning.Message}", warning.NodeId);
                }

                if (!PrepareInputs(out var inputError))
                {
                    return await FinishAsync(WorkflowRunState.Failed, inputError).ConfigureAwait(false);
                }

                if (_request.StartAtNodeId is { Length: > 0 } startAt)
                {
                    if (!ApplyStartSnapshot(startAt, out var snapshotError))
                    {
                        return await FinishAsync(WorkflowRunState.Failed, snapshotError).ConfigureAwait(false);
                    }
                }

                SetState(WorkflowRunState.Running, null, $"工作流“{_definition.Name}”开始运行。");
                Log(
                    WorkflowLogLevel.Info,
                    $"工作流开始运行（模式：{_request.TestMode}，最大并发：{_limits.MaxParallelism}）。",
                    null);

                if (_request.StartInStepMode)
                {
                    _gate.Pause(WorkflowPauseReason.StepCompleted);
                    SetState(WorkflowRunState.Paused, WorkflowPauseReason.StepCompleted, "单步模式：等待执行第一个节点。");
                }

                while (true)
                {
                    var stopping = _terminateRequested || _cancelRequested || _completionRequested;
                    if (stopping)
                    {
                        _gate.Release();
                    }
                    else
                    {
                        await _gate.WaitAsync(_cts.Token).ConfigureAwait(false);
                        if (!_terminateRequested && !_cancelRequested && !_completionRequested)
                        {
                            await LaunchReadyNodesAsync(running).ConfigureAwait(false);
                        }
                    }

                    if (running.Count == 0)
                    {
                        if (_terminateRequested || _cancelRequested || _completionRequested)
                        {
                            break;
                        }

                        if (AllNodesResolved())
                        {
                            break;
                        }

                        if (GetReadyNodes().Count == 0)
                        {
                            return await FinishAsync(
                                    WorkflowRunState.Failed,
                                    "调度停滞：没有可运行节点，但工作流尚未到达结束节点（请检查分支连线与输入映射）。")
                                .ConfigureAwait(false);
                        }

                        continue;
                    }

                    var finishedTask = await Task.WhenAny(running.Values).ConfigureAwait(false);
                    var finishedId = running.First(pair => pair.Value == finishedTask).Key;
                    var outcome = await finishedTask.ConfigureAwait(false);
                    running.Remove(finishedId);
                    ProcessOutcome(outcome);

                    if (_stepMode && !_stepConsumed && !_terminateRequested && !_cancelRequested && !_completionRequested)
                    {
                        _stepConsumed = true;
                        _stepMode = false;
                        _gate.Pause(WorkflowPauseReason.StepCompleted);
                        SetState(WorkflowRunState.Paused, WorkflowPauseReason.StepCompleted, "单步完成，已暂停。");
                    }
                }

                if (running.Count > 0)
                {
                    try
                    {
                        await Task.WhenAll(running.Values).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // 终止/取消时在途节点被取消属预期。
                    }

                    foreach (var pending in running.Values)
                    {
                        if (pending.IsCompletedSuccessfully)
                        {
                            ProcessOutcome(pending.Result);
                        }
                    }
                }

                if (_cancelRequested)
                {
                    return await FinishAsync(
                            _runTimedOut ? WorkflowRunState.TimedOut : WorkflowRunState.Canceled,
                            _runTimedOut ? $"运行超过最大时长（{_limits.MaxRunDuration}）。" : "运行已取消。")
                        .ConfigureAwait(false);
                }

                if (_terminateRequested)
                {
                    return await FinishAsync(WorkflowRunState.Failed, _errorSummary ?? "节点失败导致运行终止。")
                        .ConfigureAwait(false);
                }

                if (_endStatus == WorkflowEndStatus.BusinessFailure)
                {
                    return await FinishAsync(WorkflowRunState.Failed, _endMessage ?? "工作流以业务失败结束。")
                        .ConfigureAwait(false);
                }

                if (_endStatus == WorkflowEndStatus.Succeeded)
                {
                    return await FinishAsync(WorkflowRunState.Succeeded, null).ConfigureAwait(false);
                }

                return await FinishAsync(
                        WorkflowRunState.Failed,
                        "工作流结束时未到达任何结束节点（可能存在被跳过的分支或未连接的节点）。")
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    await Task.WhenAll(running.Values).ConfigureAwait(false);
                }
                catch
                {
                    // 在途节点取消结果不影响最终状态判定。
                }

                foreach (var pending in running.Values.Where(p => p.IsCompletedSuccessfully))
                {
                    ProcessOutcome(pending.Result);
                }

                if (_terminateRequested)
                {
                    return await FinishAsync(WorkflowRunState.Failed, _errorSummary ?? "节点失败导致运行终止。")
                        .ConfigureAwait(false);
                }

                return await FinishAsync(
                        _runTimedOut ? WorkflowRunState.TimedOut : WorkflowRunState.Canceled,
                        _runTimedOut ? $"运行超过最大时长（{_limits.MaxRunDuration}）。" : "运行已取消。")
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "工作流运行出现内部错误（RunId={RunId}）", RunId);
                Log(WorkflowLogLevel.Error, $"引擎内部错误：{ex.Message}", null);
                return await FinishAsync(WorkflowRunState.Failed, "引擎内部错误：" + ex.Message).ConfigureAwait(false);
            }
        }

        // ── 初始化与校验 ─────────────────────────────────────────

        private void InitializeRuntime()
        {
            foreach (var node in _definition.Nodes)
            {
                var record = new NodeRunRecord
                {
                    NodeId = node.Id,
                    NodeTypeId = node.TypeId,
                    Name = node.Name,
                    State = NodeRunState.Pending,
                };
                _records.Add(record);
                _nodes[node.Id] = new NodeRuntime { Node = node, Record = record };
            }

            foreach (var edge in _definition.Edges)
            {
                if (!_nodes.ContainsKey(edge.SourceNodeId) || !_nodes.ContainsKey(edge.TargetNodeId))
                {
                    continue;
                }

                _edges[edge.Id] = new EdgeRuntime { Edge = edge };
                _nodes[edge.TargetNodeId].IncomingCount++;
            }

            _meta["runId"] = RunId;
            _meta["workflowId"] = _definition.Id;
            _meta["workflowName"] = _definition.Name;
            _meta["testMode"] = _request.TestMode.ToString();
            _meta["startedAt"] = DateTimeOffset.UtcNow.ToString("O");
            _meta["defaultInstanceId"] = _request.DefaultInstanceId;
        }

        private WorkflowValidationReport ValidateBeforeRun()
            => _validator.Validate(_definition, new WorkflowValidationContext
            {
                Definition = _definition,
                AlgorithmKeys = _services.Algorithms.List().Select(a => a.Key).ToList(),
                ForRun = true,
            });

        private bool PrepareInputs(out string? error)
        {
            error = null;
            var provided = _request.Inputs?.DeepClone() as JsonObject;
            foreach (var definition in _definition.Inputs)
            {
                JsonNode? value = null;
                var hasValue = provided?.TryGetPropertyValue(definition.Name, out value) == true;
                if (!hasValue && definition.DefaultValueJson is not null)
                {
                    value = WorkflowJson.TryParse(definition.DefaultValueJson, out _);
                    hasValue = true;
                }

                if (!hasValue)
                {
                    if (definition.Required)
                    {
                        error = $"工作流输入“{definition.Name}”为必填项，但未提供（且没有默认值）。";
                        return false;
                    }

                    continue;
                }

                if (value is not null && !MatchesType(value, definition.Type))
                {
                    error = $"工作流输入“{definition.Name}”的类型不符合定义（期望 {definition.Type}）。";
                    return false;
                }

                _inputs[definition.Name] = value?.DeepClone();
            }

            if (provided is not null)
            {
                foreach (var pair in provided)
                {
                    if (!_definition.Inputs.Any(v => v.Name == pair.Key))
                    {
                        Log(WorkflowLogLevel.Warning, $"输入“{pair.Key}”未在工作流输入定义中声明，已透传使用。", null);
                        _inputs[pair.Key] = pair.Value?.DeepClone();
                    }
                }
            }

            return true;
        }

        private static bool MatchesType(JsonNode value, WorkflowValueType type)
            => type switch
            {
                WorkflowValueType.Any => true,
                WorkflowValueType.String => value is JsonValue v && v.TryGetValue<string>(out _),
                WorkflowValueType.Number => value is JsonValue v && v.TryGetValue<double>(out _),
                WorkflowValueType.Boolean => value is JsonValue v && v.TryGetValue<bool>(out _),
                WorkflowValueType.Object => value is JsonObject,
                WorkflowValueType.Array => value is JsonArray,
                _ => true,
            };

        private bool ApplyStartSnapshot(string startAtNodeId, out string? error)
        {
            error = null;
            var startNode = _definition.FindNode(startAtNodeId);
            if (startNode is null)
            {
                error = $"指定的起始节点“{startAtNodeId}”不存在。";
                return false;
            }

            JsonObject? snapshot = null;
            if (!string.IsNullOrWhiteSpace(_request.StartSnapshotJson))
            {
                snapshot = WorkflowJson.TryParse(_request.StartSnapshotJson, out var parseError) as JsonObject;
                if (snapshot is null)
                {
                    error = $"起始快照不是有效 JSON 对象：{parseError}";
                    return false;
                }
            }

            if (snapshot?["variables"] is JsonObject variables)
            {
                foreach (var pair in variables)
                {
                    _variables[pair.Key] = pair.Value?.DeepClone();
                }
            }

            if (snapshot?["upstream"] is JsonObject upstream)
            {
                foreach (var pair in upstream)
                {
                    _upstream[pair.Key] = pair.Value?.DeepClone();
                }
            }

            var preExecuted = new HashSet<string>(_request.PreExecutedNodeIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            foreach (var nodeId in preExecuted)
            {
                if (_nodes.TryGetValue(nodeId, out var runtime))
                {
                    runtime.Started = true;
                    runtime.Completed = true;
                    runtime.Record.State = NodeRunState.Skipped;
                    Notify(runtime.Record);
                    ResolveEdges(runtime, null, errorOnly: false, allInactive: false, errorContext: null);
                }
            }

            // 起始节点的入边必须已由预执行节点或快照满足。
            var target = _nodes[startAtNodeId];
            if (target.ResolvedIncoming < target.IncomingCount)
            {
                error = $"无法从节点“{startNode.Name}”启动：其上游输入不完整（缺少快照或预执行节点），" +
                        "请提供“variables/upstream”快照，或从更早的节点启动。";
                return false;
            }

            return true;
        }

        // ── 调度 ─────────────────────────────────────────────

        private async Task LaunchReadyNodesAsync(Dictionary<string, Task<NodeOutcome>> running)
        {
            foreach (var runtime in GetReadyNodes())
            {
                if (running.Count >= _limits.MaxParallelism)
                {
                    return;
                }

                if (_terminateRequested || _cancelRequested || _completionRequested)
                {
                    return;
                }

                var node = runtime.Node;
                if (node.IsBreakpoint && _consumedBreakpoints.Add(node.Id))
                {
                    runtime.Record.State = NodeRunState.PausedAtBreakpoint;
                    Notify(runtime.Record);
                    Log(WorkflowLogLevel.Warning, "命中断点，已在执行前暂停。", node.Id);
                    SetState(WorkflowRunState.Paused, WorkflowPauseReason.Breakpoint, $"断点：{node.Name}");
                    _gate.Pause(WorkflowPauseReason.Breakpoint);
                    await _gate.WaitAsync(_cts.Token).ConfigureAwait(false);

                    if (_terminateRequested || _cancelRequested || _completionRequested)
                    {
                        return;
                    }

                    runtime.Record.State = NodeRunState.Pending;
                    Notify(runtime.Record);
                    SetState(WorkflowRunState.Running, null, null);
                }

                runtime.Started = true;
                if (node.TypeId == NodeTypeCatalog.Confirm)
                {
                    SetState(WorkflowRunState.WaitingForUser, null, $"等待人工确认：{node.Name}");
                }

                running[node.Id] = ExecuteNodeAsync(runtime);
            }
        }

        private List<NodeRuntime> GetReadyNodes()
        {
            var ready = new List<NodeRuntime>();
            foreach (var runtime in _nodes.Values)
            {
                if (runtime.Started || runtime.Completed || runtime.Skipped)
                {
                    continue;
                }

                if (runtime.ResolvedIncoming != runtime.IncomingCount)
                {
                    continue;
                }

                if (runtime.IncomingCount > 0 && runtime.ActiveIncoming == 0)
                {
                    continue;
                }

                ready.Add(runtime);
            }

            return ready.OrderBy(r => _definition.Nodes.FindIndex(n => n.Id == r.Node.Id)).ToList();
        }

        private bool AllNodesResolved()
            => _nodes.Values.All(r => r.Started || r.Completed || r.Skipped);

        // ── 节点执行 ─────────────────────────────────────────────

        private async Task<NodeOutcome> ExecuteNodeAsync(NodeRuntime runtime)
        {
            var node = runtime.Node;
            var record = runtime.Record;
            record.State = NodeRunState.Running;
            record.StartedUtc = _time.GetUtcNow();
            record.Sequence = ++_nodeSequence;
            Notify(record);
            Log(WorkflowLogLevel.Info, "开始执行。", node.Id);

            var startedTimestamp = _time.GetTimestamp();
            var scope = BuildScope(runtime.ErrorContext);
            if (!AssembleInputs(runtime, scope, out var inputs, out var inputError))
            {
                return Fail(runtime, startedTimestamp, inputError!, NodeErrorKind.Failed, "InputMappingFailed", null);
            }

            var executor = _executors.GetValueOrDefault(node.TypeId);
            if (executor is null)
            {
                return Fail(
                    runtime,
                    startedTimestamp,
                    $"节点类型“{node.TypeId}”没有可用的执行器实现。",
                    NodeErrorKind.Failed,
                    "ExecutorMissing",
                    null);
            }

            var timeout = node.TimeoutSeconds is { } seconds
                ? TimeSpan.FromSeconds(seconds)
                : _limits.DefaultNodeTimeout;
            var maxAttempts = Math.Clamp(node.Retry.MaxAttempts, 1, 5);
            NodeExecutionResult? lastResult = null;
            string? lastErrorType = null;
            var attempt = 1;
            for (; attempt <= maxAttempts; attempt++)
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                attemptCts.CancelAfter(timeout);

                var context = new NodeExecutionContext
                {
                    Node = node,
                    RunId = RunId,
                    TestMode = _request.TestMode,
                    Inputs = inputs,
                    Scope = scope,
                    WorkflowInputs = _inputs,
                    Variables = _variables,
                    Upstream = _upstream,
                    Meta = _meta,
                    ErrorContext = runtime.ErrorContext,
                    Services = _services,
                    Limits = _limits,
                    Mock = GetMock(node.Id),
                    Attempt = attempt,
                    Log = (level, message, exceptionText) => Log(level, message, node.Id, exceptionText),
                    CancellationToken = attemptCts.Token,
                };

                NodeExecutionResult result;
                try
                {
                    result = await executor.ExecuteAsync(context).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                {
                    result = NodeExecutionResult.Fail(new NodeExecutionError(
                        NodeErrorKind.Timeout,
                        $"节点执行超时（{timeout.TotalSeconds:0} 秒）。",
                        "NodeTimeout"));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "节点执行异常（RunId={RunId}, NodeId={NodeId}）", RunId, node.Id);
                    result = NodeExecutionResult.Fail(new NodeExecutionError(
                        NodeErrorKind.Failed,
                        $"节点执行异常：{ex.Message}",
                        ex.GetType().Name));
                }

                if (result.Success)
                {
                    record.Retries = attempt - 1;
                    return CompleteSuccess(runtime, startedTimestamp, result);
                }

                lastResult = result;
                lastErrorType = result.Error?.ErrorType;
                var retryable = attempt < maxAttempts && ShouldRetry(node.Retry, result.Error);
                if (!retryable)
                {
                    break;
                }

                Log(
                    WorkflowLogLevel.Warning,
                    $"第 {attempt} 次尝试失败（{result.Error?.Message}），将在 {node.Retry.DelayMilliseconds} 毫秒后重试。",
                    node.Id);
                if (node.Retry.DelayMilliseconds > 0)
                {
                    await Task.Delay(node.Retry.DelayMilliseconds, _cts.Token).ConfigureAwait(false);
                }
            }

            record.Retries = Math.Max(0, attempt - 1);
            var error = lastResult?.Error ?? new NodeExecutionError(NodeErrorKind.Failed, "节点执行失败。");
            return Fail(runtime, startedTimestamp, error.Message, error.Kind, lastErrorType ?? error.ErrorType, error.Context, lastResult?.Diagnostics);
        }

        private static bool ShouldRetry(RetryPolicy policy, NodeExecutionError? error)
        {
            if (error is null || !policy.IsEffective)
            {
                return false;
            }

            return policy.Condition switch
            {
                RetryCondition.Always => true,
                RetryCondition.OnTimeout => error.Kind == NodeErrorKind.Timeout,
                RetryCondition.OnTransient => error.Kind is NodeErrorKind.Transient or NodeErrorKind.Timeout,
                _ => false,
            };
        }

        private WorkflowMockResponse? GetMock(string nodeId)
        {
            if (_request.TestMode != WorkflowTestMode.Simulated || _request.MockResponses is null)
            {
                return null;
            }

            return _request.MockResponses.GetValueOrDefault(nodeId);
        }

        private JsonObject BuildScope(JsonObject? errorContext)
            => WorkflowScope.Create(_inputs, _variables, _upstream, _meta, errorContext);

        private bool AssembleInputs(
            NodeRuntime runtime,
            JsonObject scope,
            out JsonObject inputs,
            out string? error)
        {
            inputs = new JsonObject();
            error = null;
            foreach (var mapping in runtime.Node.InputMappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.Target))
                {
                    continue;
                }

                JsonNode? value = null;
                if (!string.IsNullOrWhiteSpace(mapping.Source))
                {
                    if (!WorkflowExpression.TryParse(mapping.Source, out var expression, out var parseError))
                    {
                        error = $"输入映射“{mapping.Target}”的来源表达式无效：{parseError}";
                        return false;
                    }

                    if (!expression!.TryEvaluateToJson(scope, out value, out var evalError))
                    {
                        error = $"输入映射“{mapping.Target}”求值失败：{evalError}";
                        return false;
                    }
                }

                if (value is null && mapping.DefaultValueJson is not null)
                {
                    value = WorkflowJson.TryParse(mapping.DefaultValueJson, out _);
                }

                if (value is null)
                {
                    if (mapping.Required)
                    {
                        error = $"输入映射“{mapping.Target}”为必填项，但来源为空且没有默认值。";
                        return false;
                    }

                    continue;
                }

                if (!ExecutionHelpers.TryConvert(value, mapping.Convert, out var converted, out var convertError))
                {
                    error = $"输入映射“{mapping.Target}”转换失败：{convertError}";
                    return false;
                }

                inputs[mapping.Target] = converted?.DeepClone();
            }

            return true;
        }

        private NodeOutcome CompleteSuccess(NodeRuntime runtime, long startedTimestamp, NodeExecutionResult result)
        {
            var record = runtime.Record;
            record.State = NodeRunState.Succeeded;
            record.EndedUtc = _time.GetUtcNow();
            record.DurationMs = _time.GetElapsedTime(startedTimestamp).TotalMilliseconds;
            record.BranchTaken = result.FiredBranches is { Count: > 0 }
                ? string.Join(",", result.FiredBranches)
                : null;
            var (outputJson, truncated) = SerializeSnapshot(result.Outputs, _limits.MaxSnapshotChars);
            record.OutputJson = outputJson;
            record.OutputTruncated = truncated;
            if (result.Diagnostics is not null)
            {
                record.ModelDiagnosticsJson = WorkflowJson.Serialize(result.Diagnostics, indented: false);
            }

            Notify(record);
            Log(
                WorkflowLogLevel.Info,
                $"执行成功（耗时 {record.DurationMs:0} ms" +
                (result.FiredBranches is { Count: > 0 } ? $"，分支：{string.Join("、", result.FiredBranches)}" : string.Empty) +
                "）。",
                runtime.Node.Id);

            _upstream[runtime.Node.Id] = result.Outputs.DeepClone();
            if (runtime.Node.OutputVariable is { Length: > 0 } variableName
                && result.Outputs.TryGetPropertyValue(NodeTypeCatalog.DefaultOutputPort, out var mainOutput))
            {
                _variables[variableName] = mainOutput?.DeepClone();
                record.VariablesJson = WorkflowJson.Serialize(
                    new JsonObject { [variableName] = mainOutput?.DeepClone() },
                    indented: false);
            }

            if (runtime.Node.TypeId == NodeTypeCatalog.End)
            {
                _endStatus = result.EndStatus ?? WorkflowEndStatus.Succeeded;
                _endMessage = result.EndMessage;
                _output = result.Outputs[NodeTypeCatalog.DefaultOutputPort]?.DeepClone() as JsonObject;
                _completionRequested = true;
            }

            ResolveEdges(runtime, result.FiredBranches, errorOnly: false, allInactive: false, errorContext: null);
            return new NodeOutcome { Runtime = runtime, Success = true, FiredBranches = result.FiredBranches };
        }

        private NodeOutcome Fail(
            NodeRuntime runtime,
            long startedTimestamp,
            string message,
            NodeErrorKind kind,
            string? errorType,
            JsonObject? errorContext,
            JsonObject? diagnostics = null)
        {
            var record = runtime.Record;
            record.State = kind switch
            {
                NodeErrorKind.Canceled => NodeRunState.Canceled,
                NodeErrorKind.Timeout => NodeRunState.TimedOut,
                _ => NodeRunState.Failed,
            };
            record.EndedUtc = _time.GetUtcNow();
            record.DurationMs = _time.GetElapsedTime(startedTimestamp).TotalMilliseconds;
            record.ErrorType = errorType ?? kind.ToString();
            record.ErrorMessage = message;
            record.ErrorContextJson = errorContext is null
                ? null
                : WorkflowJson.Serialize(errorContext, indented: false);
            if (diagnostics is not null)
            {
                record.ModelDiagnosticsJson = WorkflowJson.Serialize(diagnostics, indented: false);
            }

            Notify(record);
            Log(WorkflowLogLevel.Error, $"执行失败：{message}", runtime.Node.Id);

            // 错误上下文统一形状：type/message/nodeId/attempts + 执行器提供的 details（如有）。
            var context = new JsonObject
            {
                ["type"] = record.ErrorType,
                ["message"] = message,
                ["nodeId"] = runtime.Node.Id,
                ["attempts"] = record.Retries + 1,
                ["details"] = errorContext?.DeepClone(),
            };
            return new NodeOutcome
            {
                Runtime = runtime,
                Success = false,
                ErrorKind = kind,
                ErrorMessage = message,
                ErrorType = record.ErrorType,
                ErrorContext = context,
            };
        }

        private void ProcessOutcome(NodeOutcome outcome)
        {
            var runtime = outcome.Runtime;
            runtime.Completed = true;
            if (outcome.Success)
            {
                if (runtime.Node.TypeId == NodeTypeCatalog.Confirm && _state == WorkflowRunState.WaitingForUser)
                {
                    SetState(WorkflowRunState.Running, null, null);
                }

                return;
            }

            var node = runtime.Node;
            switch (node.FailurePolicy)
            {
                case NodeFailurePolicy.Continue:
                {
                    Log(
                        WorkflowLogLevel.Warning,
                        "按失败策略继续执行（错误已记录到上游输出）。",
                        node.Id);
                    _upstream[node.Id] = new JsonObject
                    {
                        ["out"] = null,
                        ["error"] = outcome.ErrorContext?.DeepClone(),
                    };
                    ResolveEdges(runtime, null, errorOnly: false, allInactive: false, errorContext: null);
                    break;
                }

                case NodeFailurePolicy.ErrorBranch:
                {
                    Log(WorkflowLogLevel.Warning, "按失败策略转入错误分支。", node.Id);
                    _upstream[node.Id] = new JsonObject
                    {
                        ["out"] = null,
                        ["error"] = outcome.ErrorContext?.DeepClone(),
                    };
                    ResolveEdges(
                        runtime,
                        null,
                        errorOnly: true,
                        allInactive: false,
                        errorContext: outcome.ErrorContext?.DeepClone() as JsonObject);
                    break;
                }

                default:
                {
                    _terminateRequested = true;
                    _errorSummary ??= $"节点“{node.Name}”失败：{outcome.ErrorMessage}";
                    Log(WorkflowLogLevel.Error, "节点失败，正在终止运行。", node.Id);
                    _cts.Cancel();
                    break;
                }
            }
        }

        // ── 连线解析 ─────────────────────────────────────────────

        private void ResolveEdges(
            NodeRuntime runtime,
            IReadOnlyList<string>? firedBranches,
            bool errorOnly,
            bool allInactive,
            JsonObject? errorContext)
        {
            foreach (var edge in _definition.OutgoingEdges(runtime.Node.Id))
            {
                if (!_edges.TryGetValue(edge.Id, out var edgeRuntime) || edgeRuntime.Resolved)
                {
                    continue;
                }

                bool active;
                if (allInactive)
                {
                    active = false;
                }
                else if (errorOnly)
                {
                    active = edge.SourcePort == NodeTypeCatalog.ErrorPort;
                }
                else if (edge.SourcePort == NodeTypeCatalog.ErrorPort)
                {
                    active = false;
                }
                else if (firedBranches is { } branches)
                {
                    active = edge.BranchId is not null && branches.Contains(edge.BranchId);
                }
                else
                {
                    active = edge.BranchId is null;
                }

                edgeRuntime.Resolved = true;
                edgeRuntime.Active = active;
                var target = _nodes[edge.TargetNodeId];
                target.ResolvedIncoming++;
                if (active)
                {
                    target.ActiveIncoming++;
                    if (errorContext is not null && !target.Started)
                    {
                        target.ErrorContext = errorContext.DeepClone() as JsonObject;
                    }
                }

                if (!target.Started && !target.Skipped && target.ResolvedIncoming == target.IncomingCount && target.ActiveIncoming == 0)
                {
                    MarkSkipped(target);
                }
            }
        }

        private void MarkSkipped(NodeRuntime runtime)
        {
            runtime.Skipped = true;
            runtime.Completed = true;
            runtime.Record.State = NodeRunState.Skipped;
            Notify(runtime.Record);
            Log(WorkflowLogLevel.Debug, "已跳过（分支未选中或上游跳过）。", runtime.Node.Id);
            ResolveEdges(runtime, null, errorOnly: false, allInactive: true, errorContext: null);
        }

        // ── 结果与记录 ─────────────────────────────────────────────

        private async Task<WorkflowRunResult> FinishAsync(WorkflowRunState state, string? errorSummary)
        {
            _runTimer.Dispose();
            _stopwatch.Stop();
            _errorSummary ??= errorSummary;

            var closingState = _cancelRequested
                ? (_runTimedOut ? WorkflowRunState.TimedOut : WorkflowRunState.Canceled)
                : state;

            var anyStarted = _nodes.Values.Any(r => r.Started);
            if (anyStarted)
            {
                foreach (var runtime in _nodes.Values)
                {
                    if (runtime.Started || runtime.Completed || runtime.Skipped)
                    {
                        continue;
                    }

                    runtime.Skipped = true;
                    runtime.Completed = true;
                    runtime.Record.State = closingState == WorkflowRunState.Canceled
                        ? NodeRunState.Canceled
                        : NodeRunState.Skipped;
                    Notify(runtime.Record);
                }
            }

            // 运行中断的节点状态收尾（取消/终止时在途节点不会走正常完成路径）。
            foreach (var runtime in _nodes.Values)
            {
                if (!runtime.Started || runtime.Completed)
                {
                    continue;
                }

                runtime.Completed = true;
                runtime.Record.EndedUtc = _time.GetUtcNow();
                if (runtime.Record.State == NodeRunState.Running)
                {
                    runtime.Record.State = closingState is WorkflowRunState.Canceled or WorkflowRunState.TimedOut
                        ? NodeRunState.Canceled
                        : NodeRunState.Failed;
                    if (runtime.Record.State == NodeRunState.Failed)
                    {
                        runtime.Record.ErrorMessage ??= "运行终止时节点被中断。";
                    }

                    Notify(runtime.Record);
                }
            }

            Log(
                closingState switch
                {
                    WorkflowRunState.Succeeded => WorkflowLogLevel.Info,
                    WorkflowRunState.Canceled => WorkflowLogLevel.Warning,
                    _ => WorkflowLogLevel.Error,
                },
                $"运行结束：{closingState}" + (_errorSummary is null ? "。" : $"（{_errorSummary}）"),
                null);

            _state = closingState;
            var record = new WorkflowRunRecord
            {
                RunId = RunId,
                WorkflowId = _definition.Id,
                WorkflowNameSnapshot = _definition.Name,
                VersionLabel = _request.VersionLabel,
                AppVersion = _request.AppVersion,
                StartedUtc = _startedUtc,
                EndedUtc = _time.GetUtcNow(),
                DurationMs = _stopwatch.Elapsed.TotalMilliseconds,
                State = closingState,
                TestMode = _request.TestMode,
                ErrorSummary = closingState is WorkflowRunState.Succeeded ? null : _errorSummary,
                ValidationSummary = _validationSummary,
                FinalVariablesJson = _variables.Count == 0
                    ? null
                    : WorkflowJson.Serialize(_variables, indented: false),
                OutputJson = _output is null ? null : WorkflowJson.Serialize(_output, indented: false),
                ModelContextJson = WorkflowJson.Serialize(new JsonObject
                {
                    ["defaultInstanceId"] = _request.DefaultInstanceId,
                    ["testMode"] = _request.TestMode.ToString(),
                    ["maxParallelism"] = _limits.MaxParallelism,
                }, indented: false),
                DefinitionSnapshotJson = WorkflowJson.Serialize(_definition, indented: false),
                Nodes = anyStarted ? _records.OrderBy(r => r.Sequence).ToList() : new List<NodeRunRecord>(),
                Logs = _logs.ToList(),
            };

            var (inputJson, inputTruncated) = SerializeSnapshot(_inputs, _limits.MaxSnapshotChars);
            record.InputSnapshotJson = inputJson;
            if (inputTruncated)
            {
                record.InputSnapshotJson += "\n（输入快照已截断）";
            }

            _request.Observer?.OnRunStateChanged(closingState, null, _errorSummary);
            await Task.CompletedTask.ConfigureAwait(false);
            return new WorkflowRunResult { Record = record, Output = _output };
        }

        private void SetState(WorkflowRunState state, WorkflowPauseReason? reason = null, string? message = null)
        {
            _state = state;
            _request.Observer?.OnRunStateChanged(state, reason, message);
        }

        private void Notify(NodeRunRecord record)
            => _request.Observer?.OnNodeStateChanged(record);

        private void Log(WorkflowLogLevel level, string message, string? nodeId, string? exceptionText = null)
        {
            lock (_sync)
            {
                if (_logs.Count >= WorkflowLimits.MaxLogEntriesPerRun)
                {
                    if (!_logsCapped)
                    {
                        _logsCapped = true;
                        _logs.Add(new WorkflowLogEntry
                        {
                            Sequence = ++_logSequence,
                            TimestampUtc = _time.GetUtcNow(),
                            Level = WorkflowLogLevel.Warning,
                            Message = $"日志超过上限（{WorkflowLimits.MaxLogEntriesPerRun} 条），后续日志被省略。",
                        });
                    }

                    return;
                }

                var entry = new WorkflowLogEntry
                {
                    Sequence = ++_logSequence,
                    TimestampUtc = _time.GetUtcNow(),
                    Level = level,
                    NodeId = nodeId,
                    Message = message,
                    ExceptionText = exceptionText,
                };
                _logs.Add(entry);
                _request.Observer?.OnLog(entry);
            }
        }

        private static (string? Json, bool Truncated) SerializeSnapshot(JsonNode? node, int maxChars)
        {
            if (node is null)
            {
                return (null, false);
            }

            var json = WorkflowJson.Serialize(node, indented: false);
            return json.Length <= maxChars
                ? (json, false)
                : (json[..maxChars], true);
        }

        // ── 子工作流 ─────────────────────────────────────────────

        public async Task<WorkflowRunResult> RunChildAsync(
            WorkflowDefinition definition,
            JsonObject inputs,
            WorkflowTestMode testMode,
            IReadOnlyDictionary<string, WorkflowMockResponse>? mockResponses,
            string? defaultInstanceId,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            if (_depth + 1 > _limits.MaxSubWorkflowDepth)
            {
                throw new InvalidOperationException(
                    $"子工作流嵌套超过最大深度（{_limits.MaxSubWorkflowDepth} 层），已阻止执行。");
            }

            var childRequest = new WorkflowRunRequest
            {
                Definition = definition,
                Services = _services,
                Inputs = inputs,
                TestMode = testMode,
                MockResponses = mockResponses,
                DefaultInstanceId = defaultInstanceId,
                AppVersion = _request.AppVersion,
                VersionLabel = _request.VersionLabel,
                Limits = new WorkflowExecutionLimits
                {
                    MaxParallelism = _limits.MaxParallelism,
                    DefaultNodeTimeout = _limits.DefaultNodeTimeout,
                    MaxRunDuration = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 86_400)),
                    MaxSnapshotChars = _limits.MaxSnapshotChars,
                    MaxResponseBytes = _limits.MaxResponseBytes,
                    MaxSubWorkflowDepth = _limits.MaxSubWorkflowDepth,
                },
            };
            var childContext = new RunContext(childRequest, _executors, _validator, _logger, _time, _depth + 1);
            return await childContext.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }

        // ── 内部类型 ─────────────────────────────────────────────

        private sealed class NodeRuntime
        {
            public required WorkflowNode Node { get; init; }

            public required NodeRunRecord Record { get; init; }

            public bool Started { get; set; }

            public bool Completed { get; set; }

            public bool Skipped { get; set; }

            public int IncomingCount { get; set; }

            public int ResolvedIncoming { get; set; }

            public int ActiveIncoming { get; set; }

            public JsonObject? ErrorContext { get; set; }
        }

        private sealed class EdgeRuntime
        {
            public required WorkflowEdge Edge { get; init; }

            public bool Resolved { get; set; }

            public bool Active { get; set; }
        }

        private sealed class NodeOutcome
        {
            public required NodeRuntime Runtime { get; init; }

            public bool Success { get; init; }

            public NodeErrorKind ErrorKind { get; init; }

            public string? ErrorMessage { get; init; }

            public string? ErrorType { get; init; }

            public JsonObject? ErrorContext { get; init; }

            public IReadOnlyList<string>? FiredBranches { get; init; }
        }

        private sealed class ExecutionGate
        {
            private readonly object _sync = new();
            private TaskCompletionSource<bool>? _waiter;
            private bool _paused;
            private WorkflowPauseReason? _reason;

            public WorkflowPauseReason? Reason
            {
                get
                {
                    lock (_sync)
                    {
                        return _paused ? _reason : null;
                    }
                }
            }

            public async Task WaitAsync(CancellationToken cancellationToken)
            {
                Task? wait;
                lock (_sync)
                {
                    wait = _paused ? (_waiter ??= CreateWaiter()).Task : null;
                }

                if (wait is not null)
                {
                    await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            public void Pause(WorkflowPauseReason reason)
            {
                lock (_sync)
                {
                    _paused = true;
                    _reason = reason;
                    _waiter ??= CreateWaiter();
                }
            }

            public void Release()
            {
                TaskCompletionSource<bool>? waiter;
                lock (_sync)
                {
                    _paused = false;
                    _reason = null;
                    waiter = _waiter;
                    _waiter = null;
                }

                waiter?.TrySetResult(true);
            }

            private static TaskCompletionSource<bool> CreateWaiter()
                => new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
