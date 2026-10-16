using System.Text.Json.Nodes;
using LlamaLauncher.Domain.Workflows;

namespace LlamaLauncher.Application.Workflows.Executors;

/// <summary>
/// 决策模型调用逻辑（决策节点与循环节点共用）：state/questions 构建、模拟模式、
/// 服务解析、错误映射与诊断信息。真实调用统一走 IModelDecisionClient。
/// </summary>
internal static class DecisionInvocation
{
    /// <summary>执行一次决策调用。</summary>
    public static async Task<NodeExecutionResult> InvokeAsync(
        NodeExecutionContext context,
        DecisionNodeConfig config,
        CancellationToken cancellationToken)
    {
        if (context.TestMode == WorkflowTestMode.Simulated)
        {
            return Simulate(context);
        }

        WorkflowModelTarget? target;
        try
        {
            target = await context.Services.ModelResolver
                .ResolveAsync(config.InstanceId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return NodeExecutionResult.Fail(new NodeExecutionError(
                NodeErrorKind.Failed,
                $"解析模型服务失败：{ex.Message}",
                "ModelResolveFailed"));
        }

        if (target is null)
        {
            return NodeExecutionResult.Fail(new NodeExecutionError(
                NodeErrorKind.Failed,
                config.InstanceId is null
                    ? "没有可用的模型服务实例：请在启动器中启动一个 llama-server 实例，或在节点属性中绑定实例。"
                    : $"绑定的模型服务实例（{config.InstanceNameSnapshot ?? config.InstanceId}）不存在。",
                "ModelUnavailable"));
        }

        if (!target.IsUsable)
        {
            return NodeExecutionResult.Fail(new NodeExecutionError(
                NodeErrorKind.Transient,
                $"模型服务“{target.InstanceName}”当前不可用：{target.UnavailableReason ?? "未就绪"}。请先启动实例并等待其进入“运行中”。",
                "ModelNotReady"));
        }

        if (!TryBuildState(context, config, out var state, out var stateError))
        {
            return NodeExecutionResult.Fail(stateError!);
        }

        if (!TryBuildQuestions(context, config.Questions, out var questions, out var questionError))
        {
            return NodeExecutionResult.Fail(questionError!);
        }

        string? apiKey = null;
        try
        {
            apiKey = await context.Services.ModelResolver
                .GetApiKeyAsync(target.InstanceId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // 未配置 API Key 视为无。
        }

        var request = new Abstractions.DecisionCallRequest
        {
            BaseUrl = target.BaseUrl,
            ApiKey = apiKey,
            State = state!,
            Questions = questions,
            Timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 1, 3600)),
            MaxResponseBytes = Math.Clamp(config.MaxResponseBytes ?? context.Limits.MaxResponseBytes, 1024, 64 * 1024 * 1024),
        };

        Abstractions.DecisionCallResult result;
        try
        {
            result = await context.Services.DecisionClient.CallAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return NodeExecutionResult.Fail(new NodeExecutionError(
                NodeErrorKind.Transient,
                $"决策模型请求异常：{ex.Message}",
                "DecisionClientException"));
        }

        if (!result.Success)
        {
            return MapCallFailure(result, target);
        }

        var answers = result.Response?["answers"] as JsonObject;
        if (answers is null)
        {
            return NodeExecutionResult.Fail(new NodeExecutionError(
                NodeErrorKind.Failed,
                "决策模型响应缺少 answers 字段，无法给出结构化结果。",
                "DecisionInvalidResponse",
                new JsonObject { ["raw"] = result.RawJson }));
        }

        var inputTokens = result.Response?["usage"]?["input_tokens"]?.GetValue<int?>();
        var diagnostics = new JsonObject
        {
            ["elapsedMs"] = Math.Round(result.Elapsed.TotalMilliseconds, 1),
            ["inputTokens"] = inputTokens,
            ["instanceName"] = target.InstanceName,
            ["instanceId"] = target.InstanceId,
            ["baseUrl"] = target.BaseUrl,
        };
        foreach (var pair in target.ContextSnapshot)
        {
            diagnostics[pair.Key] ??= pair.Value?.DeepClone();
        }

        context.Log(
            WorkflowLogLevel.Info,
            $"决策调用完成：{answers.Count} 个问题，耗时 {result.Elapsed.TotalMilliseconds:0} ms" +
            (inputTokens is null ? string.Empty : $"，输入 {inputTokens} tokens") +
            $"（{target.InstanceName}）。",
            null);

        var outputs = new JsonObject
        {
            ["out"] = answers.DeepClone(),
            ["raw"] = JsonValue.Create(result.RawJson),
        };
        return NodeExecutionResult.Ok(outputs, diagnostics: diagnostics);
    }

