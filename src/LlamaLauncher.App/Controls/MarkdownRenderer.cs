using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace LlamaLauncher.App.Controls;

/// <summary>Markdig AST → WPF FlowDocument 的受控渲染器（无脚本、外链走系统浏览器）。</summary>
internal static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static FlowDocument Render(string markdown, Func<string, (bool Success, string? Error)> copyCode)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 13,
            Foreground = Brush("App.Text.Primary", Colors.Gainsboro),
            Background = Brushes.Transparent,
        };

        try
        {
            var parsed = Markdown.Parse(markdown ?? string.Empty, Pipeline);
            foreach (var block in parsed)
            {
                AddBlock(document.Blocks, block, copyCode);
            }
        }
        catch (Exception ex)
        {
            document.Blocks.Add(new Paragraph(new Run("（Markdown 渲染失败，以下为原文）" + Environment.NewLine + markdown + Environment.NewLine + ex.Message)));
        }

        if (document.Blocks.Count == 0)
        {
            document.Blocks.Add(new Paragraph(new Run(string.Empty)));
        }

        return document;
    }

    private static void AddBlock(BlockCollection target, Markdig.Syntax.Block block, Func<string, (bool, string?)> copyCode)
    {
        switch (block)
        {
            case ParagraphBlock paragraph:
            {
                var wpfParagraph = new Paragraph { Margin = new Thickness(0, 2, 0, 6) };
                AddInlines(wpfParagraph.Inlines, paragraph.Inline);
                target.Add(wpfParagraph);
                break;
            }

            case HeadingBlock heading:
            {
                var fontSize = heading.Level switch
                {
                    1 => 19.0,
                    2 => 17.0,
                    3 => 15.0,
                    _ => 13.5,
                };
                var wpfParagraph = new Paragraph
                {
                    FontWeight = FontWeights.SemiBold,
                    FontSize = fontSize,
                    Margin = new Thickness(0, 8, 0, 4),
                };
                AddInlines(wpfParagraph.Inlines, heading.Inline);
                target.Add(wpfParagraph);
                break;
            }

            case ListBlock list:
            {
                var wpfList = new List
                {
                    MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(18, 0, 0, 6),
                };
                if (list.IsOrdered && list.OrderedStart is { Length: > 0 } start && int.TryParse(start, out var startNumber))
                {
                    wpfList.StartIndex = startNumber;
                }

                foreach (var item in list)
                {
                    if (item is ListItemBlock listItem)
                    {
                        var wpfItem = new ListItem();
                        foreach (var child in listItem)
                        {
                            if (child is ParagraphBlock itemParagraph)
                            {
                                var wpfParagraph = new Paragraph { Margin = new Thickness(0, 1, 0, 2) };
                                AddInlines(wpfParagraph.Inlines, itemParagraph.Inline);
                                wpfItem.Blocks.Add(wpfParagraph);
                            }
                            else
                            {
                                AddBlock(wpfItem.Blocks, child, copyCode);
                            }
                        }

                        wpfList.ListItems.Add(wpfItem);
                    }
                }

                target.Add(wpfList);
                break;
            }

            case QuoteBlock quote:
            {
                var section = new Section
                {
                    BorderBrush = Brush("App.Text.Secondary", Colors.Gray),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 2, 0, 2),
                    Margin = new Thickness(0, 4, 0, 6),
                };
                foreach (var child in quote)
                {
                    AddBlock(section.Blocks, child, copyCode);
                }

                target.Add(section);
                break;
            }

            case CodeBlock code when code is FencedCodeBlock or CodeBlock:
            {
                var codeText = GetCodeText(code);
                var language = (code as FencedCodeBlock)?.Info ?? string.Empty;
                target.Add(new BlockUIContainer(BuildCodeBlockUi(codeText, language, copyCode)));
                break;
            }

            case ThematicBreakBlock:
            {
                target.Add(new BlockUIContainer(new Border
                {
                    Height = 1,
                    Background = Brush("App.Border", Colors.DimGray),
                    Margin = new Thickness(0, 8, 0, 8),
                }));
                break;
            }

            case Markdig.Extensions.Tables.Table table:
            {
                target.Add(BuildTable(table, copyCode));
                break;
            }

            case HtmlBlock html:
            {
                // 不渲染 HTML：按纯文本显示，杜绝脚本注入
                var lines = ((Markdig.Syntax.LeafBlock)html).Lines;
                var text = lines.ToString() ?? string.Empty;
                target.Add(new Paragraph(new Run(text))
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Foreground = Brush("App.Text.Secondary", Colors.Gray),
                });
                break;
            }

            case ContainerBlock container:
            {
                foreach (var child in container)
                {
                    AddBlock(target, child, copyCode);
                }

                break;
            }
        }
    }

    private static string GetCodeText(CodeBlock code)
    {
        var lines = ((Markdig.Syntax.LeafBlock)code).Lines;
        return lines.ToString() ?? string.Empty;
    }

    private static FrameworkElement BuildCodeBlockUi(
        string code,
        string language,
        Func<string, (bool Success, string? Error)> copyCode)
    {
        var panel = new StackPanel();
        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 4) };

        var copyButton = new Button
        {
            Content = "复制代码",
            Padding = new Thickness(8, 2, 8, 2),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var statusText = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("App.Text.Secondary", Colors.Gray),
            Text = string.Empty,
        };
        copyButton.Click += (_, _) =>
        {
            var (success, error) = copyCode(code);
            statusText.Text = success ? "已复制" : "复制失败：" + (error ?? "未知错误");
        };

        DockPanel.SetDock(copyButton, Dock.Right);
        header.Children.Add(copyButton);
        if (!string.IsNullOrWhiteSpace(language))
        {
            header.Children.Add(new TextBlock
            {
                Text = language,
                FontSize = 11,
                Foreground = Brush("App.Text.Secondary", Colors.Gray),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        header.Children.Add(statusText);
        panel.Children.Add(header);

        panel.Children.Add(new TextBox
        {
            Text = code.TrimEnd('\n', '\r'),
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brush("App.Surface.Alt", Color.FromRgb(0x2B, 0x2F, 0x33)),
            Foreground = Brush("App.Text.Primary", Colors.Gainsboro),
            FontFamily = new FontFamily("Consolas, Cascadia Mono"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = true,
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8),
        });

        return new Border
        {
            Background = Brush("App.Surface.Alt", Color.FromRgb(0x2B, 0x2F, 0x33)),
            BorderBrush = Brush("App.Border", Colors.DimGray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 4, 0, 8),
            Child = panel,
        };
    }

    private static Table BuildTable(Markdig.Extensions.Tables.Table table, Func<string, (bool, string?)> copyCode)
    {
        var wpfTable = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 8) };
        var rowIndex = 0;
        foreach (var row in table)
        {
            if (row is not Markdig.Extensions.Tables.TableRow tableRow)
            {
                continue;
            }

            var wpfRow = new TableRow();
            if (tableRow.IsHeader)
            {
                wpfRow.FontWeight = FontWeights.SemiBold;
                wpfRow.Background = Brush("App.Surface.Alt", Color.FromRgb(0x2B, 0x2F, 0x33));
            }

            foreach (var cell in tableRow)
            {
                if (cell is not Markdig.Extensions.Tables.TableCell tableCell)
                {
                    continue;
                }

                var paragraph = new Paragraph { Margin = new Thickness(0) };
                foreach (var block in tableCell)
                {
                    if (block is ParagraphBlock content && content.Inline is not null)
                    {
                        AddInlines(paragraph.Inlines, content.Inline);
                    }
                }

                var wpfCell = new TableCell(paragraph)
                {
                    BorderBrush = Brush("App.Border", Colors.DimGray),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 3, 6, 3),
                };
                wpfRow.Cells.Add(wpfCell);
            }

            wpfTable.RowGroups.Add(new TableRowGroup());
            ((TableRowGroup)wpfTable.RowGroups[0]).Rows.Add(wpfRow);
            rowIndex++;
            if (rowIndex > 200)
            {
                break; // 防御超长表格
            }
        }

        return wpfTable;
    }

    private static void AddInlines(InlineCollection target, ContainerInline? container)
    {
        if (container is null)
        {
            return;
        }

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;

                case EmphasisInline emphasis:
                {
                    var span = new Span();
                    var delimiterCount = emphasis.DelimiterCount;
                    if (emphasis.DelimiterChar == '~')
                    {
                        span.TextDecorations = TextDecorations.Strikethrough;
                    }
                    else
                    {
                        if (delimiterCount >= 2)
                        {
                            span.FontWeight = FontWeights.Bold;
                        }

                        if (delimiterCount == 1 || delimiterCount >= 3)
                        {
                            span.FontStyle = FontStyles.Italic;
                        }
                    }

                    AddInlines(span.Inlines, emphasis);
                    target.Add(span);
                    break;
                }

                case CodeInline code:
                {
                    var span = new Span(new Run(code.Content.Replace(' ', '\u00A0')))
                    {
                        FontFamily = new FontFamily("Consolas, Cascadia Mono"),
                        FontSize = 12.5,
                        Background = Brush("App.Surface.Alt", Color.FromRgb(0x2B, 0x2F, 0x33)),
                    };
                    target.Add(span);
                    break;
                }

                case LinkInline link:
                {
                    if (link.IsImage)
                    {
                        var altText = link.FirstChild is LiteralInline literalAlt
                            ? literalAlt.Content.ToString()
                            : "图片";
                        target.Add(new Run($"[图片：{altText}]")
                        {
                            Foreground = Brush("App.Text.Secondary", Colors.Gray),
                        });
                        break;
                    }

                    if (!IsSafeUrl(link.Url))
                    {
                        // 非 http/https/mailto 链接（如 javascript:）按纯文本显示，不生成可点击元素
                        var text = new Span();
                        AddInlines(text.Inlines, link);
                        text.Inlines.Add(new Run($"（{link.Url}）")
                        {
                            Foreground = Brush("App.Text.Secondary", Colors.Gray),
                        });
                        target.Add(text);
                        break;
                    }

                    var hyperlink = new Hyperlink
                    {
                        Foreground = Brush("App.Accent", Color.FromRgb(0x4C, 0x8D, 0xFF)),
                        TextDecorations = null,
                        Cursor = Cursors.Hand,
                    };
                    var url = link.Url ?? string.Empty;
                    AddInlines(hyperlink.Inlines, link);
                    hyperlink.Click += (_, _) => OpenExternalUrl(url);
                    target.Add(hyperlink);
                    break;
                }

                case AutolinkInline autolink:
                {
                    if (!IsSafeUrl(autolink.Url))
                    {
                        target.Add(new Run(autolink.Url)
                        {
                            Foreground = Brush("App.Text.Secondary", Colors.Gray),
                        });
                        break;
                    }

                    var hyperlink = new Hyperlink(new Run(autolink.Url))
                    {
                        Foreground = Brush("App.Accent", Color.FromRgb(0x4C, 0x8D, 0xFF)),
                        TextDecorations = null,
                    };
                    var url = autolink.Url;
                    hyperlink.Click += (_, _) => OpenExternalUrl(url);
                    target.Add(hyperlink);
                    break;
                }

                case LineBreakInline lineBreak:
                    target.Add(lineBreak.IsHard ? new LineBreak() : new Run(" "));
                    break;

                case HtmlInline html:
                    // HTML 内联按纯文本显示（防注入）
                    target.Add(new Run(html.Tag)
                    {
                        Foreground = Brush("App.Text.Secondary", Colors.Gray),
                    });
                    break;

                case ContainerInline nested:
                    AddInlines(target, nested);
                    break;
            }
        }
    }

    private static void OpenExternalUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (uri.Scheme is not ("http" or "https" or "mailto"))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 打开失败静默处理；链接只允许 http/https/mailto
        }
    }

    private static bool IsSafeUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Scheme is "http" or "https" or "mailto";

    private static Brush Brush(string resourceKey, Color fallback)
    {
        if (System.Windows.Application.Current?.TryFindResource(resourceKey) is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallback);
    }
}
