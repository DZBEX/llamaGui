using System.Text.Json.Nodes;
using LlamaLauncher.Domain.Workflows;
using LlamaLauncher.Domain.Workflows.Expressions;

namespace LlamaLauncher.Application.Workflows.Executors;

/// <summary>
/// 数据处理节点：受控操作管线（字段选择/重命名、表达式赋值、类型转换、数组过滤/映射/合并、
/// 空值默认、JSON 组装等）。不执行任意脚本；表达式为白名单语法。
/// </summary>
public sealed class TransformNodeExecutor : INodeExecutor
{
    public string TypeId => NodeTypeCatalog.Transform;

    public IReadOnlyList<WorkflowIssue> ValidateConfig(WorkflowNode node, WorkflowValidationContext context)
    {
        var issues = new List<WorkflowIssue>();
        var config = WorkflowJson.TryGetConfig<TransformNodeConfig>(node.Config, out var error);
        if (config is null)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"数据处理节点“{node.Name}”的配置无法解析：{error}", node.Id));
            return issues;
        }

        if (!string.IsNullOrWhiteSpace(config.BaseExpression)
            && WorkflowExpression.Validate(config.BaseExpression) is { } baseError)
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF-VAL-011",
                $"数据处理节点“{node.Name}”的基础输入表达式无效：{baseError}",
                node.Id));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < config.Operations.Count; i++)
        {
            var op = config.Operations[i];
            var label = $"数据处理节点“{node.Name}”的第 {i + 1} 个操作（{op.Kind}）";

            if (string.IsNullOrWhiteSpace(op.Id) || !ids.Add(op.Id))
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"{label}缺少 ID 或 ID 重复。", node.Id));
            }

            var requiresTarget = op.Kind is TransformOperationKind.Remove
                or TransformOperationKind.Rename
                or TransformOperationKind.ToString
                or TransformOperationKind.ToNumber
                or TransformOperationKind.ToBoolean
                or TransformOperationKind.Default
                or TransformOperationKind.Split
                or TransformOperationKind.Join
                or TransformOperationKind.Filter
                or TransformOperationKind.Sort
                or TransformOperationKind.Limit
                or TransformOperationKind.Stringify
                or TransformOperationKind.Template;
            if (requiresTarget && string.IsNullOrWhiteSpace(op.Target))
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"{label}必须指定目标字段。", node.Id));
            }

            if (!string.IsNullOrWhiteSpace(op.Target) && !JsonPathTools.IsValid(op.Target))
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"{label}的目标路径“{op.Target}”无效（支持 a.b[0].c）。", node.Id));
            }

            switch (op.Kind)
            {
                case TransformOperationKind.Assign:
                    if (!string.IsNullOrWhiteSpace(op.Source)
                        && WorkflowExpression.Validate(op.Source) is { } assignError)
                    {
                        issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-011", $"{label}的来源表达式无效：{assignError}", node.Id));
                    }
                    else if (string.IsNullOrWhiteSpace(op.Source) && string.IsNullOrWhiteSpace(op.DefaultValueJson))
                    {
                        issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"{label}需要来源表达式或默认值。", node.Id));
                    }

                    AddPurePathCheck(node, op.Source, $"{label}的来源", context, issues);
                    break;

                case TransformOperationKind.Pick:
                    if (op.Fields.Count == 0)
                    {
                        issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"{label}需要至少一个字段。", node.Id));
                    }

                    if (string.IsNullOrWhiteSpace(op.Source))
                    {
                        issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"{label}需要来源表达式（对象）。", node.Id));
                    }
                    else if (WorkflowExpression.Validate(op.Source) is { } pickError)
                    {
                        issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-011", $"{label}的来源表达式无效：{pickError}", node.Id));
                    }

                    break;

                case TransformOperationKind.Rename:
                    if (string.IsNullOrWhiteSpace(op.Source) || !JsonPathTools.IsValid(op.Source))
                    {
                        issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"{label}需要有效的源字段路径。", node.Id));
                    }

                    break;

                case TransformOperationKind.Default:
                    if (string.IsNullOrWhiteSpace(op.DefaultValueJson))
                    {
                        issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"{label}需要默认值。", node.Id));
                    }
                    else if (WorkflowJson.TryParse(op.DefaultValueJson, out var defaultError) is null)
                    {
                        issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-051", $"{label}的默认值不是有效 JSON：{defaultError}", node.Id));
                    }

                    break;

                case TransformOperationKind.Split:
                case TransformOperationKind.Join:
                case TransformOperationKind.Filter:
                case TransformOperationKind.Sort:
                case TransformOperationKind.Limit:
                case TransformOperationKind.Merge:
                case TransformOperationKind.ParseJson:
                case TransformOperationKind.Stringify:
                    if (string.IsNullOrWhiteSpace(op.Source))
                    {
                        issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"{label}需要来源表达式。", node.Id));
                    }
                    else if (WorkflowExpression.Validate(op.Source) is { } sourceError)
                    {
                        issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-011", $"{label}的来源表达式无效：{sourceError}", node.Id));
                    }

                    if (op.Kind == TransformOperationKind.Filter)
                    {
                        if (string.IsNullOrWhiteSpace(op.PredicateExpression))
                        {
                            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"{label}需要过滤谓词表达式。", node.Id));
                        }
                        else if (WorkflowExpression.Validate(op.PredicateExpression) is { } predicateError)
                        {
                            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-011", $"{label}的过滤谓词无效：{predicateError}", node.Id));
                        }
                    }

                    if (op.Kind == TransformOperationKind.Sort
                        && !string.IsNullOrWhiteSpace(op.KeyExpression)
                        && WorkflowExpression.Validate(op.KeyExpression) is { } keyError)
                    {
                        issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-011", $"{label}的排序键表达式无效：{keyError}", node.Id));
                    }

                    if (op.Kind == TransformOperationKind.Limit && (op.Limit is null || op.Limit < 0))
                    {
                        issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"{label}需要非负的数量限制。", node.Id));
                    }

                    AddPurePathCheck(node, op.Source, $"{label}的来源", context, issues);
                    break;

                case TransformOperationKind.Template:
                    if (string.IsNullOrWhiteSpace(op.TemplateText))
                    {
                        issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"{label}需要模板文本。", node.Id));
                    }
                    else if (WorkflowTemplate.Validate(op.TemplateText, out _) is { } templateError)
                    {
                        issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-012", $"{label}的模板无效：{templateError}", node.Id));
                    }

                    break;

                case TransformOperationKind.BuildObject:
                    if (op.Mappings.Count == 0)
                    {
                        issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-013", $"{label}需要至少一个字段映射。", node.Id));
                    }

                    foreach (var mapping in op.Mappings)
                    {
                        if (string.IsNullOrWhiteSpace(mapping.Source))
                        {
                            continue;
                        }

                        if (WorkflowExpression.Validate(mapping.Source) is { } mappingError)
                        {
                            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, "WF-VAL-011", $"{label}的字段“{mapping.Target}”表达式无效：{mappingError}", node.Id));
                        }
                        else if (WorkflowExpression.TryParse(mapping.Source, out var parsed, out _))
                        {
                            WorkflowValidator.CheckPurePath(context.Definition, node, parsed!.PurePath, $"{label}的字段“{mapping.Target}”", issues);
                        }
                    }

                    break;
            }
        }

        return issues;
    }

    public Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext context)
    {
        if (!ExecutionHelpers.TryGetConfig<TransformNodeConfig>(context, out var config, out var error))
        {
            return Task.FromResult(NodeExecutionResult.Fail(error!));
        }

        JsonObject? document;
        if (!string.IsNullOrWhiteSpace(config!.BaseExpression))
        {
            if (!ExecutionHelpers.TryEvaluate(context, config.BaseExpression, "基础输入", true, out var baseValue, out var baseError))
            {
                return Task.FromResult(NodeExecutionResult.Fail(baseError!));
            }

            document = baseValue as JsonObject;
            if (document is null)
            {
                return Task.FromResult(NodeExecutionResult.Fail(new NodeExecutionError(
                    NodeErrorKind.Failed,
                    $"基础输入表达式的结果不是 JSON 对象（实际为 {WorkflowValueConverter.TypeNameOf(WorkflowValueConverter.FromNode(baseValue))}），无法作为数据处理文档。",
                    "TransformBaseInvalid")));
            }

            document = document.DeepClone() as JsonObject;
        }
        else
        {
            document = context.Inputs.DeepClone() as JsonObject ?? new JsonObject();
        }

        if (document is null)
        {
            return Task.FromResult(NodeExecutionResult.Fail(new NodeExecutionError(
                NodeErrorKind.Failed,
                "数据处理文档为空。",
                "TransformDocumentMissing")));
        }

        var scope = context.Scope.DeepClone() as JsonObject ?? new JsonObject();
        scope["doc"] = document;

        for (var i = 0; i < config.Operations.Count; i++)
        {
            var op = config.Operations[i];
            var label = $"第 {i + 1} 个操作（{op.Kind}）";
            var result = ApplyOperation(context, scope, ref document, op, label);
            if (result is not null)
            {
                return Task.FromResult(result);
            }

            scope["doc"] = document;
        }

        var outputs = new JsonObject { ["out"] = document.DeepClone() };
        return Task.FromResult(NodeExecutionResult.Ok(outputs));
    }

    private static NodeExecutionResult? ApplyOperation(
        NodeExecutionContext context,
        JsonObject scope,
        ref JsonObject document,
        TransformOperation op,
        string label)
    {
        switch (op.Kind)
        {
            case TransformOperationKind.Assign:
            {
                JsonNode? value;
                if (!string.IsNullOrWhiteSpace(op.Source))
                {
                    if (!TryEvaluateInScope(context, scope, op.Source, $"{label}赋值", true, out value, out var evalError))
                    {
                        return evalError;
                    }
                }
                else
                {
                    value = WorkflowJson.TryParse(op.DefaultValueJson, out _);
                }

                if (string.IsNullOrWhiteSpace(op.Target))
                {
                    if (value is not JsonObject replacement)
                    {
                        return Failure(label, "目标为空时赋值结果必须是 JSON 对象（否则无法作为后续文档）。");
                    }

                    document = replacement.DeepClone() as JsonObject ?? new JsonObject();
                    return null;
                }

                if (!JsonPathTools.TrySet(document, op.Target, value, out var setError))
                {
                    return Failure(label, $"写入目标“{op.Target}”失败：{setError}");
                }

                return null;
            }

            case TransformOperationKind.Pick:
            {
                if (!TryEvaluateInScope(context, scope, op.Source, $"{label}来源", true, out var value, out var evalError))
                {
                    return evalError;
                }

                if (value is not JsonObject source)
                {
                    return Failure(label, "选取字段的来源必须是 JSON 对象。");
                }

                var picked = new JsonObject();
                foreach (var field in op.Fields)
                {
                    if (source.TryGetPropertyValue(field, out var fieldValue))
                    {
                        picked[field] = fieldValue?.DeepClone();
                    }
                }

                if (string.IsNullOrWhiteSpace(op.Target))
                {
                    document = picked;
                    return null;
                }

                if (!JsonPathTools.TrySet(document, op.Target, picked, out var setError))
                {
                    return Failure(label, $"写入目标“{op.Target}”失败：{setError}");
                }

                return null;
            }

            case TransformOperationKind.Rename:
            {
                var value = JsonPathTools.Get(document, op.Source!);
                if (value is null && !PathExists(document, op.Source!))
                {
                    return Failure(label, $"源字段“{op.Source}”不存在，无法重命名。");
                }

                JsonPathTools.Remove(document, op.Source!);
                if (!JsonPathTools.TrySet(document, op.Target!, value, out var setError))
                {
                    return Failure(label, $"写入目标“{op.Target}”失败：{setError}");
                }

                return null;
            }

            case TransformOperationKind.Remove:
                JsonPathTools.Remove(document, op.Target!);
                return null;

            case TransformOperationKind.Default:
            {
                var current = JsonPathTools.Get(document, op.Target!);
                if (current is null)
                {
                    var fallback = WorkflowJson.TryParse(op.DefaultValueJson, out _);
                    if (!JsonPathTools.TrySet(document, op.Target!, fallback, out var setError))
                    {
                        return Failure(label, $"写入目标“{op.Target}”失败：{setError}");
                    }
                }

                return null;
            }

            case TransformOperationKind.ToString:
            case TransformOperationKind.ToNumber:
            case TransformOperationKind.ToBoolean:
            {
                if (!PathExists(document, op.Target!))
                {
                    return Failure(label, $"目标字段“{op.Target}”不存在，无法转换。");
                }

                var current = JsonPathTools.Get(document, op.Target!);
                var raw = WorkflowValueConverter.FromNode(current);
                JsonNode? converted;
                switch (op.Kind)
                {
                    case TransformOperationKind.ToString:
                        converted = JsonValue.Create(WorkflowValueConverter.ToDisplayText(raw));
                        break;
                    case TransformOperationKind.ToNumber:
                        if (!WorkflowValueConverter.TryGetNumber(raw, out var number))
                        {
                            return Failure(label, $"无法把 {WorkflowValueConverter.TypeNameOf(raw)} 转换为数值。");
                        }

                        converted = JsonValue.Create(number);
                        break;
                    default:
                        if (!WorkflowValueConverter.TryGetBoolean(raw, out var boolean))
                        {
                            return Failure(label, $"无法把 {WorkflowValueConverter.TypeNameOf(raw)} 转换为布尔值。");
                        }

                        converted = JsonValue.Create(boolean);
                        break;
                }

                if (!JsonPathTools.TrySet(document, op.Target!, converted, out var setError))
                {
                    return Failure(label, $"写入目标“{op.Target}”失败：{setError}");
                }

                return null;
            }

            case TransformOperationKind.Split:
            {
                if (!TryEvaluateInScope(context, scope, op.Source, $"{label}来源", true, out var value, out var evalError))
                {
                    return evalError;
                }

                if (WorkflowValueConverter.FromNode(value) is not string text)
                {
                    return Failure(label, "拆分的来源必须是字符串。");
                }

                var parts = new JsonArray();
                foreach (var part in text.Split(op.Separator ?? ","))
                {
                    parts.Add(JsonValue.Create(part));
                }

                if (!JsonPathTools.TrySet(document, op.Target!, parts, out var setError))
                {
                    return Failure(label, $"写入目标“{op.Target}”失败：{setError}");
                }

                return null;
            }

            case TransformOperationKind.Join:
            {
                if (!TryEvaluateInScope(context, scope, op.Source, $"{label}来源", true, out var value, out var evalError))
                {
                    return evalError;
                }

                if (value is not JsonArray array)
                {
                    return Failure(label, "连接的来源必须是数组。");
                }

                var text = string.Join(
                    op.Separator ?? ",",
                    array.Select(item => WorkflowValueConverter.ToDisplayText(WorkflowValueConverter.FromNode(item))));
                if (!JsonPathTools.TrySet(document, op.Target!, JsonValue.Create(text), out var setError))
                {
                    return Failure(label, $"写入目标“{op.Target}”失败：{setError}");
                }

                return null;
            }

            case TransformOperationKind.Filter:
            {
                if (!TryEvaluateInScope(context, scope, op.Source, $"{label}来源", true, out var value, out var evalError))
                {
                    return evalError;
                }

                if (value is not JsonArray array)
                {
                    return Failure(label, "过滤的来源必须是数组。");
                }

                if (array.Count > WorkflowLimits.MaxCollectionElements)
                {
                    return Failure(label, $"数组元素数量 {array.Count} 超过上限 {WorkflowLimits.MaxCollectionElements}。");
                }

                if (!WorkflowExpression.TryParse(op.PredicateExpression, out var predicate, out var parseError))
                {
                    return Failure(label, $"过滤谓词无效：{parseError}");
                }

                var filtered = new JsonArray();
                for (var index = 0; index < array.Count; index++)
                {
                    var itemScope = scope.DeepClone() as JsonObject ?? new JsonObject();
                    itemScope["item"] = array[index]?.DeepClone();
                    itemScope["index"] = JsonValue.Create((double)index);
                    if (!predicate!.TryEvaluateBool(itemScope, out var keep, out var predicateError))
                    {
                        return Failure(label, $"过滤谓词求值失败（第 {index + 1} 项）：{predicateError}");
                    }

                    if (keep)
                    {
                        filtered.Add(array[index]?.DeepClone());
                    }
                }

                if (!JsonPathTools.TrySet(document, op.Target!, filtered, out var setError))
                {
                    return Failure(label, $"写入目标“{op.Target}”失败：{setError}");
                }

                return null;
            }

            case TransformOperationKind.Sort:
            {
                if (!TryEvaluateInScope(context, scope, op.Source, $"{label}来源", true, out var value, out var evalError))
                {
                    return evalError;
                }

                if (value is not JsonArray array)
                {
                    return Failure(label, "排序的来源必须是数组。");
                }

                if (array.Count > WorkflowLimits.MaxCollectionElements)
                {
                    return Failure(label, $"数组元素数量 {array.Count} 超过上限 {WorkflowLimits.MaxCollectionElements}。");
                }

                WorkflowExpression? keyExpression = null;
                if (!string.IsNullOrWhiteSpace(op.KeyExpression))
                {
                    if (!WorkflowExpression.TryParse(op.KeyExpression, out keyExpression, out var keyError))
                    {
                        return Failure(label, $"排序键表达式无效：{keyError}");
                    }
                }

                var entries = new List<(JsonNode? Item, object? Key)>(array.Count);
                for (var index = 0; index < array.Count; index++)
                {
                    object? key;
                    if (keyExpression is null)
                    {
                        key = WorkflowValueConverter.FromNode(array[index]);
                    }
                    else
                    {
                        var itemScope = scope.DeepClone() as JsonObject ?? new JsonObject();
                        itemScope["item"] = array[index]?.DeepClone();
                        itemScope["index"] = JsonValue.Create((double)index);
                        if (!keyExpression.TryEvaluateToJson(itemScope, out var keyValue, out var keyEvalError))
                        {
                            return Failure(label, $"排序键求值失败（第 {index + 1} 项）：{keyEvalError}");
                        }

                        key = WorkflowValueConverter.FromNode(keyValue);
                    }

                    entries.Add((array[index]?.DeepClone(), key));
                }

                entries.Sort((a, b) => CompareKeys(a.Key, b.Key, op.Descending));
                var sorted = new JsonArray();
                foreach (var entry in entries)
                {
                    sorted.Add(entry.Item?.DeepClone());
                }

                if (!JsonPathTools.TrySet(document, op.Target!, sorted, out var setError))
                {
                    return Failure(label, $"写入目标“{op.Target}”失败：{setError}");
                }

                return null;
            }

            case TransformOperationKind.Limit:
            {
                if (!TryEvaluateInScope(context, scope, op.Source, $"{label}来源", true, out var value, out var evalError))
                {
                    return evalError;
                }

                if (value is not JsonArray array)
                {
                    return Failure(label, "截断的来源必须是数组。");
                }

                var limit = Math.Max(0, op.Limit ?? 0);
                var limited = new JsonArray();
                foreach (var item in array.Take(limit))
                {
                    limited.Add(item?.DeepClone());
                }

                if (!JsonPathTools.TrySet(document, op.Target!, limited, out var setError))
                {
                    return Failure(label, $"写入目标“{op.Target}”失败：{setError}");
                }

                return null;
            }

            case TransformOperationKind.Merge:
            {
                if (!TryEvaluateInScope(context, scope, op.Source, $"{label}来源", true, out var value, out var evalError))
                {
                    return evalError;
                }

                if (value is not JsonObject source)
                {
                    return Failure(label, "合并的来源必须是 JSON 对象。");
                }

                if (string.IsNullOrWhiteSpace(op.Target))
                {
                    foreach (var pair in source)
                    {
                        if (document[pair.Key] is null)
                        {
                            document[pair.Key] = pair.Value?.DeepClone();
                        }
                    }

                    return null;
                }

                var existing = JsonPathTools.Get(document, op.Target);
                var merged = source.DeepClone() as JsonObject ?? new JsonObject();
                if (existing is JsonObject existingObject)
                {
                    foreach (var pair in existingObject)
                    {
                        merged[pair.Key] = pair.Value?.DeepClone();
                    }
                }
                else if (existing is not null)
                {
                    return Failure(label, $"目标“{op.Target}”已存在且不是对象，无法合并。");
                }

                if (!JsonPathTools.TrySet(document, op.Target, merged, out var setError))
                {
                    return Failure(label, $"写入目标“{op.Target}”失败：{setError}");
                }

                return null;
            }

            case TransformOperationKind.ParseJson:
            {
                if (!TryEvaluateInScope(context, scope, op.Source, $"{label}来源", true, out var value, out var evalError))
                {
                    return evalError;
                }

                if (WorkflowValueConverter.FromNode(value) is not string text)
                {
                    return Failure(label, "解析 JSON 的来源必须是字符串。");
                }

                var parsed = WorkflowJson.TryParse(text, out var parseError);
                if (parsed is null && !string.IsNullOrWhiteSpace(text))
                {
                    return Failure(label, $"内容不是有效 JSON：{parseError}");
                }

                if (string.IsNullOrWhiteSpace(op.Target))
                {
                    if (parsed is not JsonObject replacement)
                    {
                        return Failure(label, "目标为空时解析结果必须是 JSON 对象。");
                    }

                    document = replacement.DeepClone() as JsonObject ?? new JsonObject();
                    return null;
                }

                if (!JsonPathTools.TrySet(document, op.Target, parsed, out var setError))
                {
                    return Failure(label, $"写入目标“{op.Target}”失败：{setError}");
                }

                return null;
            }

            case TransformOperationKind.Stringify:
            {
                if (!TryEvaluateInScope(context, scope, op.Source, $"{label}来源", true, out var value, out var evalError))
                {
                    return evalError;
                }

                var text = WorkflowJson.Compact(value);
                if (!JsonPathTools.TrySet(document, op.Target!, JsonValue.Create(text), out var setError))
                {
                    return Failure(label, $"写入目标“{op.Target}”失败：{setError}");
                }

                return null;
            }

            case TransformOperationKind.BuildObject:
            {
                var built = new JsonObject();
                foreach (var mapping in op.Mappings)
                {
                    if (string.IsNullOrWhiteSpace(mapping.Target))
                    {
                        continue;
                    }

                    JsonNode? mappedValue;
                    if (string.IsNullOrWhiteSpace(mapping.Source))
                    {
                        mappedValue = mapping.DefaultValueJson is null
                            ? null
                            : WorkflowJson.TryParse(mapping.DefaultValueJson, out _);
                    }
                    else
                    {
                        if (!TryEvaluateInScope(context, scope, mapping.Source, $"{label}字段“{mapping.Target}”", mapping.Required, out mappedValue, out var mappingError))
                        {
                            return mappingError;
                        }
                    }

                    if (!JsonPathTools.TrySet(built, mapping.Target, mappedValue, out var buildError))
                    {
                        return Failure(label, $"字段“{mapping.Target}”写入失败：{buildError}");
                    }
                }

                if (string.IsNullOrWhiteSpace(op.Target))
                {
                    document = built;
                    return null;
                }

                if (!JsonPathTools.TrySet(document, op.Target, built, out var setError))
                {
                    return Failure(label, $"写入目标“{op.Target}”失败：{setError}");
                }

                return null;
            }

            case TransformOperationKind.Template:
            {
                string rendered;
                try
                {
                    rendered = WorkflowTemplate.Render(op.TemplateText, scope, out var missing);
                    if (missing.Count > 0)
                    {
                        context.Log(
                            WorkflowLogLevel.Warning,
                            $"{label}的模板占位符未取到值（已按空字符串处理）：{string.Join("、", missing)}",
                            null);
                    }
                }
                catch (WorkflowExpressionException ex)
                {
                    return Failure(label, $"模板渲染失败：{ex.Message}");
                }

                if (!JsonPathTools.TrySet(document, op.Target!, JsonValue.Create(rendered), out var setError))
                {
                    return Failure(label, $"写入目标“{op.Target}”失败：{setError}");
                }

                return null;
            }

            default:
                return Failure(label, $"不支持的操作类型“{op.Kind}”。");
        }
    }

    private static bool PathExists(JsonObject document, string path)
    {
        if (!JsonPathTools.TryParse(path, out var steps, out _) || steps.Count == 0)
        {
            return false;
        }

        JsonNode? current = document;
        foreach (var step in steps)
        {
            if (current is null)
            {
                return false;
            }

            if (step.Name is { } name)
            {
                if (current is not JsonObject obj || !obj.TryGetPropertyValue(name, out current))
                {
                    return false;
                }
            }
            else if (step.Index is { } index)
            {
                if (current is not JsonArray array || index < 0 || index >= array.Count)
                {
                    return false;
                }

                current = array[index];
            }
        }

        return true;
    }

    private static bool TryEvaluateInScope(
        NodeExecutionContext context,
        JsonObject scope,
        string? expression,
        string purpose,
        bool requireValue,
        out JsonNode? value,
        out NodeExecutionResult? error)
    {
        value = null;
        error = null;
        if (!WorkflowExpression.TryParse(expression, out var parsed, out var parseError))
        {
            error = Failure(purpose, $"表达式无效：{parseError}");
            return false;
        }

        if (!parsed!.TryEvaluateToJson(scope, out value, out var evalError))
        {
            error = Failure(purpose, $"求值失败：{evalError}");
            return false;
        }

        if (requireValue && value is null)
        {
            error = Failure(purpose, $"求值为空（表达式：{expression}）。");
            return false;
        }

        return true;
    }

    private static int CompareKeys(object? left, object? right, bool descending)
    {
        int result;
        if (left is null && right is null)
        {
            result = 0;
        }
        else if (left is null)
        {
            result = 1;
        }
        else if (right is null)
        {
            result = -1;
        }
        else if (left is double dl && right is double dr)
        {
            result = dl.CompareTo(dr);
        }
        else if (left is string sl && right is string sr)
        {
            result = string.CompareOrdinal(sl, sr);
        }
        else if (left is bool bl && right is bool br)
        {
            result = bl.CompareTo(br);
        }
        else
        {
            result = string.CompareOrdinal(
                WorkflowValueConverter.ToDisplayText(left),
                WorkflowValueConverter.ToDisplayText(right));
        }

        return descending ? -result : result;
    }

    private static NodeExecutionResult Failure(string label, string message)
        => NodeExecutionResult.Fail(new NodeExecutionError(
            NodeErrorKind.Failed,
            $"{label}失败：{message}",
            "TransformOperationFailed"));

    private static void AddPurePathCheck(
        WorkflowNode node,
        string? expression,
        string purpose,
        WorkflowValidationContext context,
        List<WorkflowIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return;
        }

        if (WorkflowExpression.TryParse(expression, out var parsed, out _))
        {
            WorkflowValidator.CheckPurePath(context.Definition, node, parsed!.PurePath, purpose, issues);
        }
    }
}
