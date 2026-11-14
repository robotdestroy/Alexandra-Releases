using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Alexandra.Infrastructure;

namespace Alexandra.App;

/// <summary>Dextra-style metadata strip, kept in the document so it scrolls and remains selectable.</summary>
internal sealed class SessionLogHeaderView
{
    private readonly Table _table = new() { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 4) };
    private readonly TableCell[] _cells;
    private int _columns;
    public Section Section { get; }

    public SessionLogHeaderView(SessionLogHeader header, double availableWidth)
    {
        Section = new Section
        {
            BorderBrush = Brush("AccentBrush"), BorderThickness = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 8), Background = Brushes.Transparent
        };
        Section.Blocks.Add(new Paragraph(new Run(header.Title.ToUpperInvariant()))
        {
            FontSize = 21, FontWeight = FontWeights.Normal, LineHeight = 28,
            Foreground = Brush("AccentHoverBrush"), Margin = new Thickness(16, 14, 16, 4)
        });
        // Lead with the most useful at-a-glance facts, retaining every other metadata field.
        _cells = header.Fields.OrderBy(f => f.Label switch
        {
            "Status" => 0, "Ship" => 1, "Recorded span" => 2, "Recorded from" => 3,
            "Last game activity" => 4, "Ended" => 5, _ => 6
        }).Select(Field).ToArray();
        Section.Blocks.Add(_table);
        UpdateWidth(availableWidth);
    }

    public void UpdateWidth(double availableWidth)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0) return;
        var columns = availableWidth >= 620 ? 3 : availableWidth >= 360 ? 2 : 1;
        if (columns == _columns) return;
        _columns = columns;
        foreach (var group in _table.RowGroups)
            foreach (var row in group.Rows) row.Cells.Clear();
        _table.RowGroups.Clear(); _table.Columns.Clear();
        for (var i = 0; i < columns; i++) _table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var rows = new TableRowGroup(); _table.RowGroups.Add(rows);
        for (var i = 0; i < _cells.Length; i++)
        {
            if (i % columns == 0) rows.Rows.Add(new TableRow());
            rows.Rows[^1].Cells.Add(_cells[i]);
        }
    }

    private static TableCell Field(SessionLogHeaderField field)
    {
        var cell = new TableCell { Padding = new Thickness(16, 10, 16, 12), BorderThickness = new Thickness(0) };
        cell.Blocks.Add(new Paragraph(new Run(field.Label.ToUpperInvariant()))
        {
            FontSize = 12, FontWeight = FontWeights.SemiBold, LineHeight = 17,
            Foreground = Brush("TextSecondaryBrush"), Margin = new Thickness(0, 0, 0, 5)
        });
        cell.Blocks.Add(new Paragraph(new Run(field.Value.ToUpperInvariant()))
        {
            FontSize = 17, FontWeight = FontWeights.Normal, LineHeight = 24,
            Foreground = Brush("AccentHoverBrush"), Margin = new Thickness(0)
        });
        return cell;
    }

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}
