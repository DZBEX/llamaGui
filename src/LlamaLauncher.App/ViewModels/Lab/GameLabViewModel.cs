using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.App.Services;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.GameTests;
using LlamaLauncher.Application.Workflows;
using LlamaLauncher.Domain.Workflows;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.App.ViewModels.Lab;

/// <summary>游戏测试页中的可选游戏。</summary>
public sealed record ModelGameOption(string Key, string Title, string Description);

/// <summary>一步游戏操作实际由谁决定。</summary>
public enum GameDecisionSource
{
    AiModel,
    Algorithm,
}

/// <summary>棋盘单元格的显示快照。</summary>
public sealed partial class ModelGameCellViewModel : ObservableObject
{
    public ModelGameCellViewModel(int row, int column)
    {
        Row = row;
        Column = column;
    }

    public int Row { get; }

    public int Column { get; }

    [ObservableProperty]
    private Brush _fill = Brushes.Transparent;

    [ObservableProperty]
    private Brush _stroke = Brushes.Transparent;

    [ObservableProperty]
    private Brush _foreground = Brushes.White;

    [ObservableProperty]
    private string _glyph = string.Empty;

    [ObservableProperty]
    private double _glyphSize = 13;

    [ObservableProperty]
    private string _toolTip = string.Empty;

    [ObservableProperty]
    private string _tag = "empty";
}

/// <summary>一次模型决策的完整可追溯记录。</summary>
public sealed class GameDecisionRecord
{
    public required int Turn { get; init; }

    public required string GameName { get; init; }

    public required string TimeText { get; init; }

    public required string Action { get; init; }

    public required string Outcome { get; init; }

    public required bool Success { get; init; }

    public required GameDecisionSource Source { get; init; }

    public required int CandidateCount { get; init; }

    public double? Confidence { get; init; }

    public long ElapsedMilliseconds { get; init; }

    public required string StateJson { get; init; }

    public required string QuestionsJson { get; init; }

    public required string AnswerJson { get; init; }

    public required string RawResponseJson { get; init; }

    public string? ErrorText { get; init; }

    public string ConfidenceText => Confidence is { } value ? value.ToString("P1") : "—";

    public string ElapsedText => $"{ElapsedMilliseconds} ms";

    public string ResultText => Success ? "成功" : "失败";

    public string TurnTimeText =>
        $"{Turn} · {(TimeText.Length >= 5 ? TimeText[..5] : TimeText)}";

    public string SourceShortText => Source switch
    {
        GameDecisionSource.AiModel => $"AI · {CandidateCount}选1",
        _ => "算法 · 唯一",
    };

    public string SourceText => Source switch
    {
        GameDecisionSource.AiModel => $"AI 模型 · {CandidateCount} 选 1",
        _ => "程序算法 · 唯一候选",
    };
}

/// <summary>
/// 决策模型游戏测试：用贪吃蛇、五子棋和俄罗斯方块驱动模型，
/// 保存每一轮发送状态、问题、模型选择、置信度、耗时与动作结果。
/// </summary>
public sealed partial class GameLabViewModel : ViewModelBase, IPageViewModel
{
    private const string PreferredModelName = "Kev-4B-Q4_K_M";

    private static readonly JsonSerializerOptions PrettyOptions = new(WorkflowJson.Options)
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly Dictionary<string, Brush> BrushCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object BrushSync = new();

    private readonly IModelDecisionClient _client;
    private readonly IWorkflowModelResolver _modelResolver;
    private readonly ILogger<GameLabViewModel> _logger;
    private readonly SnakeGame _snake = new();
    private readonly GomokuGame _gomoku = new();
    private readonly TetrisGame _tetris = new();
    private IModelGame _currentGame;
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _decisionCts;
    private TaskCompletionSource<bool>? _humanMoveSignal;
    private int _successfulDecisions;

