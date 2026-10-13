namespace LlamaLauncher.Domain.Validation;

/// <summary>一条参数/配置校验结果。</summary>
public sealed record ValidationIssue(
    ValidationSeverity Severity,
    string Field,
    string Message,
    string? Hint = null)
{
    /// <summary>是否为阻止启动的错误。</summary>
    public bool IsBlocking => Severity == ValidationSeverity.Error;
}
