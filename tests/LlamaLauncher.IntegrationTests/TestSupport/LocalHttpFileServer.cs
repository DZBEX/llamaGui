using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LlamaLauncher.IntegrationTests.TestSupport;

/// <summary>测试用极简 HTTP 文件服务器（支持慢速分块、截断与 HTML 错误页模拟）。</summary>
public sealed class LocalHttpFileServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly byte[] _body;
    private readonly string _contentType;
    private readonly int _status;
    private readonly int _chunkDelayMs;
    private readonly bool _declareLongerContent;
    private readonly CancellationTokenSource _cts = new();

    public LocalHttpFileServer(
        byte[] body,
        int status = 200,
        string contentType = "application/octet-stream",
        int chunkDelayMs = 0,
        bool declareLongerContent = false)
    {
        _body = body;
        _status = status;
        _contentType = contentType;
        _chunkDelayMs = chunkDelayMs;
        _declareLongerContent = declareLongerContent;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string Url(string path = "/asset.zip") => $"http://127.0.0.1:{Port}{path}";

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (Exception)
        {
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(_cts.Token);
            }
            catch (Exception)
            {
                break;
            }

            _ = Task.Run(() => ServeAsync(socket));
        }
    }

    private async Task ServeAsync(Socket socket)
    {
        using (socket)
        {
            try
            {
                using var stream = new NetworkStream(socket, ownsSocket: false);
                var buffer = new byte[4096];
                var read = await stream.ReadAsync(buffer.AsMemory());
                if (read <= 0)
                {
                    return;
                }

                var declaredLength = _declareLongerContent ? _body.Length + 4096 : _body.Length;
                var header =
                    $"HTTP/1.1 {_status} OK\r\n" +
                    $"Content-Type: {_contentType}\r\n" +
                    $"Content-Length: {declaredLength}\r\n" +
                    "Connection: close\r\n\r\n";
                stream.Write(Encoding.ASCII.GetBytes(header));

                if (_chunkDelayMs > 0)
                {
                    const int chunkSize = 8192;
                    for (var offset = 0; offset < _body.Length; offset += chunkSize)
                    {
                        var size = Math.Min(chunkSize, _body.Length - offset);
                        await stream.WriteAsync(_body.AsMemory(offset, size));
                        await stream.FlushAsync();
                        await Task.Delay(_chunkDelayMs, _cts.Token);
                    }
                }
                else
                {
                    await stream.WriteAsync(_body);
                    await stream.FlushAsync();
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
