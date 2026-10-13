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
/// 菜单：显示主窗口、实例快捷启停/聊天、退出。图标为运行时绘制，无外部资源依赖。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly IInstanceManager _instances;
    private readonly IAppLifecycle _lifecycle;
    private readonly INotificationService _notifications;
    private readonly ILogger<TrayService> _logger;
    private readonly NotifyIcon _notifyIcon;
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

        _notifyIcon = new NotifyIcon
        {
            Text = "LlamaLauncher",
            Icon = CreateTrayIcon(),
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
        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => RebuildMenu(menu);
        RebuildMenu(menu);
        return menu;
    }

    private void RebuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();

        var showItem = new ToolStripMenuItem("显示主窗口");
        showItem.Click += (_, _) => _lifecycle.ShowMainWindow();
        menu.Items.Add(showItem);

        menu.Items.Add(new ToolStripSeparator());

        var instances = _instances.GetInstances();
        if (instances.Count == 0)
        {
            var empty = new ToolStripMenuItem("（暂无实例）") { Enabled = false };
            menu.Items.Add(empty);
        }
        else
        {
            foreach (var instance in instances)
            {
                var item = new ToolStripMenuItem($"{instance.Name}（{StateText(instance.State)}）");
                if (instance.IsActive)
                {
                    var stopItem = new ToolStripMenuItem("停止");
                    stopItem.Click += async (_, _) => await SafeStopAsync(instance.Id).ConfigureAwait(false);
                    item.DropDownItems.Add(stopItem);
                }
                else
                {
                    var startItem = new ToolStripMenuItem("启动");
                    startItem.Click += async (_, _) => await SafeStartAsync(instance.Id).ConfigureAwait(false);
                    item.DropDownItems.Add(startItem);
                    if (instance.State is InstanceState.Failed or InstanceState.ExitedUnexpectedly)
                    {
                        item.DropDownItems.Add(new ToolStripMenuItem(instance.Record.LastError ?? "（无错误信息）")
                        {
                            Enabled = false,
                        });
                    }
                }

                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new ToolStripSeparator());
        var exitItem = new ToolStripMenuItem("退出 LlamaLauncher");
        exitItem.Click += async (_, _) => await _lifecycle.RequestExitAsync().ConfigureAwait(false);
        menu.Items.Add(exitItem);
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
