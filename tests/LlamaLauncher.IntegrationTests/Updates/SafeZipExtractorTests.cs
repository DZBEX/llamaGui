using System.IO.Compression;
using System.Text;
using LlamaLauncher.IntegrationTests.TestSupport;
using LlamaLauncher.Infrastructure.Updates;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Updates;

public sealed class SafeZipExtractorTests
{
    private static string CreateZip(
        TempDirectory directory,
        Action<ZipArchive> populate,
        string name = "test.zip")
    {
        var path = directory.Combine(name);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        populate(archive);
        return path;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    [Fact]
    public async Task ExtractsNormalArchiveWithNestedPathsAndUnicode()
    {
        using var directory = new TempDirectory();
        var zipPath = CreateZip(directory, archive =>
        {
            WriteEntry(archive, "llama-server.exe", "fake exe");
            WriteEntry(archive, "sub dir/资源文件.txt", "中文内容");
        });
        var destination = directory.Combine("out");

        var result = await new SafeZipExtractor().ExtractAsync(zipPath, destination);

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(Path.Combine(destination, "llama-server.exe")));
        Assert.True(File.Exists(Path.Combine(destination, "sub dir", "资源文件.txt")));
        Assert.Equal(2, result.ExtractedFiles.Count);
    }

    [Fact]
    public async Task RejectsPathTraversal()
    {
        using var directory = new TempDirectory();
        var zipPath = CreateZip(directory, archive => WriteEntry(archive, "../evil.txt", "boom"));
        var destination = directory.Combine("out");

        var result = await new SafeZipExtractor().ExtractAsync(zipPath, destination);

        Assert.False(result.Success);
        Assert.Contains("穿越", result.Error);
        Assert.False(File.Exists(directory.Combine("evil.txt")));
    }

    [Fact]
    public async Task RejectsAbsolutePaths()
    {
        using var directory = new TempDirectory();
        var zipPath = CreateZip(directory, archive => WriteEntry(archive, "C:/Windows/evil.txt", "boom"));

        var result = await new SafeZipExtractor().ExtractAsync(zipPath, directory.Combine("out"));

        Assert.False(result.Success);
        Assert.Contains("绝对路径", result.Error);
    }

    [Fact]
    public async Task RejectsSymlinkEntries()
    {
        using var directory = new TempDirectory();
        var zipPath = CreateZip(directory, archive =>
        {
            var entry = archive.CreateEntry("link");
            // Unix 符号链接模式（S_IFLNK | 0777）
            entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var writer = new StreamWriter(entry.Open());
            writer.Write("target");
        });

        var result = await new SafeZipExtractor().ExtractAsync(zipPath, directory.Combine("out"));

        Assert.False(result.Success);
        Assert.Contains("符号链接", result.Error);
    }

    [Fact]
    public async Task CorruptedArchive_ReturnsFriendlyError()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("broken.zip");
        await File.WriteAllTextAsync(path, "这不是一个 zip 文件");

        var result = await new SafeZipExtractor().ExtractAsync(path, directory.Combine("out"));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task FailedExtraction_CleansUpDestination()
    {
        using var directory = new TempDirectory();
        var zipPath = CreateZip(directory, archive =>
        {
            WriteEntry(archive, "ok.txt", "fine");
            WriteEntry(archive, "../evil.txt", "boom");
        });
        var destination = directory.Combine("out");

        var result = await new SafeZipExtractor().ExtractAsync(zipPath, destination);

        Assert.False(result.Success);
        Assert.False(Directory.Exists(destination));
    }
}
