using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LlamaLauncher.Application.Instances;
using LlamaLauncher.Domain;
using LlamaLauncher.Domain.StateMachine;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.App.Services;

/// <summary>
/// 系统托盘（WinForms NotifyIcon，与 WPF 共用 UI 线程消息泵）。
/// 菜单：显示主窗口、实例快捷启停/聊天、退出。
/// 图标使用应用图标资源（Assets/LlamaLauncher.ico，与 exe/窗口同一套）；资源不可用时回退为程序化绘制。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly IInstanceManager _instances;
    private readonly IAppLifecycle _lifecycle;
    private readonly INotificationService _notifications;
    private readonly ILogger<TrayService> _logger;
    private readonly NotifyIcon _notifyIcon;
    private readonly Font _menuFont;
    private Icon? _trayIcon;
    private IntPtr _iconHandle;

    public TrayService(
        IInstanceManager instances,
        IAppLifecycle lifecycle,
        INotificationService notifications,
        ILogger<TrayService> logger)
    {
        _instances = instances;
        _lifecycle = lifecycle;
        _notifications = notifications;
        _logger = logger;

        _menuFont = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
        _trayIcon = CreateTrayIcon();
        _notifyIcon = new NotifyIcon
        {
            Text = "LlamaLauncher",
            Icon = _trayIcon,
            Visible = false,
        };
        _notifyIcon.DoubleClick += (_, _) => _lifecycle.ShowMainWindow();
        _notifyIcon.ContextMenuStrip = BuildMenu();
    }

    public void Show() => _notifyIcon.Visible = true;

    public void Hide() => _notifyIcon.Visible = false;

    public void UpdateTooltip()
    {
        try
        {
            var active = _instances.GetInstances().Count(i => i.IsActive);
            var text = active == 0 ? "LlamaLauncher" : $"LlamaLauncher（{active} 个服务运行中）";
            _notifyIcon.Text = text.Length <= 63 ? text : text[..63];
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menuFont.Dispose();
        _trayIcon?.Dispose();
        _trayIcon = null;
        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip
        {
            AutoSize = true,
            DropShadowEnabled = true,
            Font = _menuFont,
            Padding = new Padding(4),
            ShowCheckMargin = false,
            ShowImageMargin = false,
            Renderer = new TrayMenuRenderer(ResolveMenuPalette()),
        };
        menu.Opening += (_, _) => RebuildMenu(menu);
        RebuildMenu(menu);
        return menu;
    }

    private void RebuildMenu(ContextMenuStrip menu)
    {
        var palette = ResolveMenuPalette();
        var renderer = new TrayMenuRenderer(palette);
        menu.Renderer = renderer;
        menu.BackColor = palette.Surface;
        menu.ForeColor = palette.TextPrimary;
        menu.Font = _menuFont;
        menu.Padding = new Padding(4);
        menu.Items.Clear();

        var showItem = CreateMenuItem("显示主窗口", palette);
        showItem.Click += (_, _) => _lifecycle.ShowMainWindow();
        menu.Items.Add(showItem);

        menu.Items.Add(new ToolStripSeparator());

        var instances = _instances.GetInstances();
        if (instances.Count == 0)
        {
            var empty = CreateMenuItem("（暂无实例）", palette);
            empty.Enabled = false;
            menu.Items.Add(empty);
        }
        else
        {
            foreach (var instance in instances)
            {
                var item = CreateMenuItem($"{instance.Name}（{StateText(instance.State)}）", palette);
                if (instance.IsActive)
                {
                    var stopItem = CreateMenuItem("停止", palette);
                    stopItem.Click += async (_, _) => await SafeStopAsync(instance.Id).ConfigureAwait(false);
                    item.DropDownItems.Add(stopItem);
                }
                else
                {
                    var startItem = CreateMenuItem("启动", palette);
                    startItem.Click += async (_, _) => await SafeStartAsync(instance.Id).ConfigureAwait(false);
                    item.DropDownItems.Add(startItem);
                    if (instance.State is InstanceState.Failed or InstanceState.ExitedUnexpectedly)
                    {
                        var errorItem = CreateMenuItem(instance.Record.LastError ?? "（无错误信息）", palette);
                        errorItem.Enabled = false;
                        item.DropDownItems.Add(errorItem);
                    }
                }

                ApplySubmenuStyle(item, renderer, palette);
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new ToolStripSeparator());
        var exitItem = CreateMenuItem("退出 LlamaLauncher", palette);
        exitItem.Click += async (_, _) => await _lifecycle.RequestExitAsync().ConfigureAwait(false);
        menu.Items.Add(exitItem);
    }

    private ToolStripMenuItem CreateMenuItem(string text, TrayMenuPalette palette)
    {
        var item = new ToolStripMenuItem(text)
        {
            AutoSize = true,
            ForeColor = palette.TextPrimary,
            Font = _menuFont,
            Padding = new Padding(8, 4, 8, 4),
        };
        return item;
    }

    private void ApplySubmenuStyle(
        ToolStripMenuItem parent,
        TrayMenuRenderer renderer,
        TrayMenuPalette palette)
    {
        parent.DropDown.Renderer = renderer;
        parent.DropDown.BackColor = palette.Surface;
        parent.DropDown.ForeColor = palette.TextPrimary;
        parent.DropDown.Padding = new Padding(4);
        if (parent.DropDown is ToolStripDropDownMenu dropDownMenu)
        {
            dropDownMenu.ShowCheckMargin = false;
            dropDownMenu.ShowImageMargin = false;
        }

        foreach (ToolStripItem item in parent.DropDownItems)
        {
            item.ForeColor = palette.TextPrimary;
            item.Font = _menuFont;
            item.Padding = new Padding(8, 4, 8, 4);
        }
    }

    internal static TrayMenuPalette ResolveMenuPalette()
    {
        var surface = ReadThemeColor("App.Surface", Color.FromArgb(35, 38, 41));
        var border = ReadThemeColor("App.Border", Color.FromArgb(58, 63, 69));
        var textPrimary = ReadThemeColor("App.Text.Primary", Color.FromArgb(232, 234, 237));
        var textSecondary = ReadThemeColor("App.Text.Secondary", Color.FromArgb(170, 176, 184));
        var accent = ReadThemeColor("App.Accent", Color.FromArgb(76, 141, 255));
        return new TrayMenuPalette(surface, border, textPrimary, textSecondary, accent, Color.White);
    }

    private static Color ReadThemeColor(string key, Color fallback)
    {
        try
        {
            if (System.Windows.Application.Current?.TryFindResource(key) is System.Windows.Media.SolidColorBrush brush)
            {
                var color = brush.Color;
                return Color.FromArgb(color.A, color.R, color.G, color.B);
            }
        }
        catch (Exception)
        {
        }

        return fallback;
    }

    private async Task SafeStartAsync(Guid instanceId)
    {
        try
        {
            var result = await _instances.StartAsync(instanceId).ConfigureAwait(false);
            _notifications.Notify(result.Success ? "实例已开始启动。" : "启动失败：" + result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "托盘启动实例失败");
            _notifications.Notify("启动失败：" + ex.Message);
        }
    }

    private async Task SafeStopAsync(Guid instanceId)
    {
        try
        {
            await _instances.StopAsync(instanceId).ConfigureAwait(false);
            _notifications.Notify("实例已停止。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "托盘停止实例失败");
            _notifications.Notify("停止失败：" + ex.Message);
        }
    }

    private static string StateText(InstanceState state) => state switch
    {
        InstanceState.Stopped => "已停止",
        InstanceState.Starting => "启动中",
        InstanceState.LoadingModel => "加载模型中",
        InstanceState.Ready => "运行中",
        InstanceState.Stopping => "停止中",
        InstanceState.Failed => "启动失败",
        InstanceState.ExitedUnexpectedly => "异常退出",
        _ => state.ToString(),
    };

    private Icon CreateTrayIcon()
    {
        // 优先加载应用图标资源（WPF pack URI），保证托盘与 exe/窗口图标一致
        try
        {
            var assemblyName = typeof(TrayService).Assembly.GetName().Name;
            var uri = new Uri(
                $"pack://application:,,,/{assemblyName};component/Assets/LlamaLauncher.ico",
                UriKind.Absolute);
            if (System.Windows.Application.GetResourceStream(uri) is { } resource)
            {
                using var stream = resource.Stream;
                return new Icon(stream, SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "加载应用图标资源失败，回退为程序化托盘图标");
        }

        return CreateFallbackTrayIcon();
    }

    /// <summary>程序化回退图标（品牌蓝圆角方块 + “LL”），仅在资源加载失败时使用。</summary>
    private Icon CreateFallbackTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var background = new SolidBrush(Color.FromArgb(255, 56, 96, 178));
            graphics.FillRoundedRectangle(background, new Rectangle(1, 1, 30, 30), 7);
            using var font = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            var text = "LL";
            var size = graphics.MeasureString(text, font);
            graphics.DrawString(
                text,
                font,
                textBrush,
                (32 - size.Width) / 2,
                (32 - size.Height) / 2 + 1);
        }

        _iconHandle = bitmap.GetHicon();
        return Icon.FromHandle(_iconHandle);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}

/// <summary>Graphics.FillRoundedRectangle 辅助（.NET 8 无内建圆角矩形绘制）。</summary>
internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(
        this Graphics graphics,
        Brush brush,
        Rectangle bounds,
        int radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
