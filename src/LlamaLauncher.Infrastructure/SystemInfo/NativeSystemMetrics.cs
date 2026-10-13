using System.Runtime.InteropServices;

namespace LlamaLauncher.Infrastructure.SystemInfo;

/// <summary>Win32 系统指标 P/Invoke 封装。</summary>
internal static class NativeSystemMetrics
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public ulong ToUInt64() => ((ulong)HighDateTime << 32) | LowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>系统 CPU 累计时间（idle/kernel/user，单位：100ns）。</summary>
    public static (ulong Idle, ulong Kernel, ulong User)? GetSystemTimes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        return (idle.ToUInt64(), kernel.ToUInt64(), user.ToUInt64());
    }

    /// <summary>物理内存总量与可用量（字节）。</summary>
    public static (long Total, long Available)? GetMemoryStatus()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return null;
        }

        return ((long)status.TotalPhys, (long)status.AvailPhys);
    }
}
