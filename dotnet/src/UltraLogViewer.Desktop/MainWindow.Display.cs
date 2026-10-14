using Avalonia.Controls;
using Avalonia.Input;

namespace UltraLogViewer.Desktop;

/// <summary>Display options: level styling, token colours, density, wrap, time column modes, details dock.</summary>
public sealed partial class MainWindow
{
    private readonly List<(MenuItem Item, Func<bool> Get)> _checks = [];

    private static readonly (string Mode, string Title)[] TimeModes =
    [
        ("original", "Time as written"),
        ("full", "Full date and time"),
        ("time", "Time of day"),
        ("relative", "Relative to selected row"),
        ("delta", "Delta from previous row"),
    ];

    /// <summary>A checkable View-menu item whose tick follows <paramref name="get"/>.</summary>
    private MenuItem Check(string header, Func<bool> get, Action click, KeyGesture? gesture = null)
    {
        var m = new MenuItem { Header = header, InputGesture = gesture, ToggleType = MenuItemToggleType.CheckBox, IsChecked = get() };
        m.Click += (_, _) => click();
        _checks.Add((m, get));
        return m;
    }

    private void RefreshChecks()
    {
        foreach (var (item, get) in _checks) item.IsChecked = get();
    }

    /// <summary>Changes a display setting, saves it and redraws the grid.</summary>
    private void SetDisplay(Action change)
    {
        change();
        _settings.Save();
        RefreshChecks();
        _grid.LayoutChanged();
    }

    private void SetTimeMode(string mode) => SetDisplay(() => _settings.TimeMode = mode);

    private IEnumerable<Control> TimeItems(bool live)
    {
        foreach (var (mode, title) in TimeModes)
        {
            var m = mode;
            yield return live
                ? Check(title, () => _settings.TimeMode == m, () => SetTimeMode(m))
                : new MenuItem { Header = title, ToggleType = MenuItemToggleType.Radio, IsChecked = _settings.TimeMode == m, Command = Cmd(() => SetTimeMode(m)) };
        }
        yield return new Separator();
        yield return live
            ? Check("Show in UTC", () => _settings.TimeUtc, () => SetDisplay(() => _settings.TimeUtc = !_settings.TimeUtc))
            : new MenuItem { Header = "Show in UTC", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.TimeUtc, Command = Cmd(() => SetDisplay(() => _settings.TimeUtc = !_settings.TimeUtc)) };
        yield return live
            ? Check("Mark gaps over 10 s", () => _settings.GapMs > 0, ToggleGaps)
            : new MenuItem { Header = "Mark gaps over 10 s", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.GapMs > 0, Command = Cmd(ToggleGaps) };
    }

    private void ToggleGaps() => SetDisplay(() => _settings.GapMs = _settings.GapMs > 0 ? 0 : 10000);

    private MenuItem TimeMenu(MenuItem parent)
    {
        foreach (var c in TimeItems(live: true)) parent.Items.Add(c);
        return parent;
    }

    internal void SetDisplayForTest(Action<AppSettings> change) => SetDisplay(() => change(_settings));
    internal void SetDetailDockForTest(bool right) => SetDetailDock(right);
    internal Controls.LogGrid GridForTest => _grid;
    internal Controls.HistogramView HistogramForTest => _hist;
}
