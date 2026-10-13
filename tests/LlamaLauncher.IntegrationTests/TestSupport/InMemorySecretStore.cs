using System.Collections.Concurrent;
using LlamaLauncher.Application.Abstractions;

namespace LlamaLauncher.IntegrationTests.TestSupport;

/// <summary>测试用内存秘密存储。</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default)
        => Task.FromResult(_secrets.ContainsKey(name));

    public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
        => Task.FromResult(_secrets.TryGetValue(name, out var value) ? value : null);

    public Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        _secrets[name] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        _secrets.TryRemove(name, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(_secrets.Keys.OrderBy(k => k).ToList());
}
