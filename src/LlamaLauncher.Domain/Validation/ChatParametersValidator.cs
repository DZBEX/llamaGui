namespace LlamaLauncher.Domain.Validation;

/// <summary>聊天采样参数值域校验。</summary>
public static class ChatParametersValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(ChatParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var issues = new List<ValidationIssue>();

        if (parameters.Temperature is { } temperature && (temperature < 0 || temperature > 5))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, nameof(parameters.Temperature), "temperature 必须在 0-5 之间。"));
        }

        if (parameters.TopP is { } topP && (topP < 0 || topP > 1))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, nameof(parameters.TopP), "top_p 必须在 0-1 之间。"));
        }

        if (parameters.TopK is { } topK && topK < 0)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, nameof(parameters.TopK), "top_k 不能为负数。"));
        }

        if (parameters.MinP is { } minP && (minP < 0 || minP > 1))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, nameof(parameters.MinP), "min_p 必须在 0-1 之间。"));
        }

        if (parameters.RepeatPenalty is { } penalty && (penalty < 0 || penalty > 10))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, nameof(parameters.RepeatPenalty), "repeat_penalty 必须在 0-10 之间。"));
        }

        if (parameters.MaxTokens is { } maxTokens && maxTokens < -1)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, nameof(parameters.MaxTokens), "max tokens 必须为 -1（不限制）或非负整数。"));
        }

        if (parameters.StopSequences.Count > 16)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, nameof(parameters.StopSequences), "停止序列最多 16 条。"));
        }

        foreach (var stop in parameters.StopSequences)
        {
            if (string.IsNullOrEmpty(stop))
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, nameof(parameters.StopSequences), "停止序列不能为空字符串。"));
                break;
            }
        }

        return issues;
    }
}
