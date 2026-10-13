using LlamaLauncher.Application.Capabilities;
using LlamaLauncher.Domain;

namespace LlamaLauncher.UnitTests.TestSupport;

/// <summary>多个版本的 --help 文本样例（用于解析器与命令行构建测试）。</summary>
internal static class HelpFixtures
{
    public const string Modern = """
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

    /// <summary>更老的排版：flag 与占位符交错（"-c N, --ctx-size N ..."）。</summary>
    public const string LegacyInterleaved = """
        usage: ./server [options]

        options:
        -h,        --help, --usage             print usage and exit
        -m FILE,   --model FILE                model path
        -c N,      --ctx-size N                size of the prompt context
        -t N,      --threads N                 number of threads
        -ngl N,    --n-gpu-layers N            number of layers to store in VRAM
        -fa,       --flash-attn                enable flash attention
        -sm,       --split-mode LAYER          split mode
        --host HOST                            ip address to listen
        --port PORT                            port to listen
        """;

    /// <summary>新版本：n-gpu-layers 明确提到 auto/all。</summary>
    public const string WithGpuLayersAuto = """
        usage: llama-server [options]

          -m,    --model <FILE>                   model to load
          -c,    --ctx-size <N>                   size of the prompt context
          -ngl,  --n-gpu-layers <N>               max number of layers to store in VRAM, pass 'all' to offload all layers, or 'auto'
          -fa,   --flash-attn [on|off|auto]       set Flash Attention use
                 --host <HOST>                    ip address to listen
                 --port <PORT>                    port to listen
          -dev,  --device <dev1,dev2,..>          comma-separated list of devices
          -ctk,  --cache-type-k <TYPE>            KV cache data type for K
          -ctv,  --cache-type-v <TYPE>            KV cache data type for V
          -b,    --batch-size <N>                 logical maximum batch size
          -ub,   --ubatch-size <N>                physical maximum batch size
          -t,    --threads <N>                    number of threads to use during generation
          -tb,   --threads-batch <N>              number of threads to use during batch and prompt processing
          -np,   --parallel <N>                   number of parallel sequences to decode
          -a,    --alias <ALIAS>                  set an alias for the model
          -sm,   --split-mode <none|layer|row>    how to split the model across multiple GPUs
          -ts,   --tensor-split <ts0/ts1/..>      fraction of the model to offload to each GPU
          -mg,   --main-gpu <I>                   the GPU to use for the model
          -v,    --verbose                        verbose logging
                 --api-key <KEY>                  API key to use for authentication
        """;

    public const string Garbage = """
        这不是帮助文本
        random gibberish !!! ###
          -c,    --ctx-size <N>     size of context
        more garbage
        """;

    public static ServerCapabilities CreateCapabilities(
        string helpText,
        string? devicesText = null,
        string versionText = "version: 4321 (abcdef1)")
    {
        var capabilities = new ServerCapabilities
        {
            ExecutablePath = "C:\\fake\\llama-server.exe",
            VersionText = versionText,
            HelpText = helpText,
            ListDevicesText = devicesText,
            Outcome = ProbeOutcome.Success,
            ProbedUtc = DateTimeOffset.UtcNow,
        };
        capabilities.Options.AddRange(HelpTextParser.ParseOptions(helpText));
        if (devicesText is not null)
        {
            capabilities.Devices.AddRange(DeviceListParser.Parse(devicesText));
        }

        return capabilities;
    }
}
