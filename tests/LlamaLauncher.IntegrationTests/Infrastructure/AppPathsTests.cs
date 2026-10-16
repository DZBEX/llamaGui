using LlamaLauncher.Infrastructure;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Infrastructure;

/// <summary>数据根目录解析：默认程序根目录、环境变量与引导配置优先级。</summary>
public sealed class AppPathsTests
{
    [Fact]
    public void DefaultDataRoot_IsProgramRoot()
    {
        var programRoot = CreateTempDirectory();
        var original = Environment.GetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable, null);

            var paths = new AppPaths(programRoot);

            Assert.Equal(Path.GetFullPath(programRoot), paths.DataRoot);
            Assert.Null(paths.ResolutionWarning);
            Assert.StartsWith(paths.DataRoot, paths.DatabasePath, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(paths.DataRoot, paths.LogsDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(paths.DataRoot, paths.RuntimesDirectory, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable, original);
            TryDelete(programRoot);
        }
    }

    [Fact]
    public void EnvironmentOverride_Wins()
    {
        var programRoot = CreateTempDirectory();
        var custom = CreateTempDirectory();
        var original = Environment.GetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable, custom);

            var paths = new AppPaths(programRoot);

            Assert.Equal(Path.GetFullPath(custom), paths.DataRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable, original);
            TryDelete(programRoot);
            TryDelete(custom);
        }
    }

    [Fact]
    public void BootstrapFile_OverridesDefault()
    {
        var programRoot = CreateTempDirectory();
        var custom = CreateTempDirectory();
        var original = Environment.GetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable, null);
            AppPaths.WriteBootstrap(programRoot, custom);

            var paths = new AppPaths(programRoot);

            Assert.Equal(Path.GetFullPath(custom), paths.DataRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable, original);
            TryDelete(programRoot);
            TryDelete(custom);
        }
    }

    [Fact]
    public void DirectoryOverrides_ApplyAndReset()
    {
        var programRoot = CreateTempDirectory();
        var customRuntimes = CreateTempDirectory();
        var customDownloads = CreateTempDirectory();
        var original = Environment.GetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable, null);
            var paths = new AppPaths(programRoot);

            var warnings = paths.ApplyDirectoryOverrides(customRuntimes, customDownloads);

            Assert.Empty(warnings);
            Assert.Equal(Path.GetFullPath(customRuntimes), paths.RuntimesDirectory);
            Assert.Equal(Path.GetFullPath(customDownloads), paths.DownloadsDirectory);
            // 默认目录（数据目录下）保持不变，供“恢复默认”与安全校验使用
            Assert.Equal(Path.Combine(paths.DataRoot, "runtimes", "llama.cpp"), paths.DefaultRuntimesDirectory);
            Assert.Equal(Path.Combine(paths.DataRoot, "downloads"), paths.DefaultDownloadsDirectory);

            paths.ApplyDirectoryOverrides(null, null);

            Assert.Equal(paths.DefaultRuntimesDirectory, paths.RuntimesDirectory);
            Assert.Equal(paths.DefaultDownloadsDirectory, paths.DownloadsDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable, original);
            TryDelete(programRoot);
            TryDelete(customRuntimes);
            TryDelete(customDownloads);
        }
    }

    [Fact]
    public void DirectoryOverride_UnusablePath_FallsBackWithWarning()
    {
        var programRoot = CreateTempDirectory();
        var original = Environment.GetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable, null);
            var paths = new AppPaths(programRoot);

            // 用一个“文件”作为父级构造不可用目录：创建必然失败
            var blocker = Path.Combine(programRoot, "blocker-file");
            File.WriteAllText(blocker, "x");
            var unusable = Path.Combine(blocker, "runtimes");

            var warnings = paths.ApplyDirectoryOverrides(unusable, null);

            var warning = Assert.Single(warnings);
            Assert.Contains("已回退", warning);
            Assert.Equal(paths.DefaultRuntimesDirectory, paths.RuntimesDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable, original);
            TryDelete(programRoot);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "LlamaLauncherPaths", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }
}
