namespace LlamaLauncher.IntegrationTests.TestSupport;

/// <summary>测试用临时目录（Dispose 时递归删除自身）。</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "LlamaLauncherTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 测试清理失败不影响测试结论。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
