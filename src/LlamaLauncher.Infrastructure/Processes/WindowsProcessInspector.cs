using System.Diagnostics;
using LlamaLauncher.Application.Abstractions;

namespace LlamaLauncher.Infrastructure.Processes;

/// <summary>通过 PID + 启动时间 + 可执行文件路径三重校验识别进程身份，避免 PID 重用误判与误杀。</summary>
public sealed class WindowsProcessInspector : IProcessInspector
{
    public ProcessIdentity Inspect(int pid)
    {
        if (pid <= 0)
        {
            return new ProcessIdentity(pid, null, null, false);
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            DateTimeOffset? startTime = null;
            string? executablePath = null;

            try
            {
                startTime = new DateTimeOffset(process.StartTime.ToUniversalTime());
            }
            catch (Exception)
            {
                // 权限受限时无法读取
            }

            try
            {
                executablePath = process.MainModule?.FileName;
            }
            catch (Exception)
            {
                // 32/64 位或权限限制
            }

            bool exited;
            try
            {
                exited = process.HasExited;
            }
            catch (Exception)
            {
                exited = false;
            }

            return new ProcessIdentity(pid, startTime, executablePath, !exited);
        }
        catch (ArgumentException)
        {
            return new ProcessIdentity(pid, null, null, false);
        }
        catch (InvalidOperationException)
        {
            return new ProcessIdentity(pid, null, null, false);
        }
    }

    public bool Matches(
        int pid,
        string executablePath,
        DateTimeOffset expectedStartTimeUtc,
        TimeSpan startTimeTolerance,
        out ProcessIdentity identity)
    {
        identity = Inspect(pid);
        if (!identity.Exists)
        {
            return false;
        }

        // 无法读取启动时间与路径的进程一律不接管（宁可不接管，也不误杀）。
        if (identity.StartTimeUtc is null && identity.ExecutablePath is null)
        {
            return false;
        }

        if (identity.StartTimeUtc is { } actual
            && Math.Abs((actual - expectedStartTimeUtc).TotalSeconds) > startTimeTolerance.TotalSeconds)
        {
            return false;
        }

        if (identity.ExecutablePath is { } path && !PathsEqual(path, executablePath))
        {
            return false;
        }

        return true;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd('\\'),
                Path.GetFullPath(right).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
