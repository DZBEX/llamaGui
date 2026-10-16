using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Workflows;
using LlamaLauncher.App.Services;
using LlamaLauncher.Domain.Workflows;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.App.ViewModels.Lab;

/// <summary>可选模型服务行。</summary>
public sealed class ModelTargetRow
{
    public required WorkflowModelTarget Target { get; init; }

    public string Display => Target.DisplayName;

    public bool IsUsable => Target.IsUsable;
}

/// <summary>
/// 决策模型调试页：单次输入、运行 /v1/systemone、查看原始响应与结构化输出、
/// 校验报告、耗时指标，并把输入保存为测试用例或导出排查文件。
/// </summary>
public sealed partial class ModelDebugViewModel : ViewModelBase, IPageViewModel
{
    private const string DefaultQuestions = """
        {
          "department": {
            "type": "choice",
            "instructions": "Which department should handle this?",
            "criteria": {
              "billing": "invoices, payments, refunds",
              "technical": "bugs, outages, system errors",
              "other": "everything else"
            }
          },
          "urgency": {
            "type": "score",
            "instructions": "How urgent is this?",
            "criteria": ["not urgent", "soon", "blocking"]
          },
          "churn_risk": {
            "type": "noul",
            "instructions": "Does the user threaten to cancel or leave?"
          }
        }
        """;

    private readonly IModelDecisionClient _client;
    private readonly WorkflowRunnerService _runner;
    private readonly IWorkflowDefinitionStore _definitions;
    private readonly IWorkflowTestCaseStore _testCases;
    private readonly ILogger<ModelDebugViewModel> _logger;
    private CancellationTokenSource? _cts;

    public ModelDebugViewModel(
        IModelDecisionClient client,
        WorkflowRunnerService runner,
        IWorkflowDefinitionStore definitions,
        IWorkflowTestCaseStore testCases,
        INotificationService notifications,
        IDialogService dialogs,
        IClipboardService clipboard,
        ILogger<ModelDebugViewModel> logger)
    {
        _client = client;
        _runner = runner;
        _definitions = definitions;
        _testCases = testCases;
        _logger = logger;
        Clipboard = clipboard;
        Notifications = notifications;
        Dialogs = dialogs;
        _stateText = "We were billed twice for March. Please refund the duplicate today or we will cancel our plan.";
        _questionsText = DefaultQuestions;
    }

    private IClipboardService Clipboard { get; }

    public ObservableCollection<ModelTargetRow> Instances { get; } = new();

    public ObservableCollection<AnswerCard> AnswerCards { get; } = new();

    public ObservableCollection<WorkflowDefinitionSummary> WorkflowChoices { get; } = new();

    [ObservableProperty]
    private ModelTargetRow? _selectedInstance;

    [ObservableProperty]
    private WorkflowDefinitionSummary? _selectedWorkflow;

    [ObservableProperty]
    private string _stateText;

    [ObservableProperty]
    private string _questionsText;

    [ObservableProperty]
    private int _timeoutSeconds = 120;

    [ObservableProperty]
    private int _maxResponseKb = 1024;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private string? _errorHint;

    [ObservableProperty]
    private string? _rawResponseText;

    [ObservableProperty]
    private string _metricsText = "尚未运行。";

    [ObservableProperty]
    private string _validationText = "输入状态与问题 JSON 将在运行时校验。";

    [ObservableProperty]
    private bool _validationOk = true;

    public Task OnActivatedAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var targets = await _runner.GetModelTargetsAsync();
            Instances.Clear();
            foreach (var target in targets)
            {
                Instances.Add(new ModelTargetRow { Target = target });
            }

            // 刷新会重建行对象；按实例 ID 重新解析选中项，避免选中旧对象导致下拉框显示为空。
            var previousInstanceId = SelectedInstance?.Target.InstanceId;
            SelectedInstance = Instances.FirstOrDefault(i => i.Target.InstanceId == previousInstanceId)
                ?? Instances.FirstOrDefault(i => i.IsUsable)
                ?? Instances.FirstOrDefault();

            var workflows = await _definitions.ListAsync();
            WorkflowChoices.Clear();
            foreach (var workflow in workflows)
            {
                WorkflowChoices.Add(workflow);
            }

