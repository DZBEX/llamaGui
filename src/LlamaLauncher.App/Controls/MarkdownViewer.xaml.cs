using System.Windows;
using System.Windows.Controls;

namespace LlamaLauncher.App.Controls;

/// <summary>
/// 受控 Markdown 渲染（Markdig → WPF FlowDocument）：
/// 不使用 WebView2、不执行脚本；HTML 一律按纯文本显示；
/// 代码块带语言标签与复制按钮（复制失败通过 CodeCopied 事件反馈）。
/// </summary>
public partial class MarkdownViewer : UserControl
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownViewer),
        new PropertyMetadata(string.Empty, OnMarkdownChanged));

    public MarkdownViewer()
    {
        InitializeComponent();
        Viewer.Document = MarkdownRenderer.Render(string.Empty, OnCodeCopy);
    }

    /// <summary>复制代码回调：返回 (成功, 错误消息)。应用启动时注入实际实现。</summary>
    public static Func<string, (bool Success, string? Error)>? CodeCopyHandler { get; set; }

    /// <summary>复制结果事件（供页面转发到状态栏）。</summary>
    public static event Action<bool, string?>? CodeCopied;

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownViewer viewer)
        {
            viewer.Render();
        }
    }

    private void Render()
    {
        Viewer.Document = MarkdownRenderer.Render(Markdown ?? string.Empty, OnCodeCopy);
    }

    private static (bool Success, string? Error) OnCodeCopy(string code)
    {
        (bool Success, string? Error) result;
        if (CodeCopyHandler is not null)
        {
            result = CodeCopyHandler(code);
        }
        else
        {
            try
            {
                Clipboard.SetText(code);
                result = (true, null);
            }
            catch (Exception ex)
            {
                result = (false, ex.Message);
            }
        }

        CodeCopied?.Invoke(result.Success, result.Error);
        return result;
    }
}
