using System.Drawing;
using System.IO;
using Xunit;

namespace LlamaLauncher.UiTests;

/// <summary>
/// 应用图标资源（Assets/LlamaLauncher.ico）：exe（ApplicationIcon）、窗口与托盘共用同一份；
/// 这里校验资源已嵌入、尺寸齐全且各条目为 PNG 压缩（Windows 10/11 与 Inno Setup 6 均支持）。
/// </summary>
public sealed class AppIconTests
{
    private static Stream OpenIconResource()
    {
        // 在 WPF 宿主之外（如测试宿主）构造 pack URI 前，先触发 pack 方案解析器注册
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
            typeof(System.IO.Packaging.PackUriHelper).TypeHandle);

        var assemblyName = typeof(LlamaLauncher.App.App).Assembly.GetName().Name;
        var uri = new Uri(
            $"pack://application:,,,/{assemblyName};component/Assets/LlamaLauncher.ico",
            UriKind.Absolute);
        var resource = System.Windows.Application.GetResourceStream(uri);
        Assert.NotNull(resource);
        return resource!.Stream;
    }

    [Fact]
    public void IconResource_IsEmbeddedWithStandardSizes()
    {
        using var stream = OpenIconResource();
        using var reader = new BinaryReader(stream);

        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        var count = reader.ReadUInt16();
        Assert.True(count >= 4, "图标应包含多个尺寸条目。");

        var sizes = new List<int>();
        var offsets = new List<long>();
        for (var i = 0; i < count; i++)
        {
            var width = reader.ReadByte();
            var height = reader.ReadByte();
            reader.ReadByte(); // 调色板颜色数
            reader.ReadByte(); // 保留
            Assert.Equal(1, reader.ReadUInt16());   // 色彩平面
            Assert.Equal(32, reader.ReadUInt16());  // 位深
            var bytes = reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            Assert.True(bytes > 0);
            offsets.Add(offset);

            var resolvedWidth = width == 0 ? 256 : width;
            var resolvedHeight = height == 0 ? 256 : height;
            Assert.Equal(resolvedWidth, resolvedHeight);
            sizes.Add(resolvedWidth);
        }

        Assert.Equal(new[] { 16, 24, 32, 48, 64, 128, 256 }, sizes);

        // 每个条目都应是 PNG（ICO 内嵌 PNG；Windows Vista+ 支持）
        foreach (var offset in offsets)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            var signature = reader.ReadBytes(4);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, signature);
        }
    }

    [Fact]
    public void IconResource_LoadsForTrayIconSizes()
    {
        foreach (var size in new[] { 16, 32 })
        {
            using var stream = OpenIconResource();
            using var icon = new Icon(stream, new Size(size, size));
            Assert.Equal(size, icon.Width);
            Assert.Equal(size, icon.Height);
        }
    }
}
