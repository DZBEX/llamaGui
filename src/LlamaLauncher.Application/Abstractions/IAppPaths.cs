namespace LlamaLauncher.Application.Abstractions;

/// <summary>应用的目录布局（默认基于 %AppData%；支持便携模式）。</summary>
public interface IAppPaths
{
    /// <summary>数据根目录（可在设置中切换，切换后需重启生效）。</summary>
    string DataRoot { get; }

    /// <summary>数据库文件路径。</summary>
    string DatabasePath { get; }

    /// <summary>日志目录。</summary>
    string LogsDirectory { get; }

    /// <summary>缓存目录（如 GitHub Releases 元数据缓存）。</summary>
    string CacheDirectory { get; }

    /// <summary>更新临时下载目录。</summary>
    string DownloadsDirectory { get; }

    /// <summary>默认 llama.cpp 版本目录（数据目录下，忽略自定义覆盖）。</summary>
    string DefaultRuntimesDirectory { get; }

    /// <summary>默认更新下载目录（数据目录下，忽略自定义覆盖）。</summary>
    string DefaultDownloadsDirectory { get; }

    /// <summary>llama.cpp 各版本安装目录的父目录。</summary>
    string RuntimesDirectory { get; }

    /// <summary>安装清单目录（installations.json）。</summary>
    string ManifestsDirectory { get; }

    /// <summary>安装清单文件路径。</summary>
    string InstallationsManifestPath { get; }

    /// <summary>备份目录。</summary>
    string BackupsDirectory { get; }

    /// <summary>配置导出目录。</summary>
    string ExportsDirectory { get; }

    /// <summary>决策模型实验室：工作流定义目录。</summary>
    string WorkflowsDirectory { get; }

    /// <summary>工作流版本快照目录。</summary>
    string WorkflowSnapshotsDirectory { get; }

    /// <summary>测试用例目录。</summary>
    string WorkflowTestCasesDirectory { get; }

    /// <summary>确保所有目录存在。</summary>
    void EnsureCreated();

    /// <summary>
    /// 应用目录覆盖（null/空 = 恢复默认）。可写性校验失败时回退默认并返回提示；
    /// 返回值非空表示存在需要告知用户的回退项。
    /// </summary>
    IReadOnlyList<string> ApplyDirectoryOverrides(string? runtimesDirectory, string? downloadsDirectory);
}
