using LlamaLauncher.Domain.Benchmarks;

namespace LlamaLauncher.Application.Benchmarks;

/// <summary>基准测试运行记录存储（SQLite 实现；摘要查询不加载样本明细）。</summary>
public interface IBenchmarkRunStore
{
    /// <summary>写入或更新一条运行记录（运行中可增量保存）。</summary>
    Task UpsertAsync(BenchmarkRunRecord record, CancellationToken cancellationToken = default);

    /// <summary>按开始时间倒序查询运行摘要。</summary>
    Task<IReadOnlyList<BenchmarkRunSummary>> ListAsync(int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>加载完整运行记录（含配置与样本明细）。</summary>
    Task<BenchmarkRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>删除一条运行记录；返回是否实际删除。</summary>
    Task<bool> DeleteAsync(string runId, CancellationToken cancellationToken = default);
}
