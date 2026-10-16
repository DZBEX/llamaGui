using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LlamaLauncher.IntegrationTests.TestSupport;

/// <summary>测试用极简 HTTP JSON 服务器：可编程响应/延迟，并捕获收到的原始请求。</summary>
public sealed class LocalJsonHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<string, (int Status, string Body)> _handler;
    private readonly int _delayMs;
    private readonly CancellationTokenSource _cts = new();

    public LocalJsonHttpServer(
        Func<string, (int Status, string Body)> handler,
        int delayMs = 0)
    {
        _handler = handler;
        _delayMs = delayMs;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public List<string> Requests { get; } = new();

    public string Url(string path = "/v1/systemone") => $"http://127.0.0.1:{Port}{path}";

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
                var requestText = await ReadRequestAsync(stream).ConfigureAwait(false);
                lock (Requests)
                {
                    Requests.Add(requestText);
                }

                if (_delayMs > 0)
                {
                    await Task.Delay(_delayMs, _cts.Token).ConfigureAwait(false);
                }

                var (status, body) = _handler(requestText);
                var bodyBytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
                var header =
                    $"HTTP/1.1 {status} {(status == 200 ? "OK" : "ERROR")}\r\n" +
                    "Content-Type: application/json; charset=utf-8\r\n" +
                    $"Content-Length: {bodyBytes.Length}\r\n" +
                    "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header)).ConfigureAwait(false);
                await stream.WriteAsync(bodyBytes).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }

    private static async Task<string> ReadRequestAsync(Stream stream)
    {
        var buffer = new byte[8192];
        using var accumulated = new MemoryStream();
        var headerEnd = -1;
        var contentLength = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            accumulated.Write(buffer, 0, read);
            var text = Encoding.UTF8.GetString(accumulated.GetBuffer(), 0, (int)accumulated.Length);
            headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0)
            {
                continue;
            }

            foreach (var line in text[..headerEnd].Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line["Content-Length:".Length..].Trim(), out var length))
                {
                    contentLength = length;
                }
            }

            var bodyStart = headerEnd + 4;
            if (accumulated.Length - bodyStart >= contentLength)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(accumulated.ToArray());
    }
}
