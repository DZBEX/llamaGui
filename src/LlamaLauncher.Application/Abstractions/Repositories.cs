using LlamaLauncher.Domain;

namespace LlamaLauncher.Application.Abstractions;

/// <summary>模型记录仓储。</summary>
public interface IModelRepository
{
    Task<IReadOnlyList<ModelRecord>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<ModelRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ModelRecord?> FindByCanonicalPathAsync(string canonicalPath, CancellationToken cancellationToken = default);

    Task UpsertAsync(ModelRecord model, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>启动配置档案仓储。</summary>
public interface IProfileRepository
{
    Task<IReadOnlyList<LaunchProfile>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<LaunchProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<LaunchProfile?> FindByNameAsync(string name, CancellationToken cancellationToken = default);

    Task UpsertAsync(LaunchProfile profile, CancellationToken cancellationToken = default);

    /// <summary>在单个事务中批量写入档案；若批次内包含默认档案，会清除其他默认标记。</summary>
    Task UpsertManyAsync(IReadOnlyList<LaunchProfile> profiles, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>将指定档案设为唯一默认（事务内清除其他默认标记）。</summary>
    Task SetDefaultAsync(Guid id, CancellationToken cancellationToken = default);

    Task<LaunchProfile?> GetDefaultAsync(CancellationToken cancellationToken = default);
}

/// <summary>llama.cpp 安装记录仓储。</summary>
public interface IInstallationRepository
{
    Task<IReadOnlyList<LlamaCppInstallation>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<LlamaCppInstallation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<LlamaCppInstallation?> FindByExecutablePathAsync(string executablePath, CancellationToken cancellationToken = default);

    Task UpsertAsync(LlamaCppInstallation installation, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task SetDefaultAsync(Guid id, CancellationToken cancellationToken = default);

    Task<LlamaCppInstallation?> GetDefaultAsync(CancellationToken cancellationToken = default);
}

/// <summary>服务实例（最近状态快照）仓储。</summary>
public interface IInstanceRepository
{
    Task<IReadOnlyList<ServerInstance>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<ServerInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task UpsertAsync(ServerInstance instance, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>启动历史查询过滤条件。</summary>
public sealed class HistoryQuery
{
    public Guid? InstanceId { get; init; }

    public int Limit { get; init; } = 200;
}

/// <summary>启动历史仓储。</summary>
public interface IStartupHistoryRepository
{
    Task<IReadOnlyList<StartupHistoryEntry>> QueryAsync(HistoryQuery query, CancellationToken cancellationToken = default);

    Task<StartupHistoryEntry?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task AddAsync(StartupHistoryEntry entry, CancellationToken cancellationToken = default);

    Task UpdateAsync(StartupHistoryEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>聊天会话仓储。</summary>
public interface IConversationRepository
{
    Task<IReadOnlyList<Conversation>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Conversation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task UpsertAsync(Conversation conversation, CancellationToken cancellationToken = default);

    /// <summary>删除会话及其全部消息（事务）。</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>聊天消息仓储。</summary>
public interface IMessageRepository
{
    Task<IReadOnlyList<ChatMessage>> GetByConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<ChatMessage?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task AddAsync(ChatMessage message, CancellationToken cancellationToken = default);

    /// <summary>更新消息内容/状态/指标（生成过程中的增量保存）。</summary>
    Task UpdateAsync(ChatMessage message, CancellationToken cancellationToken = default);

    /// <summary>删除单条消息（重新生成时清理尾消息 / 用户删除单条消息）。</summary>
    Task DeleteAsync(Guid messageId, CancellationToken cancellationToken = default);

    Task DeleteByConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>返回会话内下一个顺序号。</summary>
    Task<long> GetNextSequenceAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>将仍处于 Streaming/Pending 的消息标记为 Interrupted（应用启动恢复）。</summary>
    Task<int> MarkStreamingAsInterruptedAsync(DateTimeOffset completedUtc, CancellationToken cancellationToken = default);
}

/// <summary>更新操作审计仓储。</summary>
public interface IUpdateOperationRepository
{
    Task AddAsync(UpdateOperation operation, CancellationToken cancellationToken = default);

    Task UpdateAsync(UpdateOperation operation, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UpdateOperation>> GetRecentAsync(int limit, CancellationToken cancellationToken = default);
}

/// <summary>设置键值仓储。</summary>
public interface ISettingsRepository
{
    Task<AppSetting?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AppSetting>> GetAllAsync(CancellationToken cancellationToken = default);

    Task SetAsync(string key, string valueJson, DateTimeOffset updatedUtc, CancellationToken cancellationToken = default);

    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}