    public GameLabViewModel(
        IModelDecisionClient client,
        IWorkflowModelResolver modelResolver,
        INotificationService notifications,
        ILogger<GameLabViewModel> logger)
    {
        _client = client;
        _modelResolver = modelResolver;
        _logger = logger;
        Notifications = notifications;
        _currentGame = _snake;
        Games =
        [
            new("snake", "贪吃蛇", "模型每步选择上下左右；撞墙或撞到自己结束。"),
            new("gomoku", "五子棋", "模型执黑 X 对战真人白 O；候选落点按必胜、封堵和棋形动态裁剪。"),
            new("tetris", "俄罗斯方块", "引擎枚举并评分完整落点，模型每次选择一个放置候选。"),
        ];
        SelectedGame = Games[0];
        ResizeBoard();
        RefreshPresentation(resetLog: false);
    }

    public ObservableCollection<ModelGameOption> Games { get; }

    public ObservableCollection<ModelTargetRow> Instances { get; } = new();

    public ObservableCollection<ModelGameCellViewModel> Cells { get; } = new();

    public ObservableCollection<GameDecisionRecord> Decisions { get; } = new();

    [ObservableProperty]
    private ModelGameOption? _selectedGame;

    [ObservableProperty]
    private ModelTargetRow? _selectedInstance;

    [ObservableProperty]
    private int _timeoutSeconds = 120;

    [ObservableProperty]
    private int _decisionIntervalMs = 350;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _isModelThinking;

    [ObservableProperty]
    private bool _isGameOver;

    [ObservableProperty]
    private string _gameStatusText = string.Empty;

    [ObservableProperty]
    private string _gameSecondaryStatusText = string.Empty;

    [ObservableProperty]
    private string _currentStateJson = string.Empty;

    [ObservableProperty]
    private string _currentQuestionsJson = string.Empty;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private GameDecisionRecord? _selectedDecision;

    [ObservableProperty]
    private int _boardRows;

    [ObservableProperty]
    private int _boardColumns;

    [ObservableProperty]
    private double _boardPixelWidth;

    [ObservableProperty]
    private double _boardPixelHeight;

    public string AutoRunButtonText => IsRunning
        ? (IsPaused ? "继续" : "暂停")
        : IsGameOver ? "重新测试" : "自动测试";

    public bool CanStep =>
        !IsRunning
        && !IsModelThinking
        && !IsGameOver
        && _currentGame.IsModelTurn;

    public bool IsBoardInteractive =>
        _currentGame is GomokuGame { IsHumanTurn: true }
        && !IsPaused
        && !IsModelThinking;

    public bool HasDecisions => Decisions.Count > 0;

    public int AiDecisionCount => Decisions.Count(decision => decision.Source == GameDecisionSource.AiModel);

    public int AlgorithmDecisionCount => Decisions.Count(decision => decision.Source == GameDecisionSource.Algorithm);

    public string SuccessfulDecisionText => $"{_successfulDecisions}/{Decisions.Count}";

    public string AverageAiLatencyText
    {
        get
        {
            var latencies = Decisions
                .Where(decision => decision.Source == GameDecisionSource.AiModel)
                .Select(decision => decision.ElapsedMilliseconds)
                .ToArray();
            return latencies.Length == 0 ? "—" : $"{latencies.Average():0} ms";
        }
    }

    public Brush GameAccentBrush => Brush(SelectedGame?.Key switch
    {
        "gomoku" => "#D9B66F",
        "tetris" => "#B477F2",
        _ => "#4C8DFF",
    });

    public string DetailStateJson => SelectedDecision?.StateJson ?? CurrentStateJson;

    public string DetailQuestionsJson => SelectedDecision?.QuestionsJson ?? CurrentQuestionsJson;

    public string DetailAnswerJson =>
        SelectedDecision?.AnswerJson ?? "尚无模型决策；运行或单步后会在这里显示结构化答复。";

    public string DetailRawResponseJson =>
        SelectedDecision?.RawResponseJson ?? "尚无原始响应。";

    public string SessionStatsText
    {
        get
        {
            var aiDecisions = Decisions
                .Where(decision => decision.Source == GameDecisionSource.AiModel)
                .ToArray();
            var algorithmCount = Decisions.Count - aiDecisions.Length;
            var average = aiDecisions.Length == 0
                ? 0
                : aiDecisions.Average(d => d.ElapsedMilliseconds);
            return $"AI 决策 {aiDecisions.Length} 次 · 程序算法 {algorithmCount} 次 · " +
                   $"成功 {_successfulDecisions} 次 · AI 平均 {average:0} ms";
        }
    }

