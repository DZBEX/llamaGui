using System.Net;
using System.Net.Sockets;
using LlamaLauncher.Application.Abstractions;

namespace LlamaLauncher.Infrastructure.Net;

/// <summary>端口预检查：通过绑定测试判断可用性，绝不占用他人端口。</summary>
public sealed class TcpPortProbe : IPortProbeService
{
    public Task<PortCheckResult> CheckAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () =>
            {
                if (port is < 1 or > 65535)
                {
                    return PortCheckResult.InUse($"端口 {port} 超出有效范围。");
                }

                var address = ResolveBindAddress(host);
                var listener = new TcpListener(address, port);
                try
                {
                    listener.Start();
                    return PortCheckResult.Available();
                }
                catch (SocketException ex)
                {
                    return PortCheckResult.InUse(
                        $"端口 {port} 已被占用或无法绑定（{ex.SocketErrorCode}，错误 {ex.ErrorCode}）。");
                }
                finally
                {
                    try
                    {
                        listener.Stop();
                    }
                    catch (Exception)
                    {
                    }
                }
            },
            cancellationToken);
    }

    private static IPAddress ResolveBindAddress(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        if (IPAddress.TryParse(host, out var address))
        {
            return address;
        }

        // 主机名（非 localhost）：按所有接口测试端口整体占用情况。
        return IPAddress.Any;
    }
}
