using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Models;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.GitHub;

/// <summary>
/// 官方 llama.cpp Releases 查询（api.github.com/repos/ggml-org/llama.cpp）。
/// 只读取元数据，不硬编码任何 release 标签或资产名。
/// 速率限制与缓存策略：
/// 1. 内存 + 磁盘缓存：一次成功查询同时服务稳定/预发布两个通道；<see cref="CacheReuseWindow"/> 内的重复查询
///    直接复用（一次“检查更新”流程内的两次查询只发一次请求）；
/// 2. 条件请求：保留 ETag，数据未变化时返回 304（配置令牌时 304 不计入配额）；
/// 3. 频率上限：解析 X-RateLimit-Reset / Retry-After，恢复时间前不再发起请求；有缓存时回退缓存数据
///    并提示缓存时间与恢复时间，无缓存时给出带恢复时间的可操作错误；
/// 4. 可选 GitHub 令牌（DPAPI 存储，见 SecretNames.GitHubToken）：未登录 60 次/小时 → 登录 5000 次/小时。
/// </summary>
public sealed class GitHubReleaseFeedService : IReleaseFeedService, IDisposable
{
    public const string OfficialRepository = "ggml-org/llama.cpp";

    /// <summary>同一份数据的隐式复用窗口（窗口内的重复查询直接读缓存，不再发请求）。</summary>
    internal static readonly TimeSpan CacheReuseWindow = TimeSpan.FromSeconds(60);

    /// <summary>检测到速率限制但响应未给出恢复时间时的保守冷却时长。</summary>
    internal static readonly TimeSpan DefaultRateLimitCooldown = TimeSpan.FromMinutes(1);

    private readonly HttpClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GitHubReleaseFeedService> _logger;
    private readonly IAppPaths _paths;
    private readonly ISecretStore _secrets;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CacheState> _cache = new(StringComparer.OrdinalIgnoreCase);

