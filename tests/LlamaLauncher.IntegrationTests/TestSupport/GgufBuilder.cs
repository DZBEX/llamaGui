using System.Text;

namespace LlamaLauncher.IntegrationTests.TestSupport;

/// <summary>构造最小 GGUF 文件的测试辅助器（用于元数据解析测试）。</summary>
public sealed class GgufBuilder
{
    private readonly List<(string Key, uint Type, byte[] Data)> _entries = new();
    private uint _version = 3;

    public GgufBuilder WithVersion(uint version)
    {
        _version = version;
        return this;
    }

    public GgufBuilder AddString(string key, string value)
        => AddEntry(key, 8, WriteString(value));

    public GgufBuilder AddUInt32(string key, uint value)
        => AddEntry(key, 4, BitConverter.GetBytes(value));

    public GgufBuilder AddInt32(string key, int value)
        => AddEntry(key, 5, BitConverter.GetBytes(value));

    public GgufBuilder AddUInt64(string key, ulong value)
        => AddEntry(key, 10, BitConverter.GetBytes(value));

    public GgufBuilder AddFloat32(string key, float value)
        => AddEntry(key, 6, BitConverter.GetBytes(value));

    public GgufBuilder AddBool(string key, bool value)
        => AddEntry(key, 7, new[] { value ? (byte)1 : (byte)0 });

    public GgufBuilder AddStringArray(string key, params string[] values)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((uint)8);
        writer.Write((ulong)values.Length);
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.Write((ulong)bytes.Length);
            writer.Write(bytes);
        }

        return AddEntry(key, 9, stream.ToArray());
    }

    public GgufBuilder AddUInt32Array(string key, params uint[] values)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((uint)4);
        writer.Write((ulong)values.Length);
        foreach (var value in values)
        {
            writer.Write(value);
        }

        return AddEntry(key, 9, stream.ToArray());
    }

    public byte[] Build()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("GGUF"));
        writer.Write(_version);
        if (_version == 1)
        {
            writer.Write((uint)0);
            writer.Write((uint)_entries.Count);
        }
        else
        {
            writer.Write((ulong)0);
            writer.Write((ulong)_entries.Count);
        }
        foreach (var (key, type, data) in _entries)
        {
            writer.Write(WriteString(key));
            writer.Write(type);
            writer.Write(data);
        }

        return stream.ToArray();
    }

    private GgufBuilder AddEntry(string key, uint type, byte[] data)
    {
        _entries.Add((key, type, data));
        return this;
    }

    private static byte[] WriteString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ulong)bytes.Length);
        writer.Write(bytes);
        return stream.ToArray();
    }
}
