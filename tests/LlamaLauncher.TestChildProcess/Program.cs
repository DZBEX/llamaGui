// 集成测试专用的受控子进程。它不是产品代码，仅用于验证进程管理：
// 标准输出/错误采集、退出码、命令行参数传递、取消与强杀行为。
//
// 用法示例：
//   LlamaLauncher.TestChildProcess --echo-args
//   LlamaLauncher.TestChildProcess --stdout 5000 --stderr 5000
//   LlamaLauncher.TestChildProcess --exit-after-ms 500 --exit-code 3
//   LlamaLauncher.TestChildProcess --marker <file>
// 运行时也可通过标准输入命令控制：exit / crash / print <text>。

using System.Globalization;
using LlamaLauncher.TestChildProcess;

var argsMap = ParseArgs(args);

// 统一 UTF-8 输出，避免父进程按 UTF-8 读取时出现编码错乱。
try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch (Exception)
{
    // 无控制台句柄时设置可能失败，忽略即可。
}

const string DefaultHelpText = """
usage: llama-server [options]

----- common params -----
  -h,    --help, --usage                  print usage and exit
         --version                        show version and build info
  -m,    --model <FILE>                   model to load
  -c,    --ctx-size <N>                   size of the prompt context (default: 4096, 0 = loaded from model)
  -t,    --threads <N>                    number of threads to use during generation
  -tb,   --threads-batch <N>              number of threads to use during batch and prompt processing
  -b,    --batch-size <N>                 logical maximum batch size (default: 2048)
  -ub,   --ubatch-size <N>                physical maximum batch size (default: 512)
  -ngl,  --n-gpu-layers <N>               max number of layers to store in VRAM
  -dev,  --device <dev1,dev2,..>          comma-separated list of devices to use
  -fa,   --flash-attn [on|off|auto]       set Flash Attention use ('on', 'off', or 'auto', default: 'auto')
         --no-flash-attn                  disable Flash Attention (deprecated)
  -ctk,  --cache-type-k <TYPE>            KV cache data type for K (default: f16)
  -ctv,  --cache-type-v <TYPE>            KV cache data type for V (default: f16)
  -sm,   --split-mode <none|layer|row>    how to split the model across multiple GPUs
  -ts,   --tensor-split <ts0/ts1/..>      fraction of the model to offload to each GPU
  -mg,   --main-gpu <I>                   the GPU to use for the model (default: 0)
  -np,   --parallel <N>                   number of parallel sequences to decode (default: 1)
  -a,    --alias <ALIAS>                  set an alias for the model
         --host <HOST>                    ip address to listen (default: 127.0.0.1)
         --port <PORT>                    port to listen (default: 8080)
         --api-key <KEY>                  API key to use for authentication
         --no-mmap                        do not memory-map model
         --cont-batching                  enable continuous batching (default: enabled)
  -v,    --verbose                        verbose logging
""";

if (argsMap.ContainsKey("echo-args"))
{
    for (var i = 0; i < args.Length; i++)
    {
        Console.Out.WriteLine($"ARG[{i}]={args[i]}");
    }
}

if (argsMap.TryGetValue("write-pid", out var pidPath) && pidPath is not null)
{
    File.WriteAllText(pidPath, Environment.ProcessId.ToString());
}

if (argsMap.TryGetValue("echo-stdout", out var echoOut) && echoOut is not null)
{
    Console.Out.WriteLine(echoOut);
    Console.Out.Flush();
}

if (argsMap.TryGetValue("echo-stderr", out var echoErr) && echoErr is not null)
{
    Console.Error.WriteLine(echoErr);
    Console.Error.Flush();
}

if (GetInt(argsMap, "sleep-ms", 0) is var startupSleepMs && startupSleepMs > 0)
{
    Thread.Sleep(startupSleepMs);
}

// 探测模式：模拟 llama-server 的 --version / --help / --list-devices 行为并立即退出。
if (args.Contains("--version"))
{
    ProbeVersion();
}

if (args.Contains("--help"))
{
    ProbeHelp();
}

if (args.Contains("--list-devices"))
{
    ProbeDevices();
}

// 模拟 llama-server 的 HTTP 接口（集成测试用）。
if (Environment.GetEnvironmentVariable("LLAMALAUNCHER_TEST_ENABLE_HTTP") == "1")
{
    var httpPort = GetInt(argsMap, "port", 8080);
    var httpHost = argsMap.TryGetValue("host", out var hostValue) && hostValue is not null ? hostValue : "127.0.0.1";
    MockHttpServer.Start(httpHost, httpPort);
}

var stdoutLines = GetInt(argsMap, "stdout", 0);
var stderrLines = GetInt(argsMap, "stderr", 0);
var intervalMs = GetInt(argsMap, "interval-ms", 1);
var exitAfterMs = GetInt(argsMap, "exit-after-ms", 0);
var exitCode = GetInt(argsMap, "exit-code", 0);
var markerPath = argsMap.TryGetValue("marker", out var marker) ? marker : null;
var crashAfterMs = GetInt(argsMap, "crash-after-ms", 0);
var noStdoutFlush = argsMap.ContainsKey("no-flush");

var markerWritten = 0;
void WriteMarker(string reason)
{
    if (markerPath is null || Interlocked.Exchange(ref markerWritten, 1) == 1)
    {
        return;
    }

    try
    {
        File.AppendAllText(markerPath, $"{DateTime.UtcNow:O} {reason}{Environment.NewLine}");
    }
    catch
    {
        // 测试辅助进程，忽略标记写入失败。
    }
}