    public GitHubReleaseFeedService(
        TimeProvider timeProvider,
        ILogger<GitHubReleaseFeedService> logger,
        IAppPaths paths,
        ISecretStore secrets,
        HttpMessageHandler? handler = null)
    {
        _timeProvider = timeProvider;
        _logger = logger;
        _paths = paths;
        _secrets = secrets;
        _client = new HttpClient(handler ?? new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LlamaLauncher", "0.9"));
        _client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public string Repository { get; init; } = OfficialRepository;

    public async Task<ReleaseFeedResult> GetReleasesAsync(
        UpdateChannel channel,
        int maxReleases = 30,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var perPage = Math.Clamp(maxReleases, 1, 100);

        // 串行化：并发查询复用同一份结果，避免重复请求
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);

            // 1) 新鲜窗口：直接复用（如“检查更新”流程内的第二次列表查询）
            if (HasUsableBody(state, perPage) && now - state.FetchedUtc < CacheReuseWindow)
            {
                return SuccessResult(state, channel, fromCache: true, warning: null);
            }

            // 2) 已知限额恢复时间：不再发请求
            if (state.RateLimitedUntilUtc is { } limitedUntil && limitedUntil > now)
            {
                return RateLimitedResult(state, channel, perPage, now, limitedUntil);
            }

            var url = $"https://api.github.com/repos/{Repository}/releases?per_page={perPage}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (state.ETag is { Length: > 0 } etag && HasUsableBody(state, perPage))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }

            var token = await TryGetTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            try
            {
                using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotModified && HasUsableBody(state, perPage))
                {
                    // 数据未变化：复用缓存（配置令牌时 304 不计入配额）
                    state.FetchedUtc = now;
                    state.RateLimitedUntilUtc = null;
                    state.RateLimitNote = null;
                    await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("发行列表未变化（HTTP 304，复用缓存）");
                    return SuccessResult(state, channel, fromCache: true, warning: null);
                }

                if (response.IsSuccessStatusCode)
                {
                    var releases = ParseReleases(body, channel);
                    state.Body = body;
                    state.ETag = response.Headers.ETag?.ToString();
                    state.FetchedUtc = now;
                    state.PerPage = perPage;
                    state.RateLimitedUntilUtc = null;
                    state.RateLimitNote = null;
                    state.Parsed.Clear();
                    state.Parsed[channel] = releases;
                    await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("已获取 {Count} 个发行版本（通道：{Channel}）", releases.Count, channel);
                    return new ReleaseFeedResult(true, releases, null, now);
                }

                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    var remaining = GetHeaderValue(response, "X-RateLimit-Remaining");
                    var reset = ResolveRateLimitReset(response, remaining, body, now);
                    var isRateLimit = response.StatusCode == HttpStatusCode.TooManyRequests
                                      || remaining == "0"
                                      || reset is not null
                                      || body.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
                    if (isRateLimit)
                    {
                        var until = reset ?? now + DefaultRateLimitCooldown;
                        state.RateLimitedUntilUtc = until;
                        state.RateLimitNote = token is null
                            ? "未登录请求每小时 60 次"
                            : "已配置令牌的请求每小时 5000 次";
                        await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
                        _logger.LogWarning(
                            "GitHub API 速率限制生效（{Note}），恢复时间：{Reset:u}",
                            state.RateLimitNote,
                            until);
                        return RateLimitedResult(state, channel, perPage, now, until);
                    }

                    var apiMessage = TryReadApiMessage(body);
                    return CachedOrError(
                        state,
                        channel,
                        perPage,
                        now,
                        apiMessage is null
                            ? "GitHub API 拒绝了请求（HTTP 403）。"
                            : $"GitHub API 拒绝了请求：{Trim(apiMessage, 200)}");
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return CachedOrError(state, channel, perPage, now, "GitHub 仓库不存在或不可访问。");
                }

                return CachedOrError(
                    state,
                    channel,
                    perPage,
                    now,
                    $"GitHub 返回 HTTP {(int)response.StatusCode}。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
            {
                _logger.LogDebug(ex, "查询 GitHub Releases 失败");
                return CachedOrError(state, channel, perPage, now, "无法连接 GitHub（请检查网络）。");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _gate.Dispose();
    }

    /// <summary>成功结果；缓存窗口/304/回退场景下 <paramref name="fromCache"/> 为 true。</summary>
    private static ReleaseFeedResult SuccessResult(
        CacheState state,
        UpdateChannel channel,
        bool fromCache,
        string? warning)
    {
        if (!state.Parsed.TryGetValue(channel, out var releases))
        {
            releases = ParseReleases(state.Body, channel);
            state.Parsed[channel] = releases;
        }

        return new ReleaseFeedResult(true, releases, null, state.FetchedUtc)
        {
            FromCache = fromCache,
            Warning = warning,
        };
    }

    /// <summary>频率上限：有缓存回退缓存（附恢复时间），无缓存给出带恢复时间的可操作错误。</summary>
    private ReleaseFeedResult RateLimitedResult(
        CacheState state,
        UpdateChannel channel,
        int perPage,
        DateTimeOffset now,
        DateTimeOffset until)
    {
        var resetText = FormatResetText(until, now);
        if (HasUsableBody(state, perPage))
        {
            var warning = $"GitHub 请求频率已达上限，当前显示 {FormatLocalTime(state.FetchedUtc)} 获取的缓存数据" +
                          $"（限额预计{resetText}）。";
            return SuccessResult(state, channel, fromCache: true, warning);
        }

        var error = $"GitHub API 请求频率已达上限（{state.RateLimitNote ?? "未登录请求每小时 60 次"}）。" +
                    $"限额预计{resetText}。" +
                    "可稍后重试，或在“设置”页配置 GitHub 令牌提高上限；也可打开官方发布页手动下载后通过“导入本地压缩包”安装。";
        return new ReleaseFeedResult(false, Array.Empty<ReleaseInfo>(), error, now);
    }

    /// <summary>一般失败：有缓存则回退缓存并说明原因，无缓存返回带建议的错误。</summary>
    private static ReleaseFeedResult CachedOrError(
        CacheState state,
        UpdateChannel channel,
        int perPage,
        DateTimeOffset now,
        string reason)
    {
        if (HasUsableBody(state, perPage))
        {
            var warning = $"{reason}当前显示 {FormatLocalTime(state.FetchedUtc)} 获取的缓存数据。";
            return SuccessResult(state, channel, fromCache: true, warning);
        }

        return new ReleaseFeedResult(
            false,
            Array.Empty<ReleaseInfo>(),
            reason + "可打开官方发布页手动下载后通过“导入本地压缩包”安装。",
            now);
    }

    private static bool HasUsableBody(CacheState state, int perPage)
        => state.Body.Length > 0 && state.PerPage >= perPage;

    private async Task<string?> TryGetTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _secrets.GetAsync(SecretNames.GitHubToken, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取 GitHub 令牌失败，按未配置处理");
            return null;
        }
    }

    private async Task<CacheState> LoadStateAsync(CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(Repository, out var cached))
        {
            return cached;
        }

        var state = new CacheState();
        try
        {
            var path = CacheFilePath();
            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var persisted = JsonSerializer.Deserialize<PersistedCacheState>(json);
                if (persisted is { Body.Length: > 0 }
                    && string.Equals(persisted.Repository, Repository, StringComparison.OrdinalIgnoreCase))
                {
                    // 只接受可解析为 JSON 数组的缓存体
                    using var document = JsonDocument.Parse(persisted.Body);
                    if (document.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        state.Body = persisted.Body;
                        state.ETag = persisted.ETag;
                        state.FetchedUtc = persisted.FetchedUtc;
                        state.PerPage = persisted.PerPage;
                        state.RateLimitedUntilUtc = persisted.RateLimitedUntilUtc;
                        state.RateLimitNote = persisted.RateLimitNote;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取 GitHub Releases 缓存失败，忽略");
        }

        _cache[Repository] = state;
        return state;
    }

    private async Task SaveStateAsync(CacheState state, CancellationToken cancellationToken)
    {
        _cache[Repository] = state;
        try
        {
            Directory.CreateDirectory(_paths.CacheDirectory);
            var json = JsonSerializer.Serialize(new PersistedCacheState
            {
                Repository = Repository,
                ETag = state.ETag,
                FetchedUtc = state.FetchedUtc,
                PerPage = state.PerPage,
                RateLimitedUntilUtc = state.RateLimitedUntilUtc,
                RateLimitNote = state.RateLimitNote,
                Body = state.Body,
            });
            await File.WriteAllTextAsync(CacheFilePath(), json, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 取消不影响已取得的结果；缓存文件下次再写
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "写入 GitHub Releases 缓存失败（不影响本次查询）");
        }
    }

    private string CacheFilePath()
    {
        var slug = new string(Repository.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        return Path.Combine(_paths.CacheDirectory, $"github-releases-{slug}.json");
    }

    private static DateTimeOffset? ResolveRateLimitReset(
        HttpResponseMessage response,
        string? remaining,
        string body,
        DateTimeOffset now)
    {
        // Retry-After（秒数或 HTTP 日期）：次级限制常用
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
            {
                return now + delta;
            }

            if (retryAfter.Date is { } date)
            {
                return date;
            }
        }

        if (remaining == "0"
            && GetHeaderValue(response, "X-RateLimit-Reset") is { } resetValue
            && long.TryParse(resetValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        if (body.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase))
        {
            return now + DefaultRateLimitCooldown;
        }

        return null;
    }

    private static string? GetHeaderValue(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static string? TryReadApiMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("message", out var message)
                   && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Trim(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";

    private static string FormatLocalTime(DateTimeOffset value)
        => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private static string FormatResetText(DateTimeOffset until, DateTimeOffset now)
    {
        var local = until.ToLocalTime();
        var stamp = local.Date == now.ToLocalTime().Date
            ? local.ToString("HH:mm")
            : local.ToString("MM-dd HH:mm");
        var minutes = (int)Math.Ceiling(Math.Max(0, (until - now).TotalMinutes));
        return $"于 {stamp} 恢复，约 {Math.Max(1, minutes)} 分钟后";
    }

    private static List<ReleaseInfo> ParseReleases(string json, UpdateChannel channel)
    {
        var results = new List<ReleaseInfo>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var prerelease = element.TryGetProperty("prerelease", out var pre) && pre.GetBoolean();
            if (channel == UpdateChannel.Stable && prerelease)
            {
                continue;
            }

            var tag = element.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString() ?? string.Empty
                : string.Empty;
            if (tag.Length == 0)
            {
                continue;
            }

            var release = new ReleaseInfo
            {
                TagName = tag,
                Name = element.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null,
                Prerelease = prerelease,
                PublishedAtUtc = element.TryGetProperty("published_at", out var publishedElement)
                    && publishedElement.ValueKind == JsonValueKind.String
                    && publishedElement.TryGetDateTimeOffset(out var published)
                        ? published
                        : default,
                BodyMarkdown = element.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() : null,
                HtmlUrl = element.TryGetProperty("html_url", out var htmlElement)
                    ? htmlElement.GetString() ?? string.Empty
                    : string.Empty,
            };

            if (element.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var assetElement in assets.EnumerateArray())
                {
                    var name = assetElement.TryGetProperty("name", out var assetName)
                        ? assetName.GetString()
                        : null;
                    var downloadUrl = assetElement.TryGetProperty("browser_download_url", out var urlElement)
                        ? urlElement.GetString()
                        : null;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(downloadUrl))
                    {
                        continue;
                    }

                    release.Assets.Add(new ReleaseAsset
                    {
                        Name = name!,
                        SizeBytes = assetElement.TryGetProperty("size", out var sizeElement)
                            && sizeElement.ValueKind == JsonValueKind.Number
                            && sizeElement.TryGetInt64(out var size)
                                ? size
                                : null,
                        DownloadUrl = downloadUrl!,
                        Sha256Digest = assetElement.TryGetProperty("digest", out var digestElement)
                            ? digestElement.GetString()
                            : null,
                        BrowserUrl = downloadUrl,
                    });
                }
            }

            results.Add(release);
        }

        return results;
    }

    /// <summary>一次成功查询的内存状态（按仓库缓存，稳定/预发布通道共享同一份原始数据）。</summary>
    private sealed class CacheState
    {
        public string Body { get; set; } = string.Empty;

        public string? ETag { get; set; }

        public DateTimeOffset FetchedUtc { get; set; }

        public int PerPage { get; set; }

        public DateTimeOffset? RateLimitedUntilUtc { get; set; }

        public string? RateLimitNote { get; set; }

        public Dictionary<UpdateChannel, List<ReleaseInfo>> Parsed { get; } = new();
    }

    /// <summary>磁盘缓存（原始 JSON + ETag + 限额恢复时间），跨重启可用。</summary>
    private sealed class PersistedCacheState
    {
        public string Repository { get; set; } = string.Empty;

        public string? ETag { get; set; }

        public DateTimeOffset FetchedUtc { get; set; }

        public int PerPage { get; set; }

        public DateTimeOffset? RateLimitedUntilUtc { get; set; }

        public string? RateLimitNote { get; set; }

        public string Body { get; set; } = string.Empty;
    }
}
