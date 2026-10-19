using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LlamaLauncher.App.Services;

/// <summary>托盘右键菜单的当前主题色，打开菜单时从 WPF 资源重新读取。</summary>
internal sealed record TrayMenuPalette(
    Color Surface,
    Color Border,
    Color TextPrimary,
    Color TextSecondary,
    Color Accent,
    Color HoverText);

/// <summary>WinForms 托盘菜单的主题化渲染：去图标留白、圆角高亮、主题分隔线与自定义子菜单箭头。</summary>
internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    private readonly TrayMenuPalette _palette;

    public TrayMenuRenderer(TrayMenuPalette palette)
        : base(new TrayMenuColorTable(palette))
    {
        _palette = palette;
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(_palette.Surface);
        e.Graphics.FillRectangle(brush, e.ToolStrip.ClientRectangle);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(_palette.Border);
        var bounds = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        e.Graphics.DrawRectangle(pen, bounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled)
        {
            return;
        }

        var bounds = new Rectangle(3, 2, e.Item.Width - 7, e.Item.Height - 5);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreateRoundedRectangle(bounds, 5);
        using var brush = new SolidBrush(_palette.Accent);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled
            ? _palette.TextSecondary
            : e.Item.Selected
                ? _palette.HoverText
                : _palette.TextPrimary;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(_palette.Border);
        if (e.Vertical)
        {
            var x = e.Item.Width / 2;
            e.Graphics.DrawLine(pen, x, 4, x, e.Item.Height - 4);
            return;
        }

        var y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 9, y, e.Item.Width - 9, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        if (e.Direction != ArrowDirection.Right)
        {
            base.OnRenderArrow(e);
            return;
        }

        var rect = e.ArrowRectangle;
        var centerX = rect.Left + (rect.Width / 2);
        var centerY = rect.Top + (rect.Height / 2);
        var item = e.Item;
        if (item is null)
        {
            base.OnRenderArrow(e);
            return;
        }

        var color = item.Enabled
            ? item.Selected ? _palette.HoverText : _palette.TextSecondary
            : _palette.TextSecondary;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.4f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        e.Graphics.DrawLines(
            pen,
            new[]
            {
                new Point(centerX - 2, centerY - 4),
                new Point(centerX + 2, centerY),
                new Point(centerX - 2, centerY + 4),
            });
    }

    private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class TrayMenuColorTable : ProfessionalColorTable
{
    private readonly TrayMenuPalette _palette;

    public TrayMenuColorTable(TrayMenuPalette palette)
    {
        _palette = palette;
    }

    public override Color ToolStripDropDownBackground => _palette.Surface;
    public override Color ImageMarginGradientBegin => _palette.Surface;
    public override Color ImageMarginGradientMiddle => _palette.Surface;
    public override Color ImageMarginGradientEnd => _palette.Surface;
    public override Color ImageMarginRevealedGradientBegin => _palette.Surface;
    public override Color ImageMarginRevealedGradientMiddle => _palette.Surface;
    public override Color ImageMarginRevealedGradientEnd => _palette.Surface;
    public override Color MenuBorder => _palette.Border;
    public override Color MenuItemBorder => _palette.Accent;
    public override Color MenuItemSelected => _palette.Accent;
    public override Color MenuItemSelectedGradientBegin => _palette.Accent;
    public override Color MenuItemSelectedGradientEnd => _palette.Accent;
    public override Color MenuItemPressedGradientBegin => _palette.Surface;
    public override Color MenuItemPressedGradientMiddle => _palette.Surface;
    public override Color MenuItemPressedGradientEnd => _palette.Surface;
    public override Color SeparatorDark => _palette.Border;
    public override Color SeparatorLight => _palette.Border;
    public override Color ButtonSelectedHighlight => _palette.Accent;
    public override Color ButtonSelectedGradientBegin => _palette.Accent;
    public override Color ButtonSelectedGradientEnd => _palette.Accent;
    public override Color ButtonSelectedBorder => _palette.Accent;
}