    public string BoardHintText => SelectedGame?.Key switch
    {
        "gomoku" => "X = 模型（黑），O = 你（白）。模型候选已裁至战术相关落点，白棋回合点击空格。",
        "tetris" => "彩色块 = 堆叠/当前方块。模型在规则引擎两层搜索后的完整落点候选中选择。",
        _ => "蓝色 = 蛇身，亮蓝 = 蛇头，红色 = 食物；候选只保留前两个安全动作。",
    };

    public Task OnActivatedAsync() => RefreshAsync();

    partial void OnSelectedGameChanged(ModelGameOption? value)
    {
        if (value is null)
        {
            return;
        }

        StopRunCore();
        _currentGame = value.Key switch
        {
            "gomoku" => _gomoku,
            "tetris" => _tetris,
            _ => _snake,
        };
        Decisions.Clear();
        SelectedDecision = null;
        _successfulDecisions = 0;
        ErrorText = null;
        OnPropertyChanged(nameof(GameAccentBrush));
        ResizeBoard();
        RefreshPresentation(resetLog: true);
    }

    partial void OnIsRunningChanged(bool value) => RefreshComputedState();

    partial void OnIsPausedChanged(bool value)
    {
        RefreshComputedState();
        if (!IsRunning)
        {
            return;
        }

        GameStatusText = value ? "已暂停；点击“继续”恢复模型自动决策。" : _currentGame.StatusText;
    }

    partial void OnIsModelThinkingChanged(bool value) => RefreshComputedState();

    partial void OnIsGameOverChanged(bool value) => RefreshComputedState();

    partial void OnSelectedDecisionChanged(GameDecisionRecord? value)
    {
        OnPropertyChanged(nameof(DetailStateJson));
        OnPropertyChanged(nameof(DetailQuestionsJson));
        OnPropertyChanged(nameof(DetailAnswerJson));
        OnPropertyChanged(nameof(DetailRawResponseJson));
    }

    partial void OnCurrentStateJsonChanged(string value) =>
        OnPropertyChanged(nameof(DetailStateJson));

    partial void OnCurrentQuestionsJsonChanged(string value) =>
        OnPropertyChanged(nameof(DetailQuestionsJson));

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var targets = await _modelResolver.ListAsync().ConfigureAwait(true);
            Instances.Clear();
            foreach (var target in targets)
            {
                Instances.Add(new ModelTargetRow { Target = target });
            }

