using Avalonia.Controls;

namespace UltraLogViewer.Desktop;

/// <summary>
/// Auto-highlight: text selected in the details pane (or a word double-clicked in a row, see <see cref="Controls.LogGrid"/>)
/// is highlighted wherever it occurs in the rows on screen, until the selection clears.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Selections longer than this are not worth looking for (a whole stack trace, say).</summary>
    private const int AutoHighlightMax = 200;

    private void WireAutoHighlight()
    {
        _detail.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.SelectionStartProperty && e.Property != TextBox.SelectionEndProperty) return;
            var sel = AutoHighlightText(_detail.SelectedText);
            if (sel is not null) _grid.AutoHighlight = sel;
            else if (!_grid.AutoHighlightByGrid) _grid.AutoHighlight = null;
        };
    }

    /// <summary>The selected text worth highlighting: one line, trimmed, 2 to 200 characters; else null.</summary>
    internal static string? AutoHighlightText(string? selected)
    {
        var s = selected?.Trim();
        return s is { Length: >= 2 and <= AutoHighlightMax } && !s.Contains('\n') ? s : null;
    }

    internal void SelectDetailTextForTest(int start, int end)
    {
        _detail.SelectionStart = start;
        _detail.SelectionEnd = end;
    }

    internal string DetailTextForTest => _detail.Text ?? "";
}
