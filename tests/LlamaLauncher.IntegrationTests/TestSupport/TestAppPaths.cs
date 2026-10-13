using LlamaLauncher.Application.Abstractions;

namespace LlamaLauncher.IntegrationTests.TestSupport;

/// <summary>指向临时目录的 IAppPaths 测试实现。</summary>
public sealed class TestAppPaths : IAppPaths
{
    public TestAppPaths(string dataRoot) => DataRoot = Path.GetFullPath(dataRoot);

    public string DataRoot { get; }

    public string DatabasePath => Path.Combine(DataRoot, "data", "test.db");

    public string LogsDirectory => Path.Combine(DataRoot, "logs");

    public string DownloadsDirectory => Path.Combine(DataRoot, "downloads");

    public string RuntimesDirectory => Path.Combine(DataRoot, "runtimes", "llama.cpp");

    public string ManifestsDirectory => Path.Combine(DataRoot, "manifests");

    public string InstallationsManifestPath => Path.Combine(ManifestsDirectory, "installations.json");

    public string BackupsDirectory => Path.Combine(DataRoot, "backups");

    public string ExportsDirectory => Path.Combine(DataRoot, "exports");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(DownloadsDirectory);
        Directory.CreateDirectory(RuntimesDirectory);
        Directory.CreateDirectory(ManifestsDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(ExportsDirectory);
    }
}
