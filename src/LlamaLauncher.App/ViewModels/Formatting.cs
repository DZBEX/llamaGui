using LlamaLauncher.Domain;

namespace LlamaLauncher.App.ViewModels;

/// <summary>UI 统一格式化辅助。</summary>
public static class Formatting
{
    public static string Bytes(long? bytes)
    {
        if (bytes is null)
        {
            return "—";
        }

        double value = bytes.Value;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    public static string Percent(double? value) => value is null ? "—" : $"{value:0.#}%";

    public static string LocalTime(DateTimeOffset? value)
        => value is null ? "—" : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>聊天消息的紧凑时间（当天 HH:mm，跨天 MM-dd HH:mm，跨年 yyyy-MM-dd HH:mm）。</summary>
    public static string MessageTime(DateTimeOffset? value, DateTimeOffset? now = null)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var local = value.Value.ToLocalTime();
        var today = (now ?? DateTimeOffset.Now).ToLocalTime().Date;
        if (local.Date == today)
        {
            return local.ToString("HH:mm");
        }

        return local.Year == today.Year ? local.ToString("MM-dd HH:mm") : local.ToString("yyyy-MM-dd HH:mm");
    }

    public static string Duration(TimeSpan? span)
    {
        if (span is null)
        {
            return "—";
        }

        var value = span.Value;
        if (value.TotalDays >= 1)
        {
            return $"{(int)value.TotalDays} 天 {value.Hours} 小时";
        }

        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours} 小时 {value.Minutes} 分";
        }

        if (value.TotalMinutes >= 1)
        {
            return $"{(int)value.TotalMinutes} 分 {value.Seconds} 秒";
        }

        return $"{value.TotalSeconds:0} 秒";
    }

    public static string InstanceStateText(InstanceState state) => state switch
    {
        InstanceState.Stopped => "已停止",
        InstanceState.Starting => "启动中",
        InstanceState.LoadingModel => "加载模型中",
        InstanceState.Ready => "运行中",
        InstanceState.Stopping => "停止中",
        InstanceState.Failed => "失败",
        InstanceState.ExitedUnexpectedly => "异常退出",
        _ => "未知",
    };

    public static string ModelStatusText(ModelFileStatus status) => status switch
    {
        ModelFileStatus.Ok => "正常",
        ModelFileStatus.Missing => "文件缺失",
        ModelFileStatus.InvalidExtension => "扩展名错误",
        ModelFileStatus.Empty => "空文件",
        ModelFileStatus.Unreadable => "不可读",
        ModelFileStatus.InvalidFormat => "格式无效",
        _ => "未知",
    };

    public static string MessageStatusText(MessageStatus status) => status switch
    {
        MessageStatus.Pending => "等待中",
        MessageStatus.Streaming => "生成中",
        MessageStatus.Completed => "已完成",
        MessageStatus.Interrupted => "已中断",
        MessageStatus.Failed => "失败",
        _ => status.ToString(),
    };

    public static string BackendText(BackendKind backend) => backend switch
    {
        BackendKind.Cpu => "CPU",
        BackendKind.Cuda => "CUDA",
        BackendKind.Vulkan => "Vulkan",
        BackendKind.Sycl => "SYCL",
        BackendKind.Rocm => "ROCm/HIP",
        BackendKind.OpenVino => "OpenVINO",
        BackendKind.Other => "其他",
        _ => "未识别",
    };

    public static string UpdateStageText(UpdateStage stage) => stage switch
    {
        UpdateStage.Checking => "检查中",
        UpdateStage.Available => "有可用更新",
        UpdateStage.Downloading => "下载中",
        UpdateStage.Verifying => "校验中",
        UpdateStage.Extracting => "解压中",
        UpdateStage.Probing => "能力验证中",
        UpdateStage.ReadyToSwitch => "待切换",
        UpdateStage.Active => "已完成",
        UpdateStage.Cancelled => "已取消",
        UpdateStage.Failed => "失败",
        UpdateStage.RolledBack => "已回滚",
        UpdateStage.NotAvailable => "无更新",
        _ => stage.ToString(),
    };
}
