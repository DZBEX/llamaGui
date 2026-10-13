using System.Windows.Documents;
using LlamaLauncher.App.Controls;
using Xunit;

namespace LlamaLauncher.UiTests;

public sealed class MarkdownRendererTests
{
    private static T RunOnStaThread<T>(Func<T> action)
    {
        T? result = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw error;
        }

        return result!;
    }

    private static string ExtractText(FlowDocument document)
    {
        var range = new TextRange(document.ContentStart, document.ContentEnd);
        return range.Text;
    }

    [Fact]
    public void RendersPlainMarkdownToFlowDocument()
        => RunOnStaThread(() =>
        {
            var document = MarkdownRenderer.Render(
                "# 标题\n\n普通**加粗**文本与 `代码`。\n\n- 项目一\n- 项目二\n\n> 引用内容",
                _ => (true, null));

            var text = ExtractText(document);
            Assert.Contains("标题", text);
            Assert.Contains("加粗", text);
            Assert.Contains("项目一", text);
            Assert.Contains("引用内容", text);
            return true;
        });

    [Fact]
    public void HtmlIsRenderedAsPlainTextNotExecuted()
        => RunOnStaThread(() =>
        {
            var document = MarkdownRenderer.Render(
                "<script>alert('x')</script>\n\n正常文本",
                _ => (true, null));
            var text = ExtractText(document);
            Assert.Contains("script", text);
            Assert.Contains("正常文本", text);
            return true;
        });

    [Fact]
    public void FencedCodeBlock_ProducesCopyableTextBlock()
        => RunOnStaThread(() =>
        {
            var document = MarkdownRenderer.Render(
                "```csharp\nvar x = 1;\n```",
                _ => (true, null));

            // 代码块渲染为 BlockUIContainer（含只读 TextBox 供选择/复制）
            var container = document.Blocks.OfType<BlockUIContainer>().FirstOrDefault();
            Assert.NotNull(container);
            var textBox = FindTextBox(container!.Child);
            Assert.NotNull(textBox);
            Assert.Contains("var x = 1;", textBox!.Text);
            return true;
        });

    /// <summary>在未渲染的元素上按逻辑结构查找 TextBox（VisualTreeHelper 对未挂载元素无效）。</summary>
    private static System.Windows.Controls.TextBox? FindTextBox(object? node) => node switch
    {
        null => null,
        System.Windows.Controls.TextBox textBox => textBox,
        System.Windows.Controls.Border border => FindTextBox(border.Child),
        System.Windows.Controls.Panel panel => panel.Children
            .OfType<object>()
            .Select(FindTextBox)
            .FirstOrDefault(found => found is not null),
        System.Windows.Controls.ContentControl content => FindTextBox(content.Content),
        System.Windows.Controls.Decorator decorator => FindTextBox(decorator.Child),
        _ => null,
    };

    [Fact]
    public void TableIsRendered()
        => RunOnStaThread(() =>
        {
            var document = MarkdownRenderer.Render(
                "| A | B |\n|---|---|\n| 1 | 2 |",
                _ => (true, null));
            Assert.Contains(document.Blocks, b => b is Table);
            var text = ExtractText(document);
            Assert.Contains("A", text);
            Assert.Contains("2", text);
            return true;
        });

    [Fact]
    public void LinksAreRenderedAsHyperlinksWithSafeScheme()
        => RunOnStaThread(() =>
        {
            var document = MarkdownRenderer.Render(
                "[点我](https://example.com) 与 [坏链接](javascript:alert(1))",
                _ => (true, null));

            var hyperlinks = new List<Hyperlink>();
            Walk(document, hyperlinks);
            Assert.Single(hyperlinks); // javascript: 链接不生成超链接
            return true;
        });

    private static void Walk(FlowDocument document, List<Hyperlink> found)
    {
        foreach (var block in document.Blocks)
        {
            WalkBlock(block, found);
        }
    }

    private static void WalkBlock(Block block, List<Hyperlink> found)
    {
        if (block is Paragraph paragraph)
        {
            WalkInlines(paragraph.Inlines, found);
        }
        else if (block is Section section)
        {
            foreach (var child in section.Blocks)
            {
                WalkBlock(child, found);
            }
        }
        else if (block is List list)
        {
            foreach (var item in list.ListItems)
            {
                foreach (var child in item.Blocks)
                {
                    WalkBlock(child, found);
                }
            }
        }
    }

    private static void WalkInlines(InlineCollection inlines, List<Hyperlink> found)
    {
        foreach (var inline in inlines)
        {
            if (inline is Hyperlink hyperlink)
            {
                found.Add(hyperlink);
            }

            if (inline is Span span)
            {
                WalkInlines(span.Inlines, found);
            }
        }
    }
}