    /// <summary>配置校验（决策节点与循环模板共用）。</summary>
    public static IReadOnlyList<WorkflowIssue> ValidateConfig(
        WorkflowNode node,
        DecisionNodeConfig config,
        WorkflowValidationContext context,
        string nodeNamePrefix)
    {
        var issues = new List<WorkflowIssue>();
        var name = nodeNamePrefix;

        if (config.Questions.Count == 0)
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-013",
                $"{name}至少需要配置一个问题。",
                node.Id));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in config.Questions)
        {
            var id = question.Id?.Trim() ?? string.Empty;
            if (id.Length == 0)
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"{name}存在空问题 ID。", node.Id));
                continue;
            }

            if (!ids.Add(id))
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"{name}的问题 ID“{id}”重复。", node.Id));
            }

            var type = (question.Type ?? string.Empty).Trim().ToLowerInvariant();
            switch (type)
            {
                case "choice":
                    if (question.Options.Count < 2)
                    {
                        issues.Add(new WorkflowIssue(
                            WorkflowIssueSeverity.Error,
                            "WF-VAL-013",
                            $"{name}的问题“{id}”是 choice 类型，至少需要 2 个选项。",
                            node.Id));
                    }

                    var optionKeys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var option in question.Options)
                    {
                        if (string.IsNullOrWhiteSpace(option.Key) || !optionKeys.Add(option.Key.Trim()))
                        {
                            issues.Add(new WorkflowIssue(
                                WorkflowIssueSeverity.Error,
                                "WF-VAL-013",
                                $"{name}的问题“{id}”存在空选项键或重复键。",
                                node.Id));
                        }

                        AddTemplateIssues(issues, node, option.Description, $"{name}问题“{id}”的选项说明");
                    }

                    break;

                case "score":
                    if (question.Levels.Count is < 2 or > 10)
                    {
                        issues.Add(new WorkflowIssue(
                            WorkflowIssueSeverity.Error,
                            "WF-VAL-013",
                            $"{name}的问题“{id}”是 score 类型，等级数量需在 2–10 之间。",
                            node.Id));
                    }

                    foreach (var level in question.Levels)
                    {
                        AddTemplateIssues(issues, node, level, $"{name}问题“{id}”的等级说明");
                    }

                    break;

                case "noul":
                    AddTemplateIssues(issues, node, question.TrueDescription, $"{name}问题“{id}”的 true 说明");
                    AddTemplateIssues(issues, node, question.FalseDescription, $"{name}问题“{id}”的 false 说明");
                    break;

                default:
                    issues.Add(new WorkflowIssue(
                        WorkflowIssueSeverity.Error,
                        "WF-VAL-013",
                        $"{name}的问题“{id}”类型“{question.Type}”不受支持（可用：choice、score、noul）。",
                        node.Id));
                    break;
            }

            if (string.IsNullOrWhiteSpace(question.Instructions))
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF-VAL-013",
                    $"{name}的问题“{id}”缺少问题说明（instructions）。",
                    node.Id));
            }

            AddTemplateIssues(issues, node, question.Instructions, $"{name}问题“{id}”的说明");
        }

        AddTemplateIssues(issues, node, config.StateTemplate, $"{name}的 state 模板");
        if (!string.IsNullOrWhiteSpace(config.StateJsonTemplate))
        {
            AddTemplateIssues(issues, node, config.StateJsonTemplate, $"{name}的 state JSON 模板");
        }

        if (config.TimeoutSeconds is < 1 or > 3600)
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-013",
                $"{name}的请求超时需在 1–3600 秒之间。",
                node.Id));
        }

        var effectiveNodeTimeout = node.TimeoutSeconds ?? context.Definition.Defaults.DefaultNodeTimeoutSeconds;
        if (config.TimeoutSeconds > effectiveNodeTimeout)
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Warning,
                "WF-VAL-056",
                $"{name}的请求超时（{config.TimeoutSeconds} 秒）大于节点超时（{effectiveNodeTimeout} 秒），请求可能先被节点超时终止。",
                node.Id,
                hint: "把节点超时调大，或减小请求超时。"));
        }

        if (context.Models is { } models)
        {
            if (!string.IsNullOrWhiteSpace(config.InstanceId))
            {
                if (models.All(m => m.InstanceId != config.InstanceId))
                {
                    issues.Add(new WorkflowIssue(
                        WorkflowIssueSeverity.Error,
                        "WF-VAL-014",
                        $"{name}绑定的模型服务实例不存在（{config.InstanceNameSnapshot ?? config.InstanceId}），运行会失败。",
                        node.Id,
                        hint: "在属性面板重新选择实例，或先在启动器中创建并启动实例。"));
                }
                else if (context.ForRun && models.All(m => m.InstanceId != config.InstanceId || !m.IsUsable))
                {
                    issues.Add(new WorkflowIssue(
                        WorkflowIssueSeverity.Warning,
                        "WF-VAL-014",
                        $"{name}绑定的实例当前未就绪，运行到该节点时可能失败。",
                        node.Id));
                }
            }
            else if (context.ForRun && models.All(m => !m.IsUsable))
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Warning,
                    "WF-VAL-014",
                    $"{name}未绑定实例，且当前没有就绪的模型服务。",
                    node.Id));
            }
        }

        if (config.MaxResponseBytes is { } maxBytes && (maxBytes < 1024 || maxBytes > 64 * 1024 * 1024))
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-013",
                $"{name}的响应大小上限需在 1 KB–64 MB 之间。",
                node.Id));
        }

        return issues;
    }

    private static void AddTemplateIssues(
        List<WorkflowIssue> issues,
        WorkflowNode node,
        string? template,
        string purpose)
    {
        if (string.IsNullOrEmpty(template))
        {
            return;
        }

        if (WorkflowTemplate.Validate(template, out _) is { } error)
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-012",
                $"{purpose}的模板无效：{error}",
                node.Id));
        }
    }

    private static NodeExecutionResult Simulate(NodeExecutionContext context)
    {
        if (context.Mock is not { } mock)
        {
            return NodeExecutionResult.Fail(new NodeExecutionError(
                NodeErrorKind.Failed,
                $"模拟模式下未配置节点“{context.Node.Name}”的模拟响应；请在该节点的模拟响应中提供输出（不会调用真实模型）。",
                "SimulationMissing"));
        }

        if (!string.IsNullOrWhiteSpace(mock.ErrorMessage))
        {
            return NodeExecutionResult.Fail(new NodeExecutionError(
                NodeErrorKind.Failed,
                $"模拟响应（按配置失败）：{mock.ErrorMessage}",
                "SimulatedFailure"));
        }

        var outputs = new JsonObject
        {
            ["out"] = mock.Output?.DeepClone(),
            ["raw"] = JsonValue.Create(WorkflowJson.Compact(mock.Output)),
        };
        var branches = string.IsNullOrWhiteSpace(mock.BranchId) ? null : new[] { mock.BranchId! };
        context.Log(WorkflowLogLevel.Info, "模拟模式：使用配置的固定响应（未调用模型）。", null);
        return NodeExecutionResult.Ok(outputs, branches, new JsonObject { ["simulated"] = true });
    }

    private static bool TryBuildState(
        NodeExecutionContext context,
        DecisionNodeConfig config,
        out JsonNode? state,
        out NodeExecutionError? error)
    {
        state = null;
        error = null;

        if (!string.IsNullOrWhiteSpace(config.StateJsonTemplate))
        {
            if (!ExecutionHelpers.TryRenderTemplate(context, config.StateJsonTemplate, "state JSON 模板", out var rendered, out error))
            {
                return false;
            }

            state = WorkflowJson.TryParse(rendered, out var parseError);
            if (state is null)
            {
                error = new NodeExecutionError(
                    NodeErrorKind.Failed,
                    $"state JSON 模板渲染结果不是有效 JSON：{parseError}",
                    "DecisionStateInvalid");
                return false;
            }

            return true;
        }

        if (!string.IsNullOrWhiteSpace(config.StateTemplate))
        {
            if (!ExecutionHelpers.TryRenderTemplate(context, config.StateTemplate, "state 模板", out var text, out error))
            {
                return false;
            }

            state = JsonValue.Create(text);
            return true;
        }

        if (context.Inputs.TryGetPropertyValue("state", out var mapped) && mapped is not null)
        {
            state = mapped.DeepClone();
            return true;
        }

        error = new NodeExecutionError(
            NodeErrorKind.Failed,
            "决策节点未提供 state：请配置 state 模板（如 {{ input.message }}）或把输入映射到“state”字段。",
            "DecisionStateMissing");
        return false;
    }

    private static bool TryBuildQuestions(
        NodeExecutionContext context,
        IReadOnlyList<DecisionQuestion> definitions,
        out JsonObject questions,
        out NodeExecutionError? error)
    {
        questions = new JsonObject();
        error = null;
        if (definitions.Count == 0)
        {
            error = new NodeExecutionError(NodeErrorKind.Failed, "决策节点没有配置任何问题。", "DecisionQuestionsMissing");
            return false;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            var id = definition.Id?.Trim() ?? string.Empty;
            if (id.Length == 0 || !ids.Add(id))
            {
                error = new NodeExecutionError(
                    NodeErrorKind.Failed,
                    $"问题 ID 为空或重复（“{definition.Id}”）。",
                    "DecisionQuestionIdInvalid");
                return false;
            }

            var type = (definition.Type ?? "choice").Trim().ToLowerInvariant();
            if (type is not ("choice" or "score" or "noul"))
            {
                error = new NodeExecutionError(
                    NodeErrorKind.Failed,
                    $"问题“{id}”的类型“{definition.Type}”不受支持（可用：choice、score、noul）。",
                    "DecisionQuestionTypeInvalid");
                return false;
            }

            if (!ExecutionHelpers.TryRenderTemplate(context, definition.Instructions, $"问题“{id}”说明", out var instructions, out error))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(instructions))
            {
                error = new NodeExecutionError(
                    NodeErrorKind.Failed,
                    $"问题“{id}”的说明为空。",
                    "DecisionQuestionInvalid");
                return false;
            }

            var entry = new JsonObject
            {
                ["type"] = type,
                ["instructions"] = instructions,
            };

            switch (type)
            {
                case "choice":
                {
                    if (definition.Options.Count < 2)
                    {
                        error = new NodeExecutionError(
                            NodeErrorKind.Failed,
                            $"问题“{id}”是 choice 类型，至少需要 2 个选项。",
                            "DecisionQuestionInvalid");
                        return false;
                    }

                    var criteria = new JsonObject();
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var option in definition.Options)
                    {
                        var key = option.Key?.Trim() ?? string.Empty;
                        if (key.Length == 0 || !keys.Add(key))
                        {
                            error = new NodeExecutionError(
                                NodeErrorKind.Failed,
                                $"问题“{id}”存在空选项键或重复键。",
                                "DecisionQuestionInvalid");
                            return false;
                        }

                        if (!ExecutionHelpers.TryRenderTemplate(context, option.Description, $"问题“{id}”选项“{key}”说明", out var description, out error))
                        {
                            return false;
                        }

                        criteria[key] = string.IsNullOrWhiteSpace(description) ? null : JsonValue.Create(description);
                    }

                    entry["criteria"] = criteria;
                    break;
                }

                case "score":
                {
                    if (definition.Levels.Count is < 2 or > 10)
                    {
                        error = new NodeExecutionError(
                            NodeErrorKind.Failed,
                            $"问题“{id}”是 score 类型，等级数量需在 2–10 之间。",
                            "DecisionQuestionInvalid");
                        return false;
                    }

                    var levels = new JsonArray();
                    foreach (var level in definition.Levels)
                    {
                        if (!ExecutionHelpers.TryRenderTemplate(context, level, $"问题“{id}”等级说明", out var text, out error))
                        {
                            return false;
                        }

                        levels.Add(JsonValue.Create(text));
                    }

                    entry["criteria"] = levels;
                    break;
                }

                case "noul":
                {
                    if (!string.IsNullOrWhiteSpace(definition.TrueDescription)
                        || !string.IsNullOrWhiteSpace(definition.FalseDescription))
                    {
                        var criteria = new JsonObject();
                        if (!ExecutionHelpers.TryRenderTemplate(context, definition.TrueDescription, $"问题“{id}”true 说明", out var trueText, out error))
                        {
                            return false;
                        }

                        if (!ExecutionHelpers.TryRenderTemplate(context, definition.FalseDescription, $"问题“{id}”false 说明", out var falseText, out error))
                        {
                            return false;
                        }

                        criteria["true"] = string.IsNullOrWhiteSpace(trueText) ? null : JsonValue.Create(trueText);
                        criteria["false"] = string.IsNullOrWhiteSpace(falseText) ? null : JsonValue.Create(falseText);
                        entry["criteria"] = criteria;
                    }

                    break;
                }
            }

            questions[id] = entry;
        }

        return true;
    }

    private static NodeExecutionResult MapCallFailure(Abstractions.DecisionCallResult result, WorkflowModelTarget target)
    {
        var error = result.Error
            ?? new Abstractions.DecisionCallError(Abstractions.DecisionErrorKind.None, "未知错误");
        var kind = error.Kind switch
        {
            Abstractions.DecisionErrorKind.ConnectionFailed => NodeErrorKind.Transient,
            Abstractions.DecisionErrorKind.Timeout => NodeErrorKind.Timeout,
            Abstractions.DecisionErrorKind.Canceled => NodeErrorKind.Canceled,
            Abstractions.DecisionErrorKind.HttpError when error.HttpStatusCode is >= 500 or 429 => NodeErrorKind.Transient,
            _ => NodeErrorKind.Failed,
        };

        var message = error.Kind switch
        {
            Abstractions.DecisionErrorKind.ConnectionFailed =>
                $"无法连接模型服务“{target.InstanceName}”（{target.BaseUrl}）：{error.Message}",
            Abstractions.DecisionErrorKind.Timeout =>
                $"决策请求超时（{target.InstanceName}）：{error.Message}",
            Abstractions.DecisionErrorKind.HttpError when error.HttpStatusCode == 404 =>
                "模型服务不支持 /v1/systemone（HTTP 404）：当前 llama.cpp 版本过旧。请升级到包含决策模型支持的版本（2026-10-02 之后的构建）。",
            Abstractions.DecisionErrorKind.HttpError =>
                $"模型服务返回 HTTP {error.HttpStatusCode}：{error.Message}",
            Abstractions.DecisionErrorKind.ResponseTooLarge =>
                $"模型响应超过大小限制：{error.Message}",
            Abstractions.DecisionErrorKind.InvalidResponse =>
                $"模型响应格式不符：{error.Message}",
            _ => error.Message,
        };

        var context = new JsonObject
        {
            ["kind"] = error.Kind.ToString(),
            ["httpStatus"] = error.HttpStatusCode,
            ["detail"] = error.Detail,
        };
        return NodeExecutionResult.Fail(new NodeExecutionError(kind, message, "DecisionCall" + error.Kind, context));
    }
}
