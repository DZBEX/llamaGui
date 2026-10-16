using LlamaLauncher.Application.Abstractions;

namespace LlamaLauncher.IntegrationTests.TestSupport;

/// <summary>指向临时目录的 IAppPaths 测试实现。</summary>
public sealed class TestAppPaths : IAppPaths
{
    public TestAppPaths(string dataRoot) => DataRoot = Path.GetFullPath(dataRoot);

    public string DataRoot { get; }

    public string DatabasePath => Path.Combine(DataRoot, "data", "test.db");

    public string LogsDirectory => Path.Combine(DataRoot, "logs");

    public string CacheDirectory => Path.Combine(DataRoot, "cache");

    public string DefaultRuntimesDirectory => Path.Combine(DataRoot, "runtimes", "llama.cpp");

    public string DefaultDownloadsDirectory => Path.Combine(DataRoot, "downloads");

    public string DownloadsDirectory => _downloadsDirectoryOverride ?? DefaultDownloadsDirectory;

    public string RuntimesDirectory => _runtimesDirectoryOverride ?? DefaultRuntimesDirectory;

    public string ManifestsDirectory => Path.Combine(DataRoot, "manifests");

    public string InstallationsManifestPath => Path.Combine(ManifestsDirectory, "installations.json");

    public string BackupsDirectory => Path.Combine(DataRoot, "backups");

    public string ExportsDirectory => Path.Combine(DataRoot, "exports");

    public string WorkflowsDirectory => Path.Combine(DataRoot, "workflows");

    public string WorkflowSnapshotsDirectory => Path.Combine(WorkflowsDirectory, "snapshots");

    public string WorkflowTestCasesDirectory => Path.Combine(WorkflowsDirectory, "testcases");

    private string? _runtimesDirectoryOverride;

    private string? _downloadsDirectoryOverride;

    public IReadOnlyList<string> ApplyDirectoryOverrides(string? runtimesDirectory, string? downloadsDirectory)
    {
        _runtimesDirectoryOverride = string.IsNullOrWhiteSpace(runtimesDirectory)
            ? null
            : Path.GetFullPath(runtimesDirectory.Trim());
        _downloadsDirectoryOverride = string.IsNullOrWhiteSpace(downloadsDirectory)
            ? null
            : Path.GetFullPath(downloadsDirectory.Trim());
        Directory.CreateDirectory(RuntimesDirectory);
        Directory.CreateDirectory(DownloadsDirectory);
        return Array.Empty<string>();
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(DownloadsDirectory);
        Directory.CreateDirectory(RuntimesDirectory);
        Directory.CreateDirectory(ManifestsDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(ExportsDirectory);
        Directory.CreateDirectory(WorkflowsDirectory);
        Directory.CreateDirectory(WorkflowSnapshotsDirectory);
        Directory.CreateDirectory(WorkflowTestCasesDirectory);
    }
}
