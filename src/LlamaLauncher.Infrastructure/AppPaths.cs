using System.Text.Json;
using LlamaLauncher.Application.Abstractions;

namespace LlamaLauncher.Infrastructure;

/// <summary>
/// 应用目录布局。所有应用文件默认保存在程序根目录（exe 所在目录）下：
/// data/（SQLite 数据库）、logs/、downloads/、runtimes/llama.cpp、manifests/、backups/、exports/。
/// 数据根目录解析优先级：
/// 1. 环境变量 LLAMALAUNCHER_DATA_ROOT（测试与高级用户）；
/// 2. exe 旁 LlamaLauncher.bootstrap.json 的 dataRoot 字段；
/// 3. 默认：程序根目录；
/// 4. 程序目录不可写时回退为 %AppData%\LlamaLauncher（见 <see cref="ResolutionWarning"/>）。
/// </summary>
public sealed class AppPaths : IAppPaths
{
    public const string BootstrapFileName = "LlamaLauncher.bootstrap.json";
    public const string DataRootEnvironmentVariable = "LLAMALAUNCHER_DATA_ROOT";

    public AppPaths(string baseDirectory)
    {
        BaseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var (root, warning) = ResolveDataRoot(BaseDirectory);
        DataRoot = Path.TrimEndingDirectorySeparator(root);
        ResolutionWarning = warning;
    }

    public string BaseDirectory { get; }

    public string DataRoot { get; }

    /// <summary>解析过程中的提示（例如程序目录不可写而回退 %AppData%）；无异常时为 null。</summary>
    public string? ResolutionWarning { get; }

    public string DatabasePath => Path.Combine(DataRoot, "data", "llamalauncher.db");

    public string LogsDirectory => Path.Combine(DataRoot, "logs");

    public string DownloadsDirectory => Path.Combine(DataRoot, "downloads");

    public string RuntimesDirectory => Path.Combine(DataRoot, "runtimes", "llama.cpp");

    public string ManifestsDirectory => Path.Combine(DataRoot, "manifests");

    public string InstallationsManifestPath => Path.Combine(ManifestsDirectory, "installations.json");

    public string BackupsDirectory => Path.Combine(DataRoot, "backups");

    public string ExportsDirectory => Path.Combine(DataRoot, "exports");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(DownloadsDirectory);
        Directory.CreateDirectory(RuntimesDirectory);
        Directory.CreateDirectory(ManifestsDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(ExportsDirectory);
    }

    /// <summary>读取引导配置（无效内容按默认处理并返回错误信息）。</summary>
    public static (string? DataRoot, string? Error) ReadBootstrap(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, BootstrapFileName);
        if (!File.Exists(path))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("dataRoot", out var element)
                && element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    var resolved = Path.IsPathRooted(value)
                        ? Path.GetFullPath(value)
                        : Path.GetFullPath(Path.Combine(baseDirectory, value!));
                    return (resolved, null);
                }
            }

            return (null, "引导配置缺少有效的 dataRoot 字段，已改用默认数据目录。");
        }
        catch (Exception ex)
        {
            return (null, $"引导配置无法解析（{ex.Message}），已改用默认数据目录。");
        }
    }

    /// <summary>写入引导配置（dataRoot 为 null 时删除引导文件）。</summary>
    public static void WriteBootstrap(string baseDirectory, string? dataRoot)
    {
        var path = Path.Combine(baseDirectory, BootstrapFileName);
        if (dataRoot is null)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        var json = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["dataRoot"] = Path.GetFullPath(dataRoot) },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    /// <summary>探测目录是否可写（会用临时文件实测，兼容只读安装目录场景）。</summary>
    public static bool IsDirectoryWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".llamalauncher-write-test-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe, 4096, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static (string Root, string? Warning) ResolveDataRoot(string baseDirectory)
    {
        var environmentValue = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return (Path.GetFullPath(environmentValue), null);
        }

        var (configured, bootstrapError) = ReadBootstrap(baseDirectory);
        if (configured is not null)
        {
            return (configured, bootstrapError);
        }

        // 默认：程序根目录（所有文件放到程序目录下）。
        if (IsDirectoryWritable(baseDirectory))
        {
            return (baseDirectory, bootstrapError);
        }

        // 程序目录不可写（例如安装在 Program Files 且无写权限）时才回退。
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var fallback = Path.Combine(appData, "LlamaLauncher");
        return (fallback, "程序目录不可写，已回退为使用 %AppData%\\LlamaLauncher 作为数据目录。");
    }
}
