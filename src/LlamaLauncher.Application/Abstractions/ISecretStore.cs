namespace LlamaLauncher.Application.Abstractions;

/// <summary>秘密存储（API Key 等）。实现使用 Windows DPAPI；测试使用内存替身。</summary>
public interface ISecretStore
{
    Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default);

    Task<string?> GetAsync(string name, CancellationToken cancellationToken = default);

    Task SetAsync(string name, string value, CancellationToken cancellationToken = default);

    Task DeleteAsync(string name, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken cancellationToken = default);
}

/// <summary>标准秘密名称。</summary>
public static class SecretNames
{
    /// <summary>默认 API Key。</summary>
    public const string DefaultApiKey = "llamalauncher.apiKey";
}
