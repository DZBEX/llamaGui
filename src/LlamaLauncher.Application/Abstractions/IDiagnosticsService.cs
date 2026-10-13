namespace LlamaLauncher.Application.Abstractions;

/// <summary>诊断包导出结果。</summary>
public sealed record DiagnosticsResult(bool Success, string? FilePath, string Message);

/// <summary>诊断包导出（日志、设置、安装与实例信息；API Key 等秘密一律脱敏）。</summary>
public interface IDiagnosticsService
{
    /// <summary>
    /// 导出诊断包。includeUserPaths 为 false 时把用户目录替换为占位符。
    /// </summary>
    Task<DiagnosticsResult> ExportBundleAsync(
        string destinationZipPath,
        bool includeUserPaths,
        CancellationToken cancellationToken = default);
}