AppDomain.CurrentDomain.ProcessExit += (_, _) => WriteMarker("ProcessExit");

Console.Out.WriteLine("CHILD_READY");
Console.Out.Flush();

var lifetimeCts = new CancellationTokenSource();

if (exitAfterMs > 0)
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(exitAfterMs);
        WriteMarker("exit-after");
        Environment.Exit(exitCode);
    });
}

if (crashAfterMs > 0)
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(crashAfterMs);
        WriteMarker("crash-after");
        Environment.FailFast("模拟崩溃退出");
    });
}

// 标准输出压力输出（验证读取端不会因缓冲区写满而死锁）。
var stdoutTask = Task.Run(() =>
{
    for (var i = 0; i < stdoutLines && !lifetimeCts.IsCancellationRequested; i++)
    {
        Console.Out.WriteLine($"OUT {i:D6} 模拟日志行 " + new string('x', 60));
        if (!noStdoutFlush && i % 200 == 0)
        {
            Console.Out.Flush();
        }

        if (intervalMs > 0)
        {
            Thread.Sleep(intervalMs);
        }
    }

    Console.Out.WriteLine("STDOUT_DONE");
    Console.Out.Flush();
});

var stderrTask = Task.Run(() =>
{
    for (var i = 0; i < stderrLines && !lifetimeCts.IsCancellationRequested; i++)
    {
        Console.Error.WriteLine($"ERR {i:D6} 模拟错误输出 " + new string('y', 60));
        if (intervalMs > 0)
        {
            Thread.Sleep(intervalMs);
        }
    }

    Console.Error.WriteLine("STDERR_DONE");
    Console.Error.Flush();
});

// 标准输入命令循环；输入关闭（EOF）后保持运行，直到收到命令或自动退出。
var stdinTask = Task.Run(() =>
{
    string? line;
    while ((line = Console.In.ReadLine()) is not null)
    {
        var trimmed = line.Trim();
        if (trimmed.Equals("exit", StringComparison.OrdinalIgnoreCase))
        {
            WriteMarker("stdin-exit");
            Environment.Exit(exitCode);
        }
        else if (trimmed.Equals("crash", StringComparison.OrdinalIgnoreCase))
        {
            WriteMarker("stdin-crash");
            Environment.FailFast("模拟标准输入崩溃");
        }
        else if (trimmed.StartsWith("print ", StringComparison.OrdinalIgnoreCase))
        {
            Console.Out.WriteLine("PRINT " + trimmed[6..]);
            Console.Out.Flush();
        }
    }
});

Console.Out.WriteLine("CHILD_WAITING");
Console.Out.Flush();

Task.WaitAll(stdoutTask, stderrTask, stdinTask);

// 若没有 stdin 命令也没有自动退出参数，则挂起等待被终止。
if (argsMap.ContainsKey("exit-when-done"))
{
    WriteMarker("done");
    Environment.Exit(exitCode);
}

Thread.Sleep(Timeout.Infinite);

static Dictionary<string, string?> ParseArgs(string[] rawArgs)
{
    var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < rawArgs.Length; i++)
    {
        var arg = rawArgs[i];
        if (!arg.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = arg[2..];
        if (i + 1 < rawArgs.Length && !rawArgs[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            map[key] = rawArgs[i + 1];
            i++;
        }
        else
        {
            map[key] = null;
        }
    }

    return map;
}

static int GetInt(Dictionary<string, string?> map, string key, int fallback)
    => map.TryGetValue(key, out var value)
        && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

static void ProbeVersion()
{
    var text = Environment.GetEnvironmentVariable("LLAMALAUNCHER_TEST_VERSION")
        ?? "version: 4321 (abcdef123)\nbuilt with MSVC 19.40 test build for x64";
    Console.Out.WriteLine(text);
    Console.Out.Flush();
    Environment.Exit(0);
}

static void ProbeHelp()
{
    if (Environment.GetEnvironmentVariable("LLAMALAUNCHER_TEST_HELP_SLEEP_MS") is { } sleep
        && int.TryParse(sleep, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
        && ms > 0)
    {
        Thread.Sleep(ms);
    }

    if (Environment.GetEnvironmentVariable("LLAMALAUNCHER_TEST_HELP_EXIT") == "1")
    {
        Console.Error.WriteLine("error: unknown argument: --help");
        Environment.Exit(1);
    }

    var text = ReadEnvFile("LLAMALAUNCHER_TEST_HELP_FILE") ?? DefaultHelpText;
    Console.Out.WriteLine(text);
    Console.Out.Flush();
    Environment.Exit(0);
}

static void ProbeDevices()
{
    if (Environment.GetEnvironmentVariable("LLAMALAUNCHER_TEST_DEVICES_EXIT") == "1")
    {
        Console.Error.WriteLine("error: invalid argument: --list-devices");
        Environment.Exit(1);
    }

    var text = ReadEnvFile("LLAMALAUNCHER_TEST_DEVICES_FILE")
        ?? "Available devices:\n  CUDA0: NVIDIA GeForce RTX 4090 (24564 MiB, 24123 MiB free)\n  CPU: CPU (AVX2 = 1)";
    Console.Out.WriteLine(text);
    Console.Out.Flush();
    Environment.Exit(0);
}

static string? ReadEnvFile(string name)
{
    var path = Environment.GetEnvironmentVariable(name);
    return path is not null && File.Exists(path) ? File.ReadAllText(path) : null;
}
