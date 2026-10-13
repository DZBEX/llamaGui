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

    /// <summary>更新临时下载目录。</summary>
    string DownloadsDirectory { get; }

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

    /// <summary>确保所有目录存在。</summary>
    void EnsureCreated();
}
