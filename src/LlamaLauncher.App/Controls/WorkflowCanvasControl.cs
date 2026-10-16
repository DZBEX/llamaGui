using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LlamaLauncher.Domain.Workflows;

namespace LlamaLauncher.App.Controls;

/// <summary>连线创建请求（拖拽端口后释放）。</summary>
public sealed record WorkflowConnectionRequest(
    string SourceNodeId,
    string SourcePort,
    string TargetNodeId,
    string TargetPort);

/// <summary>
/// 工作流可视化画布：绘制节点/连线/运行状态，并处理选择、拖动、连线、缩放、平移与拖放新增。
/// 只负责呈现与交互；业务流程与校验由 ViewModel / 引擎负责。
/// </summary>
public sealed class WorkflowCanvasControl : FrameworkElement
{
    private const double NodeWidth = 216;
    private const double NodeHeight = 78;
    private const double GridSize = 26;
    private const double MinZoom = 0.3;
    private const double MaxZoom = 2.0;

    private readonly List<(string NodeId, Rect Rect)> _nodeRects = new();
    private readonly List<(string NodeId, string Port, bool IsInput, Point Center)> _portPoints = new();
    private readonly List<(string EdgeId, Point[] Points)> _edgePaths = new();

    private string? _draggingNodeId;
    private Point _dragOffset;
    private bool _isPanning;
    private Point _panStart;
    private double _panStartX;
    private double _panStartY;
    private (string NodeId, string Port, Point Start)? _connecting;
    private Point _connectCurrent;

    public WorkflowCanvasControl()
    {
        Focusable = true;
        AllowDrop = true;
        ClipToBounds = true;
    }

    /// <summary>拖入节点类型标识（DragDrop 数据格式）。</summary>
    public const string NodeTypeDataFormat = "LlamaLauncher.Workflow.NodeType";

    public WorkflowDefinition? Definition { get; private set; }

    public string? SelectedNodeId { get; private set; }

    public string? SelectedEdgeId { get; private set; }

    public IReadOnlyDictionary<string, NodeRunState>? RunStates { get; private set; }

    public IReadOnlyCollection<string>? ActiveEdges { get; private set; }

    public IReadOnlyDictionary<string, string>? NodeStatusTexts { get; private set; }

    public double Zoom { get; private set; } = 1.0;

    public double PanX { get; private set; }

    public double PanY { get; private set; }

    public event EventHandler<string?>? SelectionChanged;

    public event EventHandler<string>? NodeDragStarted;

    public event EventHandler<string>? NodeMoved;

    public event EventHandler<WorkflowConnectionRequest>? ConnectionRequested;

    public event EventHandler<(string TypeId, double X, double Y)>? NodeDropped;

    public event EventHandler<string>? NodeDoubleClicked;

    public event EventHandler? DeleteRequested;

    public void SetDefinition(WorkflowDefinition? definition)
    {
        Definition = definition;
        InvalidateVisual();
    }

    public void SetSelection(string? nodeId, string? edgeId)
    {
        SelectedNodeId = nodeId;
        SelectedEdgeId = edgeId;
        InvalidateVisual();
    }

    public void SetRunState(
        IReadOnlyDictionary<string, NodeRunState>? runStates,
        IReadOnlyCollection<string>? activeEdges,
        IReadOnlyDictionary<string, string>? nodeStatusTexts)
    {
        RunStates = runStates;
        ActiveEdges = activeEdges;
        NodeStatusTexts = nodeStatusTexts;
        InvalidateVisual();
    }

    public void NotifyNodeMoved(string nodeId, double x, double y)
    {
        if (Definition?.FindNode(nodeId) is { } node)
        {
            node.X = x;
            node.Y = y;
            InvalidateVisual();
        }
    }