            var previousWorkflowId = SelectedWorkflow?.WorkflowId;
            SelectedWorkflow = WorkflowChoices.FirstOrDefault(w => w.WorkflowId == previousWorkflowId)
                ?? WorkflowChoices.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "刷新模型服务列表失败");
            ErrorText = "刷新模型服务列表失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private void InsertTemplate(string kind)
    {
        var node = WorkflowJson.TryParse(QuestionsText, out _);
        if (node is not JsonObject questions)
        {
            questions = new JsonObject();
        }

        var index = questions.Count + 1;
        switch (kind)
        {
            case "score":
                questions[$"question{index}"] = new JsonObject
                {
                    ["type"] = "score",
                    ["instructions"] = "How urgent is this?",
                    ["criteria"] = new JsonArray("not urgent", "soon", "blocking"),
                };
                break;
            case "noul":
                questions[$"question{index}"] = new JsonObject
                {
                    ["type"] = "noul",
                    ["instructions"] = "Does the user threaten to cancel or leave?",
                };
                break;
            default:
                questions[$"question{index}"] = new JsonObject
                {
                    ["type"] = "choice",
                    ["instructions"] = "Which team should handle this?",
                    ["criteria"] = new JsonObject { ["billing"] = null, ["technical"] = null, ["other"] = null },
                };
                break;
        }

        QuestionsText = WorkflowJson.Serialize(questions);
    }

    [RelayCommand]
    private void FormatInputs()
    {
        if (WorkflowJson.TryParse(QuestionsText, out _) is { } questions)
        {
            QuestionsText = WorkflowJson.Serialize(questions);
        }
    }

    [RelayCommand]
    private void ValidateInputs()
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(StateText))
        {
            issues.Add("状态（state）不能为空。");
        }

        var questions = WorkflowJson.TryParse(QuestionsText, out var parseError);
        if (questions is not JsonObject questionsObject)
        {
            issues.Add($"问题 JSON 无效：{parseError ?? "顶层必须是对象。"}");
        }
        else if (questionsObject.Count == 0)
        {
            issues.Add("至少需要一个问题。");
        }
        else
        {
            foreach (var pair in questionsObject)
            {
                if (pair.Value is not JsonObject question)
                {
                    issues.Add($"问题“{pair.Key}”必须是对象。");
                    continue;
                }

                var type = question["type"]?.GetValue<string>()?.ToLowerInvariant() ?? "choice";
                if (type is not ("choice" or "score" or "noul"))
                {
                    issues.Add($"问题“{pair.Key}”的类型不受支持（{type}）。");
                }

                if (string.IsNullOrWhiteSpace(question["instructions"]?.GetValue<string>()))
                {
                    issues.Add($"问题“{pair.Key}”缺少 instructions。");
                }
            }
        }

        if (issues.Count == 0)
        {
            ValidationOk = true;
            ValidationText = "校验通过：输入格式正确。";
        }
        else
        {
            ValidationOk = false;
            ValidationText = string.Join("\n", issues.Select(i => "• " + i));
        }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        ValidateInputs();
        if (!ValidationOk)
        {
            return;
        }

        if (SelectedInstance is not { } instanceRow)
        {
            ErrorText = "请选择模型服务实例（可在启动器中启动一个 llama-server 实例后刷新）。";
            return;
        }

        if (!instanceRow.IsUsable)
        {
            ErrorText = $"实例不可用：{instanceRow.Target.UnavailableReason ?? "未就绪"}。请先启动实例并等待其进入“运行中”。";
            return;
        }

        var questions = (JsonObject)WorkflowJson.TryParse(QuestionsText, out _)!;
        var state = WorkflowJson.TryParse(StateText, out _) ?? JsonValue.Create(StateText);

        IsRunning = true;
        AnswerCards.Clear();
        ErrorText = null;
        ErrorHint = null;
        RawResponseText = null;
        MetricsText = "运行中…";
        _cts = new CancellationTokenSource();
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            string? apiKey = null;
            var client = _client;
            var request = new DecisionCallRequest
            {
                BaseUrl = instanceRow.Target.BaseUrl,
                ApiKey = apiKey,
                State = state!.DeepClone(),
                Questions = questions.DeepClone() as JsonObject ?? new JsonObject(),
                Timeout = TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 1, 3600)),
                MaxResponseBytes = Math.Clamp(MaxResponseKb, 1, 65_536) * 1024,
            };
            var result = await client.CallAsync(request, _cts.Token).ConfigureAwait(true);

            if (!result.Success)
            {
                ErrorText = result.Error?.Message ?? "调用失败。";
                ErrorHint = result.Error?.Detail;
                MetricsText = $"耗时 {result.Elapsed.TotalMilliseconds:0} ms（未成功）。";
                return;
            }

            RawResponseText = result.RawJson;
            BuildAnswerCards(result.Response);
            var inputTokens = result.Response?["usage"]?["input_tokens"]?.GetValue<int?>();
            MetricsText =
                $"耗时 {result.Elapsed.TotalMilliseconds:0} ms" +
                (inputTokens is null ? string.Empty : $" · 输入 {inputTokens} tokens") +
                $" · 实例 {instanceRow.Target.InstanceName}（{instanceRow.Target.BaseUrl}）";
            Notifications?.SetStatus($"决策调用完成（{result.Elapsed.TotalMilliseconds:0} ms）。");
        }
        catch (OperationCanceledException)
        {
            MetricsText = "已取消。";
            ErrorText = "请求已取消（下层请求可能仍会短暂占用服务）。";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "决策调用失败");
            ErrorText = ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void Reset()
    {
        StateText = string.Empty;
        QuestionsText = DefaultQuestions;
        AnswerCards.Clear();
        RawResponseText = null;
        ErrorText = null;
        ErrorHint = null;
        MetricsText = "尚未运行。";
        ValidationOk = true;
        ValidationText = "输入状态与问题 JSON 将在运行时校验。";
    }

    [RelayCommand]
    private async Task CopyAsync(string what)
    {
        var text = what switch
        {
            "state" => StateText,
            "questions" => QuestionsText,
            "raw" => RawResponseText ?? string.Empty,
            "result" => BuildResultText(),
            _ => string.Empty,
        };
        if (string.IsNullOrEmpty(text))
        {
            Notifications?.Notify("没有可复制的内容。");
            return;
        }

        var (success, error) = await Clipboard.SetTextAsync(text).ConfigureAwait(true);
        Notifications?.Notify(success ? "已复制。" : error ?? "复制失败。");
    }

    private string BuildResultText()
    {
        if (AnswerCards.Count == 0)
        {
            return RawResponseText ?? string.Empty;
        }

        return string.Join(
            "\n",
            AnswerCards.Select(c => $"{c.QuestionId}（{c.Type}）：{c.Summary}" + (c.Confidence is null ? string.Empty : $"（置信度 {c.Confidence:P1}）")));
    }

    [RelayCommand]
    private async Task LoadFromFileAsync()
    {
        var path = Dialogs?.PickOpenFile("加载调试输入", "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*");
        if (path is null)
        {
            return;
        }

        try
        {
            var text = await File.ReadAllTextAsync(path).ConfigureAwait(true);
            var node = WorkflowJson.TryParse(text, out var parseError);
            if (node is not JsonObject root)
            {
                await (Dialogs?.ShowErrorAsync("加载失败", $"文件不是有效 JSON 对象：{parseError}") ?? Task.CompletedTask);
                return;
            }

            StateText = root["state"] is { } stateNode
                ? WorkflowJson.ToDisplayString(stateNode)
                : root["message"]?.GetValue<string>() ?? StateText;
            QuestionsText = root["questions"] is { } questions
                ? WorkflowJson.Serialize(questions)
                : QuestionsText;
            Notifications?.Notify("已加载调试输入。");
        }
        catch (Exception ex)
        {
            await (Dialogs?.ShowErrorAsync("加载失败", ex.Message) ?? Task.CompletedTask);
        }
    }

    [RelayCommand]
    private async Task SaveToFileAsync()
    {
        var path = Dialogs?.PickSaveFile(
            "导出调试输入",
            "JSON 文件 (*.json)|*.json",
            "decision-debug.json");
        if (path is null)
        {
            return;
        }

        try
        {
            var root = new JsonObject
            {
                ["state"] = WorkflowJson.TryParse(StateText, out _) ?? JsonValue.Create(StateText),
                ["questions"] = WorkflowJson.TryParse(QuestionsText, out _),
            };
            await File.WriteAllTextAsync(path, WorkflowJson.Serialize(root)).ConfigureAwait(true);
            Notifications?.Notify($"已导出：{path}");
        }
        catch (Exception ex)
        {
            await (Dialogs?.ShowErrorAsync("导出失败", ex.Message) ?? Task.CompletedTask);
        }
    }

    [RelayCommand]
    private async Task SaveAsTestCaseAsync()
    {
        if (SelectedWorkflow is not { } workflow)
        {
            Notifications?.Notify("请先选择要绑定的工作流（工作流列表为空时可先在设计与调试页保存一个工作流）。");
            return;
        }

        var name = await (Dialogs?.PromptAsync("保存为测试用例", "用例名称：", $"模型调试 {DateTime.Now:HH:mm:ss}") ?? Task.FromResult<string?>(null));
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var testCase = new WorkflowTestCase
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Name = name!,
                Description = "由模型调试页保存（input.state / input.questions）。",
                WorkflowId = workflow.WorkflowId,
                Mode = WorkflowTestMode.LocalModel,
                Inputs = new JsonObject
                {
                    ["state"] = WorkflowJson.TryParse(StateText, out _) ?? JsonValue.Create(StateText),
                    ["questions"] = WorkflowJson.TryParse(QuestionsText, out _),
                },
                Tags = { "模型调试" },
            };
            await _testCases.SaveAsync(testCase).ConfigureAwait(true);
            Notifications?.Notify($"已保存测试用例“{name}”。");
        }
        catch (Exception ex)
        {
            await (Dialogs?.ShowErrorAsync("保存失败", ex.Message) ?? Task.CompletedTask);
        }
    }

    private void BuildAnswerCards(JsonObject? response)
    {
        AnswerCards.Clear();
        if (response?["answers"] is not JsonObject answers)
        {
            return;
        }

        foreach (var pair in answers)
        {
            if (pair.Value is not JsonObject answer)
            {
                continue;
            }

            var type = answer["type"]?.GetValue<string>() ?? "choice";
            var confidence = answer["confidence"]?.GetValue<double?>();
            var bars = new ObservableCollection<AnswerBar>();
            string summary;
            switch (type)
            {
                case "choice":
                    summary = answer["choice"]?.GetValue<string>() ?? "（无）";
                    if (answer["probabilities"] is JsonObject probabilities)
                    {
                        foreach (var probability in probabilities.OrderByDescending(p => p.Value?.GetValue<double>() ?? 0))
                        {
                            var value = probability.Value?.GetValue<double>() ?? 0;
                            bars.Add(new AnswerBar(probability.Key, value * 100, $"{value:P1}"));
                        }
                    }

                    break;

                case "score":
                    summary = answer["score"]?.GetValue<double?>() is { } score ? $"等级 {score:0.##}" : "（无）";
                    if (answer["legend"] is JsonObject legend && answer["probabilities"] is JsonObject scoreProbabilities)
                    {
                        foreach (var probability in scoreProbabilities.OrderBy(p => p.Key, StringComparer.Ordinal))
                        {
                            var value = probability.Value?.GetValue<double>() ?? 0;
                            var label = legend[probability.Key]?.GetValue<string>() ?? probability.Key;
                            bars.Add(new AnswerBar($"{probability.Key} · {label}", value * 100, $"{value:P1}"));
                        }
                    }

                    break;

                case "noul":
                    var noul = answer["noul"]?.GetValue<double?>() ?? 0;
                    summary = noul >= 0.5 ? $"是（{noul:P1}）" : $"否（{1 - noul:P1}）";
                    bars.Add(new AnswerBar("yes", noul * 100, $"{noul:P1}"));
                    bars.Add(new AnswerBar("no", (1 - noul) * 100, $"{1 - noul:P1}"));
                    break;

                default:
                    summary = WorkflowJson.Compact(answer);
                    break;
            }

            AnswerCards.Add(new AnswerCard
            {
                QuestionId = pair.Key,
                Type = type,
                Summary = summary,
                Confidence = confidence,
                Bars = bars,
            });
        }
    }
}
