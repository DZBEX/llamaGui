using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LlamaLauncher.App.Controls;
using LlamaLauncher.App.ViewModels.Lab;
using LlamaLauncher.Domain.Workflows;

namespace LlamaLauncher.App.Views;

/// <summary>工作流设计器视图：画布事件与 ViewModel 的桥接（拖放、选择同步、缩放/适配）。</summary>
public partial class WorkflowDesignerView : UserControl
{
    private const double PaletteExpandedWidth = 208;
    private const double InspectorExpandedWidth = 360;
    private const double SideRailWidth = 40;
    private WorkflowDesignerViewModel? _vm;
    private Point _libraryDragStart;
    private NodeLibraryItem? _libraryDragItem;

    public WorkflowDesignerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitializePreview();
    }

    /// <summary>
    /// 初始化画布与 ViewModel 的连接（Loaded 时调用）。
    /// 公开供离屏渲染测试在没有窗口消息循环时复用同一初始化路径。
    /// </summary>
    public void InitializePreview()
    {
        if (DataContext is not WorkflowDesignerViewModel vm)
        {
            return;
        }

        if (_vm is not null)
        {
            Detach(_vm);
        }

        _vm = vm;
        Attach(vm);
        CanvasHost.SetDefinition(vm.Definition);
        CanvasHost.FitToView();
        PushRunVisuals(vm);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            Detach(_vm);
            _vm = null;
        }
    }

    private void Attach(WorkflowDesignerViewModel vm)
    {
        vm.DefinitionChangedForCanvas += OnDefinitionChanged;
        vm.SelectionSyncRequested += OnSelectionSyncRequested;
        vm.RunVisualsChanged += OnRunVisualsChanged;
        vm.FitRequested += OnFitRequested;
        vm.ZoomRequested += OnZoomRequested;
        vm.FocusNodeRequested += OnFocusNodeRequested;

        CanvasHost.SelectionChanged += OnCanvasSelectionChanged;
        CanvasHost.NodeDragStarted += OnCanvasNodeDragStarted;
        CanvasHost.NodeMoved += OnCanvasNodeMoved;
        CanvasHost.ConnectionRequested += OnCanvasConnectionRequested;
        CanvasHost.NodeDropped += OnCanvasNodeDropped;
        CanvasHost.DeleteRequested += OnCanvasDeleteRequested;
        CanvasHost.PreviewMouseLeftButtonDown += OnCanvasFocus;
    }

    private void Detach(WorkflowDesignerViewModel vm)
    {
        vm.DefinitionChangedForCanvas -= OnDefinitionChanged;
        vm.SelectionSyncRequested -= OnSelectionSyncRequested;
        vm.RunVisualsChanged -= OnRunVisualsChanged;
        vm.FitRequested -= OnFitRequested;
        vm.ZoomRequested -= OnZoomRequested;
        vm.FocusNodeRequested -= OnFocusNodeRequested;

        CanvasHost.SelectionChanged -= OnCanvasSelectionChanged;
        CanvasHost.NodeDragStarted -= OnCanvasNodeDragStarted;
        CanvasHost.NodeMoved -= OnCanvasNodeMoved;
        CanvasHost.ConnectionRequested -= OnCanvasConnectionRequested;
        CanvasHost.NodeDropped -= OnCanvasNodeDropped;
        CanvasHost.DeleteRequested -= OnCanvasDeleteRequested;
    }

    private void OnCanvasFocus(object sender, MouseButtonEventArgs e) => CanvasHost.Focus();

    private void OnDefinitionChanged(object? sender, EventArgs e)
        => CanvasHost.SetDefinition(_vm?.Definition);

    private void OnSelectionSyncRequested(object? sender, (string? NodeId, string? EdgeId) args)
        => CanvasHost.SetSelection(args.NodeId, args.EdgeId);

    private void OnRunVisualsChanged(object? sender, EventArgs e) => PushRunVisuals(_vm);

    private void PushRunVisuals(WorkflowDesignerViewModel? vm)
    {
        if (vm is null)
        {
            return;
        }

        var states = new Dictionary<string, NodeRunState>(vm.RunStates);
        var edges = new List<string>(vm.ActiveEdgeIds);
        var texts = new Dictionary<string, string>(vm.NodeStatusTexts);
        CanvasHost.SetRunState(states, edges, texts);
    }

    private void OnFitRequested(object? sender, EventArgs e) => CanvasHost.FitToView();

    private void OnZoomRequested(object? sender, double factor) => CanvasHost.ZoomBy(factor);

    private void OnFocusNodeRequested(object? sender, string nodeId)
    {
        if (_vm?.Definition.FindNode(nodeId) is { } node)
        {
            CanvasHost.CenterOn(node.X + 108, node.Y + 39);
        }
    }

    private void OnCanvasSelectionChanged(object? sender, string? nodeId)
        => _vm?.HandleCanvasSelection(nodeId, nodeId is null ? null : _vm.SelectedEdge?.Id);

    private void OnCanvasNodeDragStarted(object? sender, string nodeId)
        => _vm?.HandleNodeDragStarted(nodeId);

    private void OnCanvasNodeMoved(object? sender, string nodeId)
        => _vm?.HandleNodeMoved(nodeId);

    private void OnCanvasConnectionRequested(object? sender, WorkflowConnectionRequest request)
        => _vm?.HandleConnectionRequested(request);

    private void OnCanvasNodeDropped(object? sender, (string TypeId, double X, double Y) args)
        => _vm?.HandleNodeDropped(args.TypeId, args.X, args.Y);

    private void OnCanvasDeleteRequested(object? sender, EventArgs e)
        => _vm?.DeleteSelectionCommand.Execute(null);

    private void OnTogglePaletteClick(object sender, RoutedEventArgs e)
        => SetPaletteExpanded(PalettePanel.Visibility != Visibility.Visible);

    private void OnToggleInspectorClick(object sender, RoutedEventArgs e)
        => SetInspectorExpanded(InspectorPanel.Visibility != Visibility.Visible);

    private void SetPaletteExpanded(bool expanded)
    {
        PalettePanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        PaletteRail.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        PaletteColumn.Width = new GridLength(expanded ? PaletteExpandedWidth : SideRailWidth);
        PaletteToggle.IsChecked = expanded;
    }

    private void SetInspectorExpanded(bool expanded)
    {
        InspectorPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        InspectorRail.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        InspectorColumn.Width = new GridLength(expanded ? InspectorExpandedWidth : SideRailWidth);
        InspectorToggle.IsChecked = expanded;
    }

    private void OnIssueDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: DesignerValidationRow row })
        {
            _vm?.SelectIssueCommand.Execute(row);
        }
    }

    private void OnLibraryItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        _libraryDragStart = e.GetPosition(this);
        _libraryDragItem = (sender as FrameworkElement)?.DataContext as NodeLibraryItem;
    }

    private void OnLibraryItemMouseMove(object sender, MouseEventArgs e)
    {
        if (_libraryDragItem is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _libraryDragStart.X) < 6 && Math.Abs(current.Y - _libraryDragStart.Y) < 6)
        {
            return;
        }

        var data = new DataObject(WorkflowCanvasControl.NodeTypeDataFormat, _libraryDragItem.Descriptor.TypeId);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
        _libraryDragItem = null;
    }

    private void OnLibraryItemMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is NodeLibraryItem item)
        {
            _vm?.AddNodeCommand.Execute(item.Descriptor.TypeId);
        }

        _libraryDragItem = null;
    }
}
