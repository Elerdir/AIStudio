using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AIStudio.App.Controls;

/// <summary>
/// Renders Markdown as native Avalonia controls, styled for the AI Studio dark theme.
/// Used in assistant chat bubbles for rich text output (code blocks, lists, headings, etc.).
/// </summary>
public class MarkdownViewer : ContentControl
{
    // ── Dependency property ───────────────────────────────────────────────────

    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownViewer, string?>(nameof(Markdown));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    // ── Static resources ──────────────────────────────────────────────────────

    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .DisableHtml()
            .Build();

    private static readonly FontFamily MonoFont =
        new FontFamily("Cascadia Code,Cascadia Mono,Consolas,Courier New,monospace");

    private static readonly IBrush FgDefault   = new SolidColorBrush(Color.Parse("#EBEBF5"));
    private static readonly IBrush FgDim       = new SolidColorBrush(Color.Parse("#888896"));
    private static readonly IBrush FgCode      = new SolidColorBrush(Color.Parse("#E879F9"));
    private static readonly IBrush FgLink      = new SolidColorBrush(Color.Parse("#818CF8"));
    private static readonly IBrush FgH1        = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush FgH2        = new SolidColorBrush(Color.Parse("#F0F0FF"));
    private static readonly IBrush FgH3        = new SolidColorBrush(Color.Parse("#DDDDEE"));
    private static readonly IBrush BgCodeBlock  = new SolidColorBrush(Color.Parse("#0F0F11"));
    private static readonly IBrush BgCodeLang   = new SolidColorBrush(Color.Parse("#1A1A20"));
    private static readonly IBrush BgQuote      = new SolidColorBrush(Color.Parse("#18181E"));
    private static readonly IBrush BorderCode   = new SolidColorBrush(Color.Parse("#2A2A32"));
    private static readonly IBrush AccentQuote  = new SolidColorBrush(Color.Parse("#7C3AED"));
    private static readonly IBrush BrushSep     = new SolidColorBrush(Color.Parse("#2A2A2E"));
    private static readonly IBrush BgCopyBtn    = new SolidColorBrush(Color.Parse("#1E1E28"));
    private static readonly IBrush FgCopyBtn    = new SolidColorBrush(Color.Parse("#555560"));
    private static readonly IBrush FgCopyBtnOk  = new SolidColorBrush(Color.Parse("#4ADE80"));

    // ── Setup ─────────────────────────────────────────────────────────────────

