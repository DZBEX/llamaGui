using System.Globalization;

namespace LlamaLauncher.Application.Updates;

/// <summary>
/// 启动时自动检查更新的节流规则：让设置中的“检查间隔（小时）”真正生效，
/// 避免频繁启动/重启应用时反复请求 GitHub（未登录配额仅 60 次/小时）。
/// </summary>
public static class UpdateCheckSchedule
{
    /// <summary>
    /// 现在是否应执行自动检查：从未检查过、上次检查时间无法解析、或距上次检查已达到间隔。
    /// </summary>
    public static bool ShouldCheck(string? lastCheckUtcIso, int intervalHours, DateTimeOffset nowUtc)
    {
        if (intervalHours <= 0)
        {
            return true;
        }

        return !TryParseLastCheck(lastCheckUtcIso, out var last)
               || nowUtc - last >= TimeSpan.FromHours(intervalHours);
    }

    /// <summary>解析设置中保存的上次检查时间（ISO 8601，Roundtrip）。</summary>
    public static bool TryParseLastCheck(string? value, out DateTimeOffset lastCheckUtc)
    {
        lastCheckUtc = default;
        return !string.IsNullOrWhiteSpace(value)
               && DateTimeOffset.TryParse(
                   value,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind,
                   out lastCheckUtc);
    }
}
