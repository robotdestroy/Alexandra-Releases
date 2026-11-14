using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.TaskLists;
using Alexandra.Infrastructure;
using MdTable = Markdig.Extensions.Tables.Table;
using MdRow = Markdig.Extensions.Tables.TableRow;
using MdCell = Markdig.Extensions.Tables.TableCell;
using Block = System.Windows.Documents.Block;
using Inline = System.Windows.Documents.Inline;

namespace Alexandra.App;

internal sealed record SessionLogDocument(FlowDocument Document, IReadOnlyList<SessionPhoto> Photos, SessionLogHeaderView? Header);

/// <summary>Native, read-only Markdown. Never renders HTML or automatically downloads anything.</summary>
internal sealed class SessionLogMarkdown(string logPath, Action<string> activateLink)
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml()
        .UsePipeTables().UseTaskLists().UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough).Build();
    private readonly List<SessionPhoto> _photos = [];
    private readonly HashSet<string> _photoPaths = new(StringComparer.OrdinalIgnoreCase);
    private static Brush Accent => (Brush)Application.Current.FindResource("TextPrimaryBrush");
    private static Brush Panel => (Brush)Application.Current.FindResource("PanelBrush");

    public SessionLogDocument Render(string markdown, double availableWidth = 720)
    {
        _photos.Clear(); _photoPaths.Clear();
        var document = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 17,
            Foreground = Accent, Background = Brushes.Transparent, PagePadding = new Thickness(8),
            ColumnWidth = double.PositiveInfinity };
        SessionLogHeaderView? header = null;
        if (SessionLogPresentation.ReadHeader(markdown) is { } metadata)
        {
            header = new SessionLogHeaderView(metadata, availableWidth);
            document.Blocks.Add(header.Section);
            markdown = metadata.Body;
        }
        foreach (var block in Markdown.Parse(markdown, Pipeline)) document.Blocks.Add(RenderBlock(block));
        return new(document, _photos.ToArray(), header);
    }

    private Block RenderBlock(Markdig.Syntax.Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var title = Paragraph(heading.Inline);
                title.FontSize = heading.Level == 1 ? 24 : heading.Level == 2 ? 21 : 18;
                title.FontWeight = FontWeights.SemiBold; title.Margin = new(0, heading.Level == 1 ? 0 : 18, 0, 10);
                return title;
            case ParagraphBlock paragraph: return Paragraph(paragraph.Inline);
            case ListBlock list:
                var items = new System.Windows.Documents.List { MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Padding = new Thickness(24, 0, 0, 0), Margin = new Thickness(0, 4, 0, 12) };
                if (int.TryParse(list.OrderedStart, out var start) && start > 0) items.StartIndex = start;
                foreach (var child in list.OfType<ListItemBlock>())
                {
                    var item = new ListItem();
                    foreach (var nested in child) item.Blocks.Add(RenderBlock(nested));
                    items.ListItems.Add(item);
                }
                return items;
            case QuoteBlock quote:
                var section = new Section { BorderThickness = new Thickness(3, 0, 0, 0), BorderBrush = Accent,
                    Padding = new Thickness(12, 4, 0, 4), Margin = new Thickness(0, 6, 0, 12) };
                foreach (var nested in quote) section.Blocks.Add(RenderBlock(nested));
                return section;
            case CodeBlock code:
                return new Paragraph(new Run(code.Lines.ToString())) { FontFamily = new FontFamily("Consolas"), FontSize = 14,
                    Background = Panel, Padding = new Thickness(10), Margin = new Thickness(0, 6, 0, 12) };
            case ThematicBreakBlock:
                return new Paragraph { BorderBrush = Accent, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 10, 0, 10), FontSize = 1 };
            case MdTable table:
                var result = new System.Windows.Documents.Table { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 12) };
                foreach (var _ in table.ColumnDefinitions) result.Columns.Add(new TableColumn());
                var rows = new TableRowGroup(); result.RowGroups.Add(rows);
                foreach (var row in table.OfType<MdRow>())
                {
                    var target = new TableRow(); rows.Rows.Add(target);
                    foreach (var cell in row.OfType<MdCell>())
                    {
                        var targetCell = new TableCell { Padding = new Thickness(8), Background = row.IsHeader ? Panel : Brushes.Transparent,
                            FontWeight = row.IsHeader ? FontWeights.SemiBold : FontWeights.Normal, BorderBrush = Panel, BorderThickness = new Thickness(0, 0, 0, 1) };
                        foreach (var nested in cell) targetCell.Blocks.Add(RenderBlock(nested));
                        target.Cells.Add(targetCell);
                    }
                }
                return result;
            case ContainerBlock container:
                var group = new Section();
                foreach (var nested in container) group.Blocks.Add(RenderBlock(nested));
                return group;
            case LeafBlock leaf: return Paragraph(leaf.Inline);
            default: return new Paragraph();
        }
    }

    private Paragraph Paragraph(ContainerInline? content)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10), LineHeight = 25 };
        if (content is not null) AddInlines(paragraph.Inlines, content);
        return paragraph;
    }

    private void AddInlines(InlineCollection output, ContainerInline source)
    {
        foreach (var inline in source)
        {
            switch (inline)
            {
                case LiteralInline literal: output.Add(new Run(literal.Content.ToString())); break;
                case CodeInline code: output.Add(new Run(code.Content) { FontFamily = new FontFamily("Consolas"), Background = Panel }); break;
                case LineBreakInline line: output.Add(line.IsHard ? new LineBreak() : new Run(" ")); break;
                case TaskList task: output.Add(new Run(task.Checked ? "☑ " : "☐ ")); break;
                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                    else if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.Bold;
                    else span.FontStyle = FontStyles.Italic;
                    AddInlines(span.Inlines, emphasis); output.Add(span); break;
                case LinkInline link: output.Add(RenderLink(link)); break;
                case AutolinkInline auto:
                    output.Add(CreateLink(auto.Url, new Run(auto.Url))); break;
                case ContainerInline nested: AddInlines(output, nested); break;
            }
        }
    }

    private Inline RenderLink(LinkInline link)
    {
        var url = link.Url ?? "";
        var label = new Span(); AddInlines(label.Inlines, link);
        if (SessionLogMedia.TryPhotoPath(url, logPath, out var path))
        {
            var caption = new TextRange(label.ContentStart, label.ContentEnd).Text.Trim();
            if (string.IsNullOrEmpty(caption) || caption.Equals("View photo", StringComparison.OrdinalIgnoreCase))
            {
                // Generated journals put the time, optional caption and location before the photo link.
                var context = link.Parent is null ? "" : string.Concat(link.Parent.TakeWhile(i => i != link)
                    .OfType<LiteralInline>().Select(i => i.Content.ToString())).Trim().TrimEnd('—', ' ', '-');
                caption = string.IsNullOrWhiteSpace(context) ? Path.GetFileName(path) : context;
            }
            if (_photos.Count < 200 && _photoPaths.Add(path)) _photos.Add(new SessionPhoto(path, caption));
            return CreateLink(path, new Run(link.IsImage ? "View photo · " + caption : new TextRange(label.ContentStart, label.ContentEnd).Text));
        }
        // Remote images are labels, not tracking requests; ordinary web links require an explicit click.
        return link.IsImage ? new Run("[Image: " + new TextRange(label.ContentStart, label.ContentEnd).Text + "]") : CreateLink(url, label);
    }

    private Inline CreateLink(string url, Inline label)
    {
        if (!SessionLogMedia.TryPhotoPath(url, logPath, out _) && !SessionLogMedia.TryWebLink(url, out _)) return label;
        var link = new Hyperlink(label) { Foreground = Accent, ToolTip = url, Cursor = System.Windows.Input.Cursors.Hand };
        link.Click += (_, _) => activateLink(url);
        return link;
    }
}
