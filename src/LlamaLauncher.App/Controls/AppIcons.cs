using System.Windows.Media;
using System.Windows.Media.Imaging;
using LlamaLauncher.Domain.Workflows;

namespace LlamaLauncher.App.Controls;

/// <summary>
/// 界面图标目录：Assets/Icons 下的 PNG 资源（由 scripts/make-ui-icons.ps1 生成，
/// 纯白 + 透明通道，界面统一用 <see cref="AppIcon"/> 蒙版着色；浅色/深色主题共用同一份资源）。
/// </summary>
public static class AppIcons
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, ImageSource?> Cache = new();

    /// <summary>图标资源绝对 pack URI（供代码加载）。</summary>
    public static Uri Uri(string name) => new(
        $"pack://application:,,,/{typeof(AppIcons).Assembly.GetName().Name};component/Assets/Icons/{name}.png",
        UriKind.Absolute);

    /// <summary>按名称取图标位图（冻结缓存；缺失或加载失败时返回 null）。</summary>
    public static ImageSource? Get(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        lock (SyncRoot)
        {
            if (Cache.TryGetValue(name, out var cached))
            {
                return cached;
            }
        }

        ImageSource? image = null;
        try
        {
            var bitmap = new BitmapImage(Uri(name));
            bitmap.Freeze();
            image = bitmap;
        }
        catch (Exception)
        {
            // 图标缺失时静默跳过（其余界面元素照常渲染）
        }

        lock (SyncRoot)
        {
            Cache[name] = image;
        }

        return image;
    }

    /// <summary>工作流节点类型 → 图标名。</summary>
    public static string? NodeIconName(string? typeId) => typeId switch
    {
        NodeTypeCatalog.Start => "node-start",
        NodeTypeCatalog.End => "node-end",
        NodeTypeCatalog.Transform => "node-transform",
        NodeTypeCatalog.Condition => "node-condition",
        NodeTypeCatalog.Loop => "node-loop",
        NodeTypeCatalog.Parallel => "node-parallel",
        NodeTypeCatalog.Join => "node-join",
        NodeTypeCatalog.Exception => "warn",
        NodeTypeCatalog.Decision => "node-decision",
        NodeTypeCatalog.Rules => "node-rules",
        NodeTypeCatalog.Algorithm => "node-algorithm",
        NodeTypeCatalog.Confirm => "node-confirm",
        NodeTypeCatalog.SubWorkflow => "node-subworkflow",
        _ => null,
    };

    /// <summary>节点运行状态 → 图标名。</summary>
    public static string RunStateIconName(NodeRunState? state) => state switch
    {
        NodeRunState.Succeeded => "state-check",
        NodeRunState.Running => "state-play",
        NodeRunState.Failed => "state-error",
        NodeRunState.TimedOut => "state-timeout",
        NodeRunState.Skipped => "state-skip",
        NodeRunState.Canceled => "state-cancel",
        NodeRunState.PausedAtBreakpoint => "state-pause",
        NodeRunState.WaitingForUser => "node-confirm",
        _ => "state-pending",
    };

    /// <summary>工作流运行状态 → 图标名。</summary>
    public static string RunStateIconName(WorkflowRunState? state) => state switch
    {
        WorkflowRunState.Succeeded => "state-check",
        WorkflowRunState.Running => "state-play",
        WorkflowRunState.Paused => "state-pause",
        WorkflowRunState.Failed => "state-error",
        WorkflowRunState.Canceled => "state-cancel",
        WorkflowRunState.TimedOut => "state-timeout",
        _ => "state-pending",
    };
}