            var previousId = SelectedInstance?.Target.InstanceId;
            var preferred = Instances.FirstOrDefault(i =>
                    i.IsUsable
                    && i.Target.InstanceName.Contains(PreferredModelName, StringComparison.OrdinalIgnoreCase));
            SelectedInstance = preferred
                ?? Instances.FirstOrDefault(i => i.Target.InstanceId == previousId)
                ?? Instances.FirstOrDefault(i => i.IsUsable)
                ?? Instances.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "刷新游戏测试模型实例失败");
            ErrorText = "刷新模型服务列表失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private void NewGame()
    {
        ResetCurrentGame();
        GameStatusText = "新对局已建立：" + _currentGame.StatusText;
    }

    [RelayCommand]
    private void ToggleRun()
    {
        if (IsRunning)
        {
            IsPaused = !IsPaused;
            return;
        }

        if (IsGameOver)
        {
            ResetCurrentGame();
        }

        if (SelectedInstance is not { } instance)
        {
            ErrorText = "请先选择并启动一个决策模型实例。";
            return;
        }

        if (!instance.IsUsable)
        {
            ErrorText = $"模型实例不可用：{instance.Target.UnavailableReason ?? "未就绪"}。";
            return;
        }

        ErrorText = null;
        IsPaused = false;
        IsRunning = true;
        GameStatusText = "自动测试已启动。";
        var cts = new CancellationTokenSource();
        _runCts = cts;
        _ = RunLoopAsync(cts);
    }

    [RelayCommand]
    private async Task StepAsync()
    {
        if (IsRunning || IsModelThinking || _currentGame.IsGameOver)
        {
            return;
        }

        await ExecuteDecisionOnceAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private void Stop()
    {
        StopRunCore();
        GameStatusText = "已停止自动测试；当前棋盘保留，可单步或建立新局。";
    }

    [RelayCommand]
    private async Task HumanMoveAsync(ModelGameCellViewModel? cell)
    {
        if (cell is null
            || _currentGame is not GomokuGame gomoku
            || !gomoku.IsHumanTurn
            || IsModelThinking
            || IsPaused)
        {
            return;
        }

        if (!gomoku.TryPlayHuman(cell.Row, cell.Column, out var result))
        {
            ErrorText = result;
            return;
        }

        ErrorText = null;
        RefreshPresentation();
        Notifications?.SetStatus(result);
        if (IsRunning)
        {
            _humanMoveSignal?.TrySetResult(true);
        }
        else if (!_currentGame.IsGameOver)
        {
            await ExecuteDecisionOnceAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    private async Task RunLoopAsync(CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested && !_currentGame.IsGameOver)
            {
                await WaitWhilePausedAsync(cts.Token).ConfigureAwait(true);
                if (cts.IsCancellationRequested || _currentGame.IsGameOver)
                {
                    break;
                }

                if (_currentGame is GomokuGame { IsHumanTurn: true })
                {
                    GameStatusText = "等待你落子（白 O）。";
                    var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _humanMoveSignal = signal;
                    try
                    {
                        await signal.Task.WaitAsync(cts.Token).ConfigureAwait(true);
                    }
                    finally
                    {
                        if (ReferenceEquals(_humanMoveSignal, signal))
                        {
                            _humanMoveSignal = null;
                        }
                    }

                    continue;
                }

                var ok = await ExecuteDecisionOnceAsync(cts.Token).ConfigureAwait(true);
                if (!ok || _currentGame.IsGameOver)
                {
                    break;
                }

                if (_currentGame is not GomokuGame)
                {
                    await Task.Delay(
                            Math.Clamp(DecisionIntervalMs, 0, 5_000),
                            cts.Token)
                        .ConfigureAwait(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 用户停止或切换游戏。
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "游戏测试自动运行异常");
            ErrorText = "自动测试异常：" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_runCts, cts))
            {
                _runCts = null;
                IsPaused = false;
                IsRunning = false;
                RefreshPresentation();
            }

            cts.Dispose();
        }
    }

    private async Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        while (IsPaused && !cancellationToken.IsCancellationRequested)
        {
            GameStatusText = "已暂停；点击“继续”恢复模型自动决策。";
            await Task.Delay(100, cancellationToken).ConfigureAwait(true);
        }
    }

    private async Task<bool> ExecuteDecisionOnceAsync(CancellationToken cancellationToken)
    {
        var activeGame = _currentGame;
        if (IsModelThinking || activeGame.IsGameOver)
        {
            return false;
        }

        if (!activeGame.IsModelTurn)
        {
            GameStatusText = "等待你落子（白 O）。";
            return false;
        }

        if (SelectedInstance is not { } instanceRow)
        {
            ErrorText = "请先选择并启动一个决策模型实例。";
            return false;
        }

        if (!instanceRow.IsUsable)
        {
            ErrorText = $"模型实例不可用：{instanceRow.Target.UnavailableReason ?? "未就绪"}。";
            return false;
        }

        using var decisionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _decisionCts = decisionCts;
        var decisionToken = decisionCts.Token;
        var state = activeGame.BuildState();
        var questions = activeGame.BuildQuestions();
        var stateJson = Pretty(state);
        var questionsJson = Pretty(questions);
        var candidateCount = GetCandidateCount(questions);
        CurrentStateJson = stateJson;
        CurrentQuestionsJson = questionsJson;
        ErrorText = null;
        if (candidateCount == 1 && TryGetSoleChoice(questions, out var algorithmChoice))
        {
            try
            {
                return await ExecuteAlgorithmDecisionAsync(
                        activeGame,
                        stateJson,
                        questionsJson,
                        algorithmChoice,
                        decisionToken)
                    .ConfigureAwait(true);
            }
            finally
            {
                if (ReferenceEquals(_decisionCts, decisionCts))
                {
                    _decisionCts = null;
                }
            }
        }

        IsModelThinking = true;
        GameStatusText = "模型正在分析当前状态并做决策…";
        var turn = Decisions.Count + 1;
        var answerJson = string.Empty;
        var rawResponseJson = string.Empty;
        var choice = string.Empty;
        double? confidence = null;
        var elapsed = 0L;
        var success = false;
        var action = "—";
        var outcome = string.Empty;
        string? error = null;

        try
        {
            var apiKey = await _modelResolver
                .GetApiKeyAsync(instanceRow.Target.InstanceId, decisionToken)
                .ConfigureAwait(true);
            var request = new DecisionCallRequest
            {
                BaseUrl = instanceRow.Target.BaseUrl,
                ApiKey = apiKey,
                State = state,
                Questions = questions,
                Timeout = TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 1, 3_600)),
                MaxResponseBytes = 1_048_576,
            };
            var result = await _client.CallAsync(request, decisionToken).ConfigureAwait(true);
            elapsed = (long)Math.Round(result.Elapsed.TotalMilliseconds);
            rawResponseJson = result.RawJson ?? string.Empty;
            if (!result.Success)
            {
                error = result.Error?.Message ?? "模型调用失败。";
                outcome = error;
                answerJson = Pretty(new JsonObject
                {
                    ["error"] = error,
                    ["kind"] = result.Error?.Kind.ToString(),
                    ["httpStatus"] = result.Error?.HttpStatusCode,
                });
                GameStatusText = "模型调用失败；详情已写入决策记录。";
            }
            else if (!ModelGameDecisionReader.TryReadChoice(result.Response, "action", out choice, out confidence)
                     && !ModelGameDecisionReader.TryReadChoice(result.Response, "move", out choice, out confidence))
            {
                error = "模型响应中没有可读取的 choice（action/move）。";
                outcome = error;
                answerJson = Pretty(result.Response?["answers"]?.DeepClone()
                    ?? new JsonObject { ["raw"] = result.RawJson });
                GameStatusText = "模型响应格式不完整；详情已写入决策记录。";
            }
            else
            {
                var answer = new JsonObject
                {
                    ["choice"] = choice,
                    ["confidence"] = confidence,
                    ["answers"] = result.Response?["answers"]?.DeepClone(),
                    ["usage"] = result.Response?["usage"]?.DeepClone(),
                };
                answerJson = Pretty(answer);
                if (activeGame is TetrisGame)
                {
                    await AnimateTetrisPlacementAsync(choice, decisionToken).ConfigureAwait(true);
                }

                success = activeGame.TryApplyModelChoice(choice, out action, out outcome);
                if (!success)
                {
                    error = outcome;
                    GameStatusText = "模型给出了不可执行的动作；详情已写入决策记录。";
                }
            }
        }
        catch (OperationCanceledException) when (decisionToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "游戏测试模型决策失败");
            error = ex.Message;
            outcome = "调用异常：" + ex.Message;
            answerJson = Pretty(new JsonObject { ["error"] = ex.Message });
            GameStatusText = "模型决策异常；详情已写入决策记录。";
        }
        finally
        {
            if (ReferenceEquals(_decisionCts, decisionCts))
            {
                _decisionCts = null;
            }

            IsModelThinking = false;
        }

        var record = new GameDecisionRecord
        {
            Turn = turn,
            GameName = activeGame.DisplayName,
            TimeText = DateTime.Now.ToString("HH:mm:ss"),
            Action = action,
            Outcome = outcome,
            Success = success,
            Source = GameDecisionSource.AiModel,
            CandidateCount = candidateCount,
            Confidence = confidence,
            ElapsedMilliseconds = elapsed,
            StateJson = stateJson,
            QuestionsJson = questionsJson,
            AnswerJson = answerJson,
            RawResponseJson = string.IsNullOrWhiteSpace(rawResponseJson)
                ? Pretty(new JsonObject { ["error"] = error, ["choice"] = choice })
                : rawResponseJson,
            ErrorText = error,
        };
        Decisions.Insert(0, record);
        SelectedDecision = record;
        if (success)
        {
            _successfulDecisions++;
        }

        NotifyDecisionMetrics();
        if (error is not null)
        {
            ErrorText = error;
        }

        RefreshPresentation();
        return success;
    }

    private async Task<bool> ExecuteAlgorithmDecisionAsync(
        IModelGame activeGame,
        string stateJson,
        string questionsJson,
        string choice,
        CancellationToken cancellationToken)
    {
        IsModelThinking = true;
        GameStatusText = "程序算法已确定唯一候选，直接执行。";
        var turn = Decisions.Count + 1;
        var success = false;
        var action = "—";
        var outcome = string.Empty;
        try
        {
            if (activeGame is TetrisGame)
            {
                await AnimateTetrisPlacementAsync(choice, cancellationToken).ConfigureAwait(true);
            }

            success = activeGame.TryApplyModelChoice(choice, out action, out outcome);
        }
        finally
        {
            IsModelThinking = false;
        }

        var decisionJson = Pretty(new JsonObject
        {
            ["source"] = "algorithm",
            ["choice"] = choice,
            ["candidateCount"] = 1,
            ["reason"] = "规则引擎已将候选裁剪为唯一可执行项。",
        });
        var record = new GameDecisionRecord
        {
            Turn = turn,
            GameName = activeGame.DisplayName,
            TimeText = DateTime.Now.ToString("HH:mm:ss"),
            Action = action,
            Outcome = outcome,
            Success = success,
            Source = GameDecisionSource.Algorithm,
            CandidateCount = 1,
            Confidence = null,
            ElapsedMilliseconds = 0,
            StateJson = stateJson,
            QuestionsJson = questionsJson,
            AnswerJson = decisionJson,
            RawResponseJson = decisionJson,
            ErrorText = success ? null : outcome,
        };
        Decisions.Insert(0, record);
        SelectedDecision = record;
        if (success)
        {
            _successfulDecisions++;
        }
        else
        {
            ErrorText = outcome;
        }

        NotifyDecisionMetrics();
        RefreshPresentation();
        return success;
    }

    private static int GetCandidateCount(JsonObject questions)
    {
        foreach (var question in questions.Select(pair => pair.Value).OfType<JsonObject>())
        {
            if (question["criteria"] is JsonObject criteria)
            {
                return criteria.Count;
            }
        }

        return 0;
    }

    private static bool TryGetSoleChoice(JsonObject questions, out string choice)
    {
        choice = string.Empty;
        var choiceQuestions = questions
            .Select(pair => pair.Value)
            .OfType<JsonObject>()
            .Where(question => question["criteria"] is JsonObject)
            .ToArray();
        if (choiceQuestions.Length != 1
            || choiceQuestions[0]["criteria"] is not JsonObject criteria
            || criteria.Count != 1)
        {
            return false;
        }

        choice = criteria.First().Key;
        return !string.IsNullOrWhiteSpace(choice);
    }

    private void ResetCurrentGame()
    {
        StopRunCore();
        _currentGame.Reset();
        Decisions.Clear();
        SelectedDecision = null;
        _successfulDecisions = 0;
        ErrorText = null;
        RefreshPresentation(resetLog: true);
    }

    private void StopRunCore()
    {
        _runCts?.Cancel();
        _decisionCts?.Cancel();
        _humanMoveSignal?.TrySetCanceled();
        _humanMoveSignal = null;
        IsPaused = false;
        IsRunning = false;
    }

    private void ResizeBoard()
    {
        BoardRows = _currentGame.Rows;
        BoardColumns = _currentGame.Columns;
        var cellSize = _currentGame.Key switch
        {
            "gomoku" => 30d,
            "tetris" => 26d,
            _ => 28d,
        };
        BoardPixelWidth = BoardColumns * cellSize;
        BoardPixelHeight = BoardRows * cellSize;
        GlyphCellSize = cellSize;
        Cells.Clear();
        for (var row = 0; row < BoardRows; row++)
        {
            for (var column = 0; column < BoardColumns; column++)
            {
                Cells.Add(new ModelGameCellViewModel(row, column));
            }
        }
    }

    private double GlyphCellSize { get; set; }

    private void RefreshPresentation(bool resetLog = false)
    {
        CurrentStateJson = Pretty(_currentGame.BuildState());
        CurrentQuestionsJson = Pretty(_currentGame.BuildQuestions());
        IsGameOver = _currentGame.IsGameOver;
        GameStatusText = _currentGame.StatusText;
        GameSecondaryStatusText = _currentGame.SecondaryStatusText;
        RefreshBoardCells();
        OnPropertyChanged(nameof(BoardHintText));
        NotifyDecisionMetrics();
        RefreshComputedState();
    }

    private void RefreshBoardCells()
    {
        if (Cells.Count != _currentGame.Rows * _currentGame.Columns)
        {
            ResizeBoard();
        }

        var lookup = _currentGame.GetCells().ToDictionary(c => (c.Row, c.Column));
        foreach (var cell in Cells)
        {
            if (!lookup.TryGetValue((cell.Row, cell.Column), out var snapshot))
            {
                continue;
            }

            var visual = VisualFor(snapshot);
            SetCellVisual(cell, visual, snapshot.Tag, snapshot.ToolTip);
        }
    }

    private async Task AnimateTetrisPlacementAsync(
        string choice,
        CancellationToken cancellationToken)
    {
        if (_currentGame is not TetrisGame tetris
            || !tetris.TryBuildPlacementAnimation(choice, out var animation)
            || animation is null)
        {
            return;
        }

        HashSet<(int Row, int Column)> previous = new();
        foreach (var cell in Cells.Where(c => c.Tag.StartsWith("active-", StringComparison.Ordinal)))
        {
            SetCellVisual(
                cell,
                VisualFor(new ModelGameCell(cell.Row, cell.Column, "empty")),
                "empty",
                $"空位 ({cell.Row}, {cell.Column})");
        }

        for (var index = 0; index < animation.Frames.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var point in previous)
            {
                if (CellAt(point.Row, point.Column) is { } oldCell)
                {
                    SetCellVisual(
                        oldCell,
                        VisualFor(new ModelGameCell(point.Row, point.Column, "empty")),
                        "empty",
                        $"空位 ({point.Row}, {point.Column})");
                }
            }

            var frame = animation.Frames[index];
            previous = frame.Cells.Select(cell => (cell.Row, cell.Column)).ToHashSet();
            var tag = $"active-{animation.PieceType}";
            foreach (var point in previous)
            {
                if (CellAt(point.Row, point.Column) is { } activeCell)
                {
                    SetCellVisual(
                        activeCell,
                        VisualFor(new ModelGameCell(point.Row, point.Column, tag)),
                        tag,
                        $"正在放置 {animation.PlacementId}：({point.Row}, {point.Column})");
                }
            }

            GameStatusText =
                $"{animation.PlacementId}：旋转 {animation.RotationCount * 90}° 后落点预览 " +
                $"{index + 1}/{animation.Frames.Count}";
            await Task.Delay(30, cancellationToken).ConfigureAwait(true);
        }

        await Task.Delay(60, cancellationToken).ConfigureAwait(true);
    }

    private ModelGameCellViewModel? CellAt(int row, int column)
    {
        var index = row * BoardColumns + column;
        return index >= 0 && index < Cells.Count ? Cells[index] : null;
    }

    private static void SetCellVisual(
        ModelGameCellViewModel cell,
        (Brush Fill, Brush Stroke, Brush Foreground, string Glyph, double GlyphSize) visual,
        string tag,
        string toolTip)
    {
        cell.Tag = tag;
        cell.Fill = visual.Fill;
        cell.Stroke = visual.Stroke;
        cell.Foreground = visual.Foreground;
        cell.Glyph = visual.Glyph;
        cell.GlyphSize = visual.GlyphSize;
        cell.ToolTip = toolTip;
    }

    private (Brush Fill, Brush Stroke, Brush Foreground, string Glyph, double GlyphSize) VisualFor(
        ModelGameCell cell)
    {
        if (_currentGame.Key == "gomoku")
        {
            return cell.Tag switch
            {
                "black" => (Brush("#202326"), Brush("#8D7548"), Brush("#F4C96B"), "X", GlyphCellSize * 0.56),
                "black-last" => (Brush("#202326"), Brush("#4C8DFF"), Brush("#F4C96B"), "X", GlyphCellSize * 0.6),
                "black-winning" => (Brush("#202326"), Brush("#FF6B6B"), Brush("#FFD66B"), "X", GlyphCellSize * 0.6),
                "white" => (Brush("#EEE9DD"), Brush("#8D7548"), Brush("#2B2721"), "O", GlyphCellSize * 0.56),
                "white-last" => (Brush("#EEE9DD"), Brush("#4C8DFF"), Brush("#2B2721"), "O", GlyphCellSize * 0.6),
                "white-winning" => (Brush("#EEE9DD"), Brush("#FF6B6B"), Brush("#2B2721"), "O", GlyphCellSize * 0.6),
                _ => (Brush("#D8B878"), Brush("#A88954"), Brush("#2B2721"), string.Empty, 0),
            };
        }

        if (_currentGame.Key == "tetris")
        {
            var active = cell.Tag.StartsWith("active-", StringComparison.Ordinal);
            var type = cell.Tag.Length >= 2 ? cell.Tag[^1] : '.';
            var fill = type switch
            {
                'I' => Brush(active ? "#39D8E8" : "#248E9A"),
                'O' => Brush(active ? "#F2D84B" : "#A79B31"),
                'T' => Brush(active ? "#B477F2" : "#75499F"),
                'S' => Brush(active ? "#59D678" : "#378C4B"),
                'Z' => Brush(active ? "#F26868" : "#A34343"),
                'J' => Brush(active ? "#568CEB" : "#345D9D"),
                'L' => Brush(active ? "#F2A34C" : "#9E6930"),
                _ => Brush("#151A1F"),
            };
            return (
                fill,
                active ? Brush("#EAF2FF") : Brush("#0E1216"),
                Brushes.Transparent,
                string.Empty,
                0);
        }

        return cell.Tag switch
        {
            "snake-head" => (Brush("#4C8DFF"), Brush("#A7C5FF"), Brush("#FFFFFF"), cell.Glyph, 11),
            "snake-body" => (Brush("#2F6FEB"), Brush("#244E9D"), Brush("#FFFFFF"), string.Empty, 0),
            "food" => (Brush("#F25F5C"), Brush("#FFB1AE"), Brush("#FFFFFF"), cell.Glyph, 11),
            _ => (Brush("#171C21"), Brush("#242C34"), Brush("#FFFFFF"), string.Empty, 0),
        };
    }

    private void RefreshComputedState()
    {
        OnPropertyChanged(nameof(AutoRunButtonText));
        OnPropertyChanged(nameof(CanStep));
        OnPropertyChanged(nameof(IsBoardInteractive));
    }

    private void NotifyDecisionMetrics()
    {
        OnPropertyChanged(nameof(HasDecisions));
        OnPropertyChanged(nameof(SessionStatsText));
        OnPropertyChanged(nameof(AiDecisionCount));
        OnPropertyChanged(nameof(AlgorithmDecisionCount));
        OnPropertyChanged(nameof(SuccessfulDecisionText));
        OnPropertyChanged(nameof(AverageAiLatencyText));
    }

    private static string Pretty(JsonNode? node) =>
        node?.ToJsonString(PrettyOptions) ?? "null";

    private static Brush Brush(string hex)
    {
        lock (BrushSync)
        {
            if (BrushCache.TryGetValue(hex, out var cached))
            {
                return cached;
            }

            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            BrushCache[hex] = brush;
            return brush;
        }
    }
}
