using LlamaLauncher.Domain;
using LlamaLauncher.Domain.Validation;

namespace LlamaLauncher.Application.Abstractions;

/// <summary>配置档案操作失败（面向用户消息）。</summary>
public sealed class LaunchProfileException : Exception
{
    public LaunchProfileException(string userMessage, Exception? inner = null)
        : base(userMessage, inner)
    {
    }
}

/// <summary>配置校验报告。</summary>
public sealed record ProfileValidationReport(IReadOnlyList<ValidationIssue> Issues, string? MaskedCommandLinePreview)
{
    public bool IsValid => !Issues.Any(i => i.IsBlocking);
}

/// <summary>启动配置档案 CRUD 与校验。</summary>
public interface ILaunchProfileService
{
    Task<IReadOnlyList<LaunchProfile>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<LaunchProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<LaunchProfile> CreateAsync(
        string name,
        Guid? modelId,
        Guid? installationId,
        CancellationToken cancellationToken = default);

    /// <summary>保存（校验名称唯一性）；运行中实例不受影响。</summary>
    Task SaveAsync(LaunchProfile profile, CancellationToken cancellationToken = default);

    Task<LaunchProfile> DuplicateAsync(Guid id, string? newName, CancellationToken cancellationToken = default);

    /// <summary>删除配置；被运行中实例引用时阻止。</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task SetDefaultAsync(Guid id, CancellationToken cancellationToken = default);

    Task<LaunchProfile?> GetDefaultAsync(CancellationToken cancellationToken = default);

    /// <summary>值域 + 命令行构建校验（若已选择可执行文件则包含能力校验）。</summary>
    Task<ProfileValidationReport> ValidateAsync(LaunchProfile profile, CancellationToken cancellationToken = default);

    /// <summary>为新建配置建议一个未被占用的端口。</summary>
    Task<int> SuggestPortAsync(CancellationToken cancellationToken = default);
}