    /// <summary>适配全图（缩放到可见范围）。</summary>
    public void FitToView()
    {
        if (Definition is null || Definition.Nodes.Count == 0)
        {
            Zoom = 1;
            PanX = 0;
            PanY = 0;
            InvalidateVisual();
            return;
        }

        var minX = Definition.Nodes.Min(n => n.X);
        var minY = Definition.Nodes.Min(n => n.Y);
        var maxX = Definition.Nodes.Max(n => n.X + NodeWidth);
        var maxY = Definition.Nodes.Max(n => n.Y + NodeHeight);
        var width = Math.Max(1, maxX - minX);
        var height = Math.Max(1, maxY - minY);
        var viewWidth = Math.Max(200, ActualWidth - 60);
        var viewHeight = Math.Max(200, ActualHeight - 60);
        // 下限 0.5：避免在较窄的面板中把节点缩得过小影响阅读（可手动再缩小）。
        Zoom = Math.Clamp(Math.Min(viewWidth / width, viewHeight / height), 0.5, 1.4);
        PanX = (ActualWidth - width * Zoom) / 2 - minX * Zoom;
        PanY = (ActualHeight - height * Zoom) / 2 - minY * Zoom;
        InvalidateVisual();
    }

    /// <summary>按缩放系数调整视图（以画布中心为基准）。</summary>
    public void ZoomBy(double factor)
    {
        var newZoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - Zoom) < 0.0001)
        {
            return;
        }

        var centerX = ActualWidth / 2;
        var centerY = ActualHeight / 2;
        var canvasX = (centerX - PanX) / Zoom;
        var canvasY = (centerY - PanY) / Zoom;
        Zoom = newZoom;
        PanX = centerX - canvasX * Zoom;
        PanY = centerY - canvasY * Zoom;
        InvalidateVisual();
    }

    /// <summary>把画布坐标点移动到视图中心（用于定位节点）。</summary>
    public void CenterOn(double canvasX, double canvasY)
    {
        PanX = ActualWidth / 2 - canvasX * Zoom;
        PanY = ActualHeight / 2 - canvasY * Zoom;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(
            double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var background = ResolveBrush("App.Surface", Color.FromRgb(0x23, 0x26, 0x29));
        dc.DrawRectangle(background, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (Definition is null)
        {
            return;
        }

        _nodeRects.Clear();
        _portPoints.Clear();
        _edgePaths.Clear();

        var borderBrush = ResolveBrush("App.Border", Color.FromRgb(0x3A, 0x3F, 0x45));
        var textBrush = ResolveBrush("App.Text.Primary", Color.FromRgb(0xE8, 0xEA, 0xED));
        var secondaryBrush = ResolveBrush("App.Text.Secondary", Color.FromRgb(0xAA, 0xB0, 0xB8));
        var accentBrush = ResolveBrush("App.Accent", Color.FromRgb(0x4C, 0x8D, 0xFF));
        var surfaceAlt = ResolveBrush("App.Surface.Alt", Color.FromRgb(0x2B, 0x2F, 0x33));

        // 网格
        var gridPen = new Pen(borderBrush, 0.6) { Brush = borderBrush };
        gridPen.Freeze();
        var gridOpacity = 0.35;
        dc.PushOpacity(gridOpacity);
        var gridStep = GridSize * Zoom;
        var offsetX = PanX % gridStep;
        var offsetY = PanY % gridStep;
        for (var x = offsetX; x < ActualWidth; x += gridStep)
        {
            dc.DrawLine(gridPen, new Point(x, 0), new Point(x, ActualHeight));
        }

        for (var y = offsetY; y < ActualHeight; y += gridStep)
        {
            dc.DrawLine(gridPen, new Point(0, y), new Point(ActualWidth, y));
        }

        dc.Pop();

        dc.PushTransform(new TranslateTransform(PanX, PanY));
        dc.PushTransform(new ScaleTransform(Zoom, Zoom));

        // 先画连线（节点覆盖其上）
        foreach (var edge in Definition.Edges)
        {
            var source = Definition.FindNode(edge.SourceNodeId);
            var target = Definition.FindNode(edge.TargetNodeId);
            if (source is null || target is null)
            {
                continue;
            }

            var sourcePort = ResolveOutputPort(edge, source);
            var start = PortPosition(source, sourcePort, isInput: false);
            var end = PortPosition(target, null, isInput: true);
            var path = BuildBezierPoints(start, end);
            _edgePaths.Add((edge.Id, path));

            var isActive = ActiveEdges?.Contains(edge.Id) == true;
            var isSelected = SelectedEdgeId == edge.Id;
            var color = isSelected
                ? accentBrush
                : isActive
                    ? ResolveBrush("App.State.Ready", Color.FromRgb(0x3F, 0xA6, 0x5A))
                    : secondaryBrush;
            var pen = new Pen(color, isSelected ? 2.6 : isActive ? 2.4 : 1.4);
            pen.Freeze();
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(path[0], isFilled: false, isClosed: false);
                ctx.BezierTo(path[1], path[2], path[3], isStroked: true, isSmoothJoin: false);
            }

            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);

            // 箭头
            var arrow = BuildArrow(path[2], path[3]);
            dc.DrawGeometry(color, null, arrow);

            if (edge.BranchId is { Length: > 0 } branch)
            {
                var mid = MidPoint(path);
                var text = CreateText(branch, 10, secondaryBrush);
                dc.DrawText(text, new Point(mid.X - text.Width / 2, mid.Y - 14));
            }
        }

        // 节点
        foreach (var node in Definition.Nodes)
        {
            DrawNode(dc, node, borderBrush, textBrush, secondaryBrush, accentBrush, surfaceAlt);
        }

        // 连线橡皮筋
        if (_connecting is { } connecting)
        {
            var pen = new Pen(accentBrush, 1.6) { DashStyle = DashStyles.Dash };
            pen.Freeze();
            dc.DrawLine(pen, connecting.Start, _connectCurrent);
        }

        dc.Pop();
        dc.Pop();
    }

    private void DrawNode(
        DrawingContext dc,
        WorkflowNode node,
        Brush borderBrush,
        Brush textBrush,
        Brush secondaryBrush,
        Brush accentBrush,
        Brush surfaceAlt)
    {
        var rect = new Rect(node.X, node.Y, NodeWidth, NodeHeight);
        _nodeRects.Add((node.Id, rect));

        var descriptor = NodeTypeCatalog.Find(node.TypeId);
        var state = RunStates?.GetValueOrDefault(node.Id);
        var stateColor = StateColor(state);
        var isSelected = SelectedNodeId == node.Id;

        var fill = surfaceAlt;
        var border = isSelected ? accentBrush : state is null ? borderBrush : stateColor;
        var borderThickness = isSelected ? 2.4 : state is null ? 1.0 : 2.0;

        dc.DrawRoundedRectangle(fill, new Pen(border, borderThickness), rect, 8, 8);

        // 类别色条
        var categoryBrush = new SolidColorBrush(CategoryColor(descriptor?.Category));
        categoryBrush.Freeze();
        var stripRect = new Rect(rect.X, rect.Y, 6, rect.Height);
        dc.PushClip(new RectangleGeometry(rect, 8, 8));
        dc.DrawRectangle(categoryBrush, null, stripRect);
        dc.Pop();

        var glyph = CreateText(descriptor?.Glyph ?? "?", 16, textBrush);
        dc.DrawText(glyph, new Point(rect.X + 14, rect.Y + 8));

        var name = CreateText(node.Name, 13, textBrush, FontWeights.SemiBold, NodeWidth - 60);
        dc.DrawText(name, new Point(rect.X + 40, rect.Y + 9));

        var typeText = CreateText(
            descriptor is null ? node.TypeId : descriptor.DisplayName,
            10.5,
            secondaryBrush,
            FontWeights.Normal,
            NodeWidth - 56);
        dc.DrawText(typeText, new Point(rect.X + 14, rect.Y + 34));

        var statusText = NodeStatusTexts?.GetValueOrDefault(node.Id);
        if (state is not null || statusText is not null)
        {
            var label = statusText ?? state.ToString();
            var status = CreateText($"{StateGlyph(state)} {label}", 10.5, stateColor, FontWeights.Normal, NodeWidth - 56);
            dc.DrawText(status, new Point(rect.X + 14, rect.Y + 52));
        }

        if (node.IsBreakpoint)
        {
            dc.DrawEllipse(
                ResolveBrush("App.State.Error", Color.FromRgb(0xD0, 0x50, 0x50)),
                null,
                new Point(rect.Right - 12, rect.Y + 12),
                5,
                5);
        }

        // 输入端口
        var inputCenter = new Point(rect.X, rect.Y + rect.Height / 2);
        dc.DrawEllipse(ResolveBrush("App.Window.Background", Color.FromRgb(0x1B, 0x1D, 0x21)), new Pen(border, 1.2), inputCenter, 4.5, 4.5);
        _portPoints.Add((node.Id, NodeTypeCatalog.DefaultInputPort, true, inputCenter));

        // 输出端口
        var ports = ResolveOutputPortNames(node);
        for (var i = 0; i < ports.Count; i++)
        {
            var portName = ports[i];
            var center = new Point(
                rect.Right,
                rect.Y + 20 + i * 18);
            dc.DrawEllipse(ResolveBrush("App.Window.Background", Color.FromRgb(0x1B, 0x1D, 0x21)), new Pen(border, 1.2), center, 4.5, 4.5);
            _portPoints.Add((node.Id, portName, false, center));

            if (Zoom > 0.7 && ports.Count > 1)
            {
                var label = CreateText(portName, 9, secondaryBrush, FontWeights.Normal, 90);
                dc.DrawText(label, new Point(center.X + 8, center.Y - 7));
            }
        }
    }

    private static List<string> ResolveOutputPortNames(WorkflowNode node)
    {
        if (WorkflowValidatorBridge.GetBranchIds(node) is { Count: > 0 } branches)
        {
            return branches.ToList();
        }

        return new List<string> { NodeTypeCatalog.DefaultOutputPort };
    }

    private static string ResolveOutputPort(WorkflowEdge edge, WorkflowNode sourceNode)
    {
        var ports = ResolveOutputPortNames(sourceNode);
        if (edge.SourcePort == NodeTypeCatalog.ErrorPort)
        {
            return ports.Contains(NodeTypeCatalog.ErrorPort) ? NodeTypeCatalog.ErrorPort : ports[0];
        }

        if (edge.BranchId is { Length: > 0 } branch && ports.Contains(branch))
        {
            return branch;
        }

        return ports.Contains(NodeTypeCatalog.DefaultOutputPort) ? NodeTypeCatalog.DefaultOutputPort : ports[0];
    }

    private static Point PortPosition(WorkflowNode node, string? port, bool isInput)
    {
        var rect = new Rect(node.X, node.Y, NodeWidth, NodeHeight);
        if (isInput)
        {
            return new Point(rect.X, rect.Y + rect.Height / 2);
        }

        var ports = ResolveOutputPortNames(node);
        var index = port is null ? 0 : Math.Max(0, ports.IndexOf(port));
        return new Point(rect.Right, rect.Y + 20 + index * 18);
    }

    private static Point[] BuildBezierPoints(Point start, Point end)
    {
        var dx = Math.Max(40, Math.Abs(end.X - start.X) * 0.45);
        return new[]
        {
            start,
            new Point(start.X + dx, start.Y),
            new Point(end.X - dx, end.Y),
            end,
        };
    }

    private static Point MidPoint(Point[] bezierPoints)
        // 纯数学求中点（t=0.5）。此前用 GetFlattenedPathGeometry().Figures[0]，
        // 部分分支连线会得到空 Figures，随后在渲染回调中抛异常导致进程崩溃（见回归测试）。
        => Bezier(bezierPoints, 0.5);

    private static Geometry BuildArrow(Point control, Point tip)
    {
        var angle = Math.Atan2(tip.Y - control.Y, tip.X - control.X);
        const double length = 9;
        const double spread = 0.45;
        var p1 = new Point(tip.X - length * Math.Cos(angle - spread), tip.Y - length * Math.Sin(angle - spread));
        var p2 = new Point(tip.X - length * Math.Cos(angle + spread), tip.Y - length * Math.Sin(angle + spread));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(tip, true, true);
            ctx.LineTo(p1, true, false);
            ctx.LineTo(p2, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private FormattedText CreateText(
        string text,
        double size,
        Brush brush,
        FontWeight? weight = null,
        double maxWidth = double.PositiveInfinity)
        => new(
            text ?? string.Empty,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size,
            brush,
            1.25)
        {
            // WPF 要求带 Trimming 的 FormattedText 使用有限宽度（∞ 会在绘制时抛参数异常）。
            MaxTextWidth = double.IsFinite(maxWidth) ? maxWidth : 512,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

    private Brush ResolveBrush(string key, Color fallback)
        => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private static Brush StateColor(NodeRunState? state)
        => state switch
        {
            NodeRunState.Succeeded => new SolidColorBrush(Color.FromRgb(0x3F, 0xA6, 0x5A)),
            NodeRunState.Running or NodeRunState.WaitingForUser => new SolidColorBrush(Color.FromRgb(0xD9, 0xA0, 0x3C)),
            NodeRunState.Failed or NodeRunState.TimedOut => new SolidColorBrush(Color.FromRgb(0xD0, 0x50, 0x50)),
            NodeRunState.PausedAtBreakpoint => new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)),
            NodeRunState.Canceled => new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0x9E)),
            NodeRunState.Skipped => new SolidColorBrush(Color.FromRgb(0x5A, 0x61, 0x6B)),
            _ => new SolidColorBrush(Color.FromRgb(0xAA, 0xB0, 0xB8)),
        };

    private static string StateGlyph(NodeRunState? state)
        => state switch
        {
            NodeRunState.Succeeded => "●",
            NodeRunState.Running => "▶",
            NodeRunState.Failed => "✕",
            NodeRunState.TimedOut => "⏱",
            NodeRunState.Skipped => "–",
            NodeRunState.Canceled => "⊘",
            NodeRunState.PausedAtBreakpoint => "⏸",
            NodeRunState.WaitingForUser => "✋",
            _ => "○",
        };

    private static Color CategoryColor(string? category)
        => category switch
        {
            "基础" => Color.FromRgb(0x7A, 0x8A, 0xA8),
            "数据处理" => Color.FromRgb(0x4C, 0x8D, 0xFF),
            "控制流" => Color.FromRgb(0x9B, 0x7B, 0xE8),
            "模型" => Color.FromRgb(0x3F, 0xA6, 0x5A),
            "校验" => Color.FromRgb(0xD9, 0xA0, 0x3C),
            "算法" => Color.FromRgb(0x2F, 0xB3, 0xB3),
            "交互" => Color.FromRgb(0xE0, 0x8E, 0x4A),
            "组合" => Color.FromRgb(0xC0, 0x6A, 0xC0),
            _ => Color.FromRgb(0x8A, 0x92, 0x9E),
        };

    // ── 交互 ─────────────────────────────────────────────

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (Definition is null)
        {
            return;
        }

        var point = CanvasPoint(e.GetPosition(this));

        if (e.ClickCount == 2)
        {
            foreach (var (nodeId, rect) in _nodeRects)
            {
                if (rect.Contains(point))
                {
                    NodeDoubleClicked?.Invoke(this, nodeId);
                    e.Handled = true;
                    return;
                }
            }
        }

        // 端口（优先）：开始连线
        foreach (var port in _portPoints)
        {
            if ((port.Center - point).Length <= 9)
            {
                if (port.IsInput)
                {
                    continue;
                }

                _connecting = (port.NodeId, port.Port, port.Center);
                _connectCurrent = point;
                CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        // 节点：选择 + 拖动
        foreach (var (nodeId, rect) in _nodeRects)
        {
            if (rect.Contains(point))
            {
                SelectedNodeId = nodeId;
                SelectedEdgeId = null;
                _draggingNodeId = nodeId;
                _dragOffset = new Point(point.X - rect.X, point.Y - rect.Y);
                CaptureMouse();
                var node = Definition.FindNode(nodeId);
                if (node is not null)
                {
                    NodeDragStarted?.Invoke(this, nodeId);
                }

                SelectionChanged?.Invoke(this, nodeId);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        // 连线：选择
        foreach (var (edgeId, path) in _edgePaths)
        {
            if (HitEdge(point, path))
            {
                SelectedEdgeId = edgeId;
                SelectedNodeId = null;
                SelectionChanged?.Invoke(this, null);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        // 空白：平移
        _isPanning = true;
        _panStart = e.GetPosition(this);
        _panStartX = PanX;
        _panStartY = PanY;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = CanvasPoint(e.GetPosition(this));
        var screen = e.GetPosition(this);

        if (_connecting is { } connecting)
        {
            _connectCurrent = point;
            InvalidateVisual();
            return;
        }

        if (_draggingNodeId is { } nodeId && e.LeftButton == MouseButtonState.Pressed)
        {
            var x = Math.Round(point.X - _dragOffset.X);
            var y = Math.Round(point.Y - _dragOffset.Y);
            NotifyNodeMoved(nodeId, x, y);
            NodeMoved?.Invoke(this, nodeId);
            return;
        }

        if (_isPanning && e.LeftButton == MouseButtonState.Pressed)
        {
            PanX = _panStartX + (screen.X - _panStart.X);
            PanY = _panStartY + (screen.Y - _panStart.Y);
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
        var point = e.GetPosition(this);

        if (_connecting is { } connecting)
        {
            var canvasPoint = CanvasPoint(point);
            string? targetNode = null;
            var targetPort = NodeTypeCatalog.DefaultInputPort;
            foreach (var (nodeId, rect) in _nodeRects)
            {
                if (rect.Contains(canvasPoint))
                {
                    targetNode = nodeId;
                    break;
                }
            }

            if (targetNode is not null && targetNode != connecting.NodeId)
            {
                ConnectionRequested?.Invoke(this, new WorkflowConnectionRequest(
                    connecting.NodeId,
                    connecting.Port,
                    targetNode,
                    targetPort));
            }

            _connecting = null;
            InvalidateVisual();
        }

        _draggingNodeId = null;
        _isPanning = false;
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        var newZoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - Zoom) < 0.0001)
        {
            return;
        }

        var cursor = e.GetPosition(this);
        var canvasX = (cursor.X - PanX) / Zoom;
        var canvasY = (cursor.Y - PanY) / Zoom;
        Zoom = newZoom;
        PanX = cursor.X - canvasX * Zoom;
        PanY = cursor.Y - canvasY * Zoom;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        // 右键：取消当前连线拖拽
        if (_connecting is not null)
        {
            _connecting = null;
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Delete)
        {
            DeleteRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (!e.Data.GetDataPresent(NodeTypeDataFormat))
        {
            return;
        }

        if (e.Data.GetData(NodeTypeDataFormat) is not string typeId)
        {
            return;
        }

        var point = CanvasPoint(e.GetPosition(this));
        NodeDropped?.Invoke(this, (typeId, point.X - NodeWidth / 2, point.Y - NodeHeight / 2));
        e.Handled = true;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = e.Data.GetDataPresent(NodeTypeDataFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private Point CanvasPoint(Point screen)
        => new((screen.X - PanX) / Zoom, (screen.Y - PanY) / Zoom);

    private static bool HitEdge(Point point, Point[] bezierPoints)
    {
        var previous = bezierPoints[0];
        const int samples = 24;
        for (var i = 1; i <= samples; i++)
        {
            var t = i / (double)samples;
            var current = Bezier(bezierPoints, t);
            if (DistanceToSegment(point, previous, current) <= 6)
            {
                return true;
            }

            previous = current;
        }

        return false;
    }

    private static Point Bezier(Point[] p, double t)
    {
        var mt = 1 - t;
        var x = mt * mt * mt * p[0].X + 3 * mt * mt * t * p[1].X + 3 * mt * t * t * p[2].X + t * t * t * p[3].X;
        var y = mt * mt * mt * p[0].Y + 3 * mt * mt * t * p[1].Y + 3 * mt * t * t * p[2].Y + t * t * t * p[3].Y;
        return new Point(x, y);
    }

    private static double DistanceToSegment(Point point, Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 0.001)
        {
            return (point - a).Length;
        }

        var t = Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared, 0, 1);
        var projection = new Point(a.X + t * dx, a.Y + t * dy);
        return (point - projection).Length;
    }

    /// <summary>桥接领域分支定义（避免在控件中重复解析配置）。</summary>
    private static class WorkflowValidatorBridge
    {
        public static HashSet<string>? GetBranchIds(WorkflowNode node)
        {
            switch (node.TypeId)
            {
                case NodeTypeCatalog.Condition:
                {
                    var config = WorkflowJson.TryGetConfig<ConditionNodeConfig>(node.Config, out _);
                    return config is null
                        ? null
                        : new HashSet<string>(config.Branches.Select(b => b.Id), StringComparer.Ordinal);
                }

                case NodeTypeCatalog.Parallel:
                {
                    var config = WorkflowJson.TryGetConfig<ParallelNodeConfig>(node.Config, out _);
                    return config is null
                        ? null
                        : new HashSet<string>(config.Branches.Select(b => b.Id), StringComparer.Ordinal);
                }

                case NodeTypeCatalog.Confirm:
                    return new HashSet<string>(new[] { "approved", "rejected" }, StringComparer.Ordinal);

                case NodeTypeCatalog.Rules:
                {
                    var config = WorkflowJson.TryGetConfig<RulesNodeConfig>(node.Config, out _);
                    return config is { OnFailure: RuleFailureAction.Branch }
                        ? new HashSet<string>(new[] { "pass", "fail" }, StringComparer.Ordinal)
                        : null;
                }

                default:
                    return null;
            }
        }
    }
}
