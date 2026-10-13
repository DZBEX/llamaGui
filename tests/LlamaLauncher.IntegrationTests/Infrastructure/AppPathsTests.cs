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