    static MarkdownViewer()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownViewer>((v, _) => v.Render());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Render();
    }

    // ── Core render ───────────────────────────────────────────────────────────

    private void Render()
    {
        var md = Markdown;
        if (string.IsNullOrEmpty(md))
        {
            Content = null;
            return;
        }

        var doc   = Markdig.Markdown.Parse(md, Pipeline);
        var panel = new StackPanel { Spacing = 0 };
        var first = true;

        foreach (var block in doc)
        {
            var ctrl = RenderBlock(block, first);
            if (ctrl is null) continue;
            panel.Children.Add(ctrl);
            first = false;
        }

        Content = panel;
    }

    // ── Block dispatch ────────────────────────────────────────────────────────

    private Control? RenderBlock(Block block, bool isFirst = false) => block switch
    {
        HeadingBlock      hb                       => RenderHeading(hb, isFirst),
        ParagraphBlock    pb when IsSingleImage(pb) => RenderImageBlock(pb, isFirst),
        ParagraphBlock    pb                        => RenderParagraph(pb, isFirst),
        FencedCodeBlock   fcb                       => RenderCodeBlock(fcb, isFirst),
        CodeBlock         cb                        => RenderCodeBlock(cb, isFirst),
        ListBlock         lb                        => RenderList(lb, isFirst),
        QuoteBlock        qb                        => RenderBlockquote(qb, isFirst),
        ThematicBreakBlock _                        => RenderThematicBreak(isFirst),
        _                                           => null,
    };

    private static bool IsSingleImage(ParagraphBlock pb)
    {
        var first = pb.Inline?.FirstChild;
        return first is LinkInline { IsImage: true } && first.NextSibling is null;
    }

    private Control RenderImageBlock(ParagraphBlock pb, bool isFirst)
    {
        var link = (LinkInline)pb.Inline!.FirstChild!;
        var url  = link.Url ?? string.Empty;
        var alt  = link.FirstChild is LiteralInline lt ? lt.Content.ToString() : "obrázek";

        var wrapper = new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            MaxWidth     = 420,
            Margin       = new Thickness(0, isFirst ? 0 : 8, 0, 0),
            Background   = BgCodeBlock,
            BorderBrush  = BorderCode,
            BorderThickness = new Thickness(1),
        };

        try
        {
            if (File.Exists(url))
            {
                using var stream = File.OpenRead(url);
                var bitmap = new Bitmap(stream);
                wrapper.Child = new Image
                {
                    Source  = bitmap,
                    Stretch = Stretch.Uniform,
                    MaxHeight = 300,
                };
            }
            else
            {
                wrapper.Child = new TextBlock
                {
                    Text       = $"[{alt}: {Path.GetFileName(url)}]",
                    Foreground = FgDim,
                    Padding    = new Thickness(10, 6),
                    FontSize   = 13,
                };
            }
        }
        catch
        {
            wrapper.Child = new TextBlock
            {
                Text       = $"[{alt}: {Path.GetFileName(url)}]",
                Foreground = FgDim,
                Padding    = new Thickness(10, 6),
                FontSize   = 13,
            };
        }

        return wrapper;
    }

    // ── Headings ──────────────────────────────────────────────────────────────

    private Control RenderHeading(HeadingBlock hb, bool isFirst)
    {
        var (size, weight, fg, topMargin, bottomMargin) = hb.Level switch
        {
            1 => (20.0, FontWeight.Bold,     FgH1, isFirst ? 0.0 : 16.0, 8.0),
            2 => (17.0, FontWeight.SemiBold, FgH2, isFirst ? 0.0 : 12.0, 5.0),
            _ => (14.5, FontWeight.SemiBold, FgH3, isFirst ? 0.0 : 10.0, 3.0),
        };

        var tb = new SelectableTextBlock
        {
            FontSize     = size,
            FontWeight   = weight,
            Foreground   = fg,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, topMargin, 0, bottomMargin),
        };

        if (hb.Inline is not null)
            BuildInlines(hb.Inline, tb.Inlines);

        return tb;
    }

    // ── Paragraphs ────────────────────────────────────────────────────────────

    private Control RenderParagraph(ParagraphBlock pb, bool isFirst)
    {
        var tb = new SelectableTextBlock
        {
            Foreground   = FgDefault,
            FontSize     = 14,
            LineHeight   = 22,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, isFirst ? 0 : 8, 0, 0),
        };

        if (pb.Inline is not null)
            BuildInlines(pb.Inline, tb.Inlines);

        return tb;
    }

    // ── Code blocks ───────────────────────────────────────────────────────────

    private Control RenderCodeBlock(LeafBlock cb, bool isFirst)
    {
        var lang = (cb as FencedCodeBlock)?.Info?.Trim() ?? string.Empty;

        // Extrahuj surový kód
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < cb.Lines.Count; i++)
            sb.AppendLine(cb.Lines.Lines[i].ToString());
        var code = sb.ToString().TrimEnd();

        var inner = new StackPanel();

        // Hlavičkový řádek: název jazyka vlevo + tlačítko Copy vpravo
        var copyLabel = new TextBlock
        {
            Text       = "Kopírovat",
            FontSize   = 11,
            Foreground = FgCopyBtn,
        };

        var copyBtn = new Button
        {
            Content         = copyLabel,
            Background      = BgCopyBtn,
            BorderThickness = new Thickness(1),
            BorderBrush     = BorderCode,
            CornerRadius    = new CornerRadius(5),
            Padding         = new Thickness(8, 3),
            Cursor          = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };

        // Click handler — kopíruje kód + dočasně změní label na "Zkopírováno ✓"
        copyBtn.Click += async (_, _) =>
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is not null)
                    await clipboard.SetTextAsync(code);

                copyLabel.Text       = "Zkopírováno ✓";
                copyLabel.Foreground = FgCopyBtnOk;

                await Task.Delay(1500);

                copyLabel.Text       = "Kopírovat";
                copyLabel.Foreground = FgCopyBtn;
            }
            catch { /* clipboard nedostupný */ }
        };

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Background        = BgCodeLang,
        };

        // Název jazyka (vlevo, jen pokud je definován)
        var langLabel = new Border
        {
            BorderBrush     = BorderCode,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding         = new Thickness(12, 5, 12, 5),
            Child           = new TextBlock
            {
                Text       = !string.IsNullOrEmpty(lang) ? lang : "kód",
                FontFamily = MonoFont,
                FontSize   = 11,
                Foreground = FgDim,
            },
        };
        Grid.SetColumn(langLabel, 0);
        headerGrid.Children.Add(langLabel);

        // Copy tlačítko (vpravo)
        var copyWrap = new Border
        {
            BorderBrush     = BorderCode,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding         = new Thickness(8, 4),
            Child           = copyBtn,
        };
        Grid.SetColumn(copyWrap, 1);
        headerGrid.Children.Add(copyWrap);

        inner.Children.Add(headerGrid);

        // Scrollovatelný obsah kódu
        inner.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Disabled,
            Content = new SelectableTextBlock
            {
                Text         = code,
                FontFamily   = MonoFont,
                FontSize     = 13,
                LineHeight   = 20,
                Foreground   = FgDefault,
                TextWrapping = TextWrapping.NoWrap,
                Padding      = new Thickness(12, 8, 12, 8),
            },
        });

        return new Border
        {
            Background      = BgCodeBlock,
            BorderBrush     = BorderCode,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            ClipToBounds    = true,
            Margin          = new Thickness(0, isFirst ? 0 : 10, 0, 0),
            Child           = inner,
        };
    }

    // ── Lists ─────────────────────────────────────────────────────────────────

    private Control RenderList(ListBlock lb, bool isFirst)
    {
        var panel = new StackPanel
        {
            Spacing = 3,
            Margin  = new Thickness(0, isFirst ? 0 : 8, 0, 0),
        };

        var idx = lb.OrderedStart is { } s && int.TryParse(s, out var n) ? n : 1;

        foreach (var rawItem in lb)
        {
            if (rawItem is not ListItemBlock item) continue;

            var bullet = lb.IsOrdered ? $"{idx++}." : "•";

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("18,*"),
            };

            row.Children.Add(new TextBlock
            {
                Text              = bullet,
                Foreground        = FgDim,
                FontSize          = 14,
                VerticalAlignment = VerticalAlignment.Top,
                Margin            = new Thickness(2, 2, 0, 0),
            });

            var contentPanel = new StackPanel { Spacing = 4 };
            var firstInItem  = true;
            foreach (var subBlock in item)
            {
                var ctrl = RenderBlock(subBlock, isFirst: firstInItem);
                if (ctrl is null) continue;

                // Suppress top margin for first element inside an item
                if (firstInItem)
                    ctrl.Margin = new Thickness(ctrl.Margin.Left, 0,
                                               ctrl.Margin.Right, ctrl.Margin.Bottom);

                contentPanel.Children.Add(ctrl);
                firstInItem = false;
            }

            Grid.SetColumn(contentPanel, 1);
            row.Children.Add(contentPanel);
            panel.Children.Add(row);
        }

        return panel;
    }

    // ── Blockquotes ───────────────────────────────────────────────────────────

    private Control RenderBlockquote(QuoteBlock qb, bool isFirst)
    {
        var innerPanel = new StackPanel
        {
            Spacing = 4,
            Margin  = new Thickness(10, 6, 8, 6),
        };

        var first = true;
        foreach (var sub in qb)
        {
            var ctrl = RenderBlock(sub, first);
            if (ctrl is null) continue;
            innerPanel.Children.Add(ctrl);
            first = false;
        }

        return new Border
        {
            Background      = BgQuote,
            BorderBrush     = AccentQuote,
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius    = new CornerRadius(0, 6, 6, 0),
            Margin          = new Thickness(0, isFirst ? 0 : 8, 0, 0),
            Child           = innerPanel,
        };
    }

    // ── Thematic break ────────────────────────────────────────────────────────

    private static Control RenderThematicBreak(bool isFirst) => new Border
    {
        Height     = 1,
        Background = BrushSep,
        Margin     = new Thickness(0, isFirst ? 4 : 12, 0, 12),
    };

    // ── Inline rendering ──────────────────────────────────────────────────────

    private void BuildInlines(ContainerInline container, InlineCollection? inlines)
    {
        if (inlines is null) return;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    inlines.Add(new Run(literal.Content.ToString()));
                    break;

                case EmphasisInline em:
                    var span = BuildEmphasisSpan(em);
                    BuildInlines(em, span.Inlines);
                    inlines.Add(span);
                    break;

                case CodeInline code:
                    inlines.Add(new Run(code.Content)
                    {
                        FontFamily = MonoFont,
                        FontSize   = 13,
                        Foreground = FgCode,
                    });
                    break;

                case LinkInline link:
                    var linkSpan = new Span
                    {
                        Foreground      = FgLink,
                        TextDecorations = TextDecorations.Underline,
                    };
                    if (link.FirstChild is LiteralInline linkText)
                        linkSpan.Inlines.Add(new Run(linkText.Content.ToString()));
                    else
                        BuildInlines(link, linkSpan.Inlines);
                    inlines.Add(linkSpan);
                    break;

                case LineBreakInline lb when lb.IsHard:
                    inlines.Add(new LineBreak());
                    break;

                case ContainerInline ci:
                    // Catch-all for nested containers we don't specifically handle
                    BuildInlines(ci, inlines);
                    break;
            }
        }
    }

    private static Span BuildEmphasisSpan(EmphasisInline em)
    {
        var span = new Span();

        // Bold: ** or __
        if (em.DelimiterChar is '*' or '_' && em.DelimiterCount == 2)
            span.FontWeight = FontWeight.Bold;

        // Italic: * or _
        else if (em.DelimiterChar is '*' or '_' && em.DelimiterCount == 1)
            span.FontStyle = FontStyle.Italic;

        // Strikethrough: ~~ (UseAdvancedExtensions)
        else if (em.DelimiterChar == '~')
            span.TextDecorations = TextDecorations.Strikethrough;

        return span;
    }
}
