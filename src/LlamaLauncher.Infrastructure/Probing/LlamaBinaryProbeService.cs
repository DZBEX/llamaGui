using System.Collections.Concurrent;
using System.Text;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Capabilities;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.Probing;

/// <summary>探测超时配置（测试可注入更短的值）。</summary>
public sealed record LlamaProbeTimeouts(
    TimeSpan Version,
    TimeSpan Help,
    TimeSpan Devices)
{
    public static readonly LlamaProbeTimeouts Default =
        new(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(25));
}

/// <summary>
/// llama-server.exe 能力探测：分别执行 --version / --help / --list-devices，
/// 保存原始文本与解析结果，并按“路径 + 文件指纹”缓存；同一可执行文件变化后自动失效。
/// </summary>
public sealed class LlamaBinaryProbeService : ILlamaBinaryProbeService
{
    private readonly IProbeProcessRunner _runner;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LlamaBinaryProbeService> _logger;
    private readonly LlamaProbeTimeouts _timeouts;
    private readonly ConcurrentDictionary<string, ServerCapabilities> _cache = new(StringComparer.OrdinalIgnoreCase);

    public LlamaBinaryProbeService(
        IProbeProcessRunner runner,
        TimeProvider timeProvider,
        ILogger<LlamaBinaryProbeService> logger,
        LlamaProbeTimeouts? timeouts = null)
    {
        _runner = runner;
        _timeProvider = timeProvider;
        _logger = logger;
        _timeouts = timeouts ?? LlamaProbeTimeouts.Default;
    }

    public ServerCapabilities? TryGetCached(string executablePath)
    {
        var stamp = TryGetFileStamp(executablePath);
        if (stamp is null)
        {
            return null;
        }

        return _cache.TryGetValue(CacheKey(executablePath, stamp), out var cached) ? cached : null;
    }

    public async Task<ServerCapabilities> ProbeAsync(
        string executablePath,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var stamp = TryGetFileStamp(executablePath);
        var capabilities = new ServerCapabilities
        {
            ExecutablePath = executablePath,
            ExecutableStamp = stamp,
            ProbedUtc = _timeProvider.GetUtcNow(),
        };

        if (stamp is null)
        {
            capabilities.Outcome = ProbeOutcome.Failed;
            capabilities.Warnings.Add("可执行文件不存在或不可访问。");
            capabilities.ProbeResults.Add(new ProbeResult
            {
                Command = executablePath,
                ErrorMessage = "可执行文件不存在或不可访问。",
                StartedUtc = capabilities.ProbedUtc,
            });
            return capabilities;
        }

        var key = CacheKey(executablePath, stamp);
        if (!forceRefresh && _cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var version = await _runner.RunAsync(executablePath, new[] { "--version" }, _timeouts.Version, cancellationToken)
            .ConfigureAwait(false);
        var help = await _runner.RunAsync(executablePath, new[] { "--help" }, _timeouts.Help, cancellationToken)
            .ConfigureAwait(false);
        var devices = await _runner.RunAsync(executablePath, new[] { "--list-devices" }, _timeouts.Devices, cancellationToken)
            .ConfigureAwait(false);

        capabilities.ProbeResults.Add(ToProbeResult(version));
        capabilities.ProbeResults.Add(ToProbeResult(help));
        capabilities.ProbeResults.Add(ToProbeResult(devices));

        capabilities.VersionText = PreferredOutput(version);
        capabilities.HelpText = PreferredOutput(help);
        capabilities.ListDevicesText = (devices.StandardOutput + "\n" + devices.StandardError).Trim();

        capabilities.Options = HelpTextParser.ParseOptions(capabilities.HelpText);
        capabilities.Devices = DeviceListParser.Parse(capabilities.ListDevicesText);

        capabilities.Outcome = Classify(version, help, devices);
        if (!version.Succeeded)
        {
            capabilities.Warnings.Add($"--version 探测失败：{Describe(version)}");
        }

        if (!help.Succeeded)
        {
            capabilities.Warnings.Add($"--help 探测失败：{Describe(help)}，无法校验参数支持情况。");
        }

        if (!devices.Succeeded)
        {
            capabilities.Warnings.Add($"--list-devices 未成功：{Describe(devices)}（部分版本不提供该开关，不代表程序不可用）。");
        }

        if (capabilities.Outcome == ProbeOutcome.Failed)
        {
            capabilities.Warnings.Add("版本与帮助探测均失败，无法确认该程序可用。");
            try
            {
                var directory = Path.GetDirectoryName(executablePath);
                var hasCompanionDlls = !string.IsNullOrWhiteSpace(directory)
                    && Directory.Exists(directory)
                    && Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).Any();
                if (!hasCompanionDlls)
                {
                    capabilities.Warnings.Add(
                        "该目录中只有可执行文件、没有 llama.cpp 依赖 DLL；请复制完整发行包目录" +
                        "（含 ggml*.dll、llama*.dll 等），或使用“版本管理”重新安装/导入完整压缩包。");
                }
            }
            catch (Exception)
            {
                // 依赖完整性提示是附加诊断，不影响探测结果。
            }
        }

        _cache[key] = capabilities;
        if (capabilities.Outcome == ProbeOutcome.Failed)
        {
            _logger.LogWarning(
                "能力探测失败：{Path}；{Details}",
                executablePath,
                string.Join("；", capabilities.Warnings));
        }
        else
        {
            _logger.LogInformation(
                "能力探测完成：{Path}，结果 {Outcome}，支持参数 {OptionCount} 个，设备 {DeviceCount} 台",
                executablePath, capabilities.Outcome, capabilities.Options.Count, capabilities.Devices.Count);
        }
        return capabilities;
    }

    private static ProbeOutcome Classify(ProcessRunResult version, ProcessRunResult help, ProcessRunResult devices)
    {
        if (version.Succeeded && help.Succeeded)
        {
            return ProbeOutcome.Success;
        }

        if (version.Succeeded || help.Succeeded || devices.Succeeded)
        {
            return ProbeOutcome.PartialFailure;
        }

        return ProbeOutcome.Failed;
    }

    private static string PreferredOutput(ProcessRunResult result)
    {
        var stdout = result.StandardOutput.Trim();
        if (!string.IsNullOrEmpty(stdout))
        {
            return stdout;
        }

        return result.StandardError.Trim();
    }

    private static string Describe(ProcessRunResult result)
    {
        if (result.TimedOut)
        {
            return "超时";
        }

        if (result.ErrorMessage is not null)
        {
            return result.ErrorMessage;
        }

        var stderr = result.StandardError.Trim();
        if (!string.IsNullOrEmpty(stderr))
        {
            var line = stderr.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
            return $"退出代码 {result.ExitCode}：{Truncate(line, 160)}";
        }

        return $"退出代码 {result.ExitCode}";
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    private static ProbeResult ToProbeResult(ProcessRunResult run) => new()
    {
        Command = run.Command,
        ExitCode = run.ExitCode,
        StandardOutput = run.StandardOutput,
        StandardError = run.StandardError,
        TimedOut = run.TimedOut,
        DurationMs = run.DurationMs,
        ErrorMessage = run.ErrorMessage,
        StartedUtc = run.StartedUtc,
    };

    private static string? TryGetFileStamp(string executablePath)
    {
        try
        {
            var info = new FileInfo(executablePath);
            if (!info.Exists)
            {
                return null;
            }

            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string CacheKey(string executablePath, string stamp)
        => Path.GetFullPath(executablePath).ToLowerInvariant() + "|" + stamp;

    public override string ToString() => $"LlamaBinaryProbeService(cached={_cache.Count})";
}
