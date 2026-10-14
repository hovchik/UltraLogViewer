using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UltraLogViewer.Core;
using UltraLogViewer.Desktop.Controls;

namespace UltraLogViewer.Desktop;

/// <summary>
/// The application window: toolbar, tabs, sidebar, the log grid with its timeline and detail pane,
/// and the status bar. Built in code so the layout and behaviour live side by side.
/// </summary>
public sealed partial class MainWindow : Window, ILogGridHost
{
    public static readonly string[] Encodings = ["auto", "utf-8", "utf-16le", "utf-16be", "windows-1252"];

    private readonly AppSettings _settings;
    private readonly List<DocumentTab> _tabs = [];
    private DocumentTab? _active;

    private readonly LogGrid _grid;
    private readonly HistogramView _hist;
    private readonly Sidebar _sidebar;
    private readonly TextBox _detail;
    private readonly Grid _detailFields = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(8, 4, 8, 6) };
    private readonly TextBlock _detailBar;
    private readonly Border _contextBanner;
    private readonly TextBlock _contextText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Popup _quickPopup;
    private readonly ListBox _quickList = new() { MaxHeight = 280, MinWidth = 260, FontSize = 12.5 };
    private List<Suggestion> _quickSuggestions = [];
    private readonly Grid _center;
    private readonly GridSplitter _detailSplitter;
    private readonly GridSplitter _sideSplitter;
    private readonly Control _welcome;
    private readonly StackPanel _recentList = new() { Spacing = 2 };
    private readonly StackPanel _tabStrip = new() { Orientation = Orientation.Horizontal };
    private readonly Border _toast;
    private readonly TextBlock _toastText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
    private IDisposable? _toastTimer;

    private readonly TextBox _search;
    private readonly ToggleButton _sCase, _sWord, _sRegex;
    private readonly TextBlock _sCount;
    private readonly TextBox _quick;
    private readonly ToggleButton _followBtn, _histBtn, _detailBtn;
    private readonly ComboBox _format;
    private readonly TextBlock _statusLeft, _statusSel, _statusLive, _statusBusy;
    private readonly Button _statusEnc, _statusFormat;
    private readonly ProgressBar _progress;
    private readonly MenuItem _recentMenu;

    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _quickDebounce;
    private readonly HashSet<DocumentTab> _refreshing = [];
    private int _tickCount;
    private bool _suppressFormatChange;
    private DateTime _lastHeavyRefresh;
    private int _searchGen;

    public MainWindow() : this(AppSettings.Load(), [])
    {
    }

    public MainWindow(AppSettings settings, string[] files)
    {
        _settings = settings;
        Title = "UltraLogViewer";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://UltraLogViewer/Assets/app.ico")));
        Width = 1380;
        Height = 860;
        MinWidth = 720;
        MinHeight = 420;
        ApplyTheme();

        // ------------------------------------------------------------ toolbar
        var open = ToolButton("Open", "Open files (Ctrl+O)", () => Run(() => PickAndOpen(false)));
        var merge = ToolButton("Merge…", "Open several files merged into one timeline (Ctrl+Shift+O)", () => Run(() => PickAndOpen(true)));

        _search = new TextBox { Watermark = "Find (Ctrl+F)", Width = 160, FontFamily = Fonts.Mono, FontSize = 12.5, VerticalContentAlignment = VerticalAlignment.Center };
        _sCase = Toggle("Aa", "Match case");
        _sWord = Toggle("ab|", "Whole word");
        _sRegex = Toggle(".*", "Regular expression");
        foreach (var t in new[] { _sCase, _sWord, _sRegex }) t.IsCheckedChanged += (_, _) => Run(() => RunSearch(false));
        _sCount = new TextBlock { MinWidth = 64, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Opacity = 0.7, Margin = new Thickness(4, 0) };
        var sPrev = ToolButton("↑", "Previous match (Shift+F3)", () => Run(() => NextHit(-1)));
        var sNext = ToolButton("↓", "Next match (F3)", () => Run(() => NextHit(1)));
        var sFilter = ToolButton("⧩", "Turn this search into a filter", SearchToFilter);
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Run(() => NextHit(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1));
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _grid!.Focus();
            }
        };

        _quick = new TextBox { Watermark = "Quick filter (Ctrl+L): words, a OR b, -x, level:error, /re/", MinWidth = 160, FontFamily = Fonts.Mono, FontSize = 12.5, VerticalContentAlignment = VerticalAlignment.Center };
        _quickDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _quickDebounce.Tick += (_, _) =>
        {
            _quickDebounce.Stop();
            CommitQuickFilter();
        };
        _quick.TextChanged += (_, _) =>
        {
            _quickDebounce.Stop();
            _quickDebounce.Start();
            if (_quick.IsFocused) UpdateSuggestions();
        };
        _quick.AddHandler(KeyDownEvent, OnQuickKey, RoutingStrategies.Tunnel);
        _quick.GotFocus += (_, _) => UpdateSuggestions();
        _quick.LostFocus += (_, _) =>
        {
            CloseSuggestions();
            if (_active is { } t && !string.IsNullOrWhiteSpace(t.Filter.Query) && t.Filter.Query == _quick.Text) RememberQuery(t.Filter.Query);
        };
        _quickList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Suggestion>((sg, _) => new DockPanel
        {
            Children =
            {
                new TextBlock { Text = sg?.Detail ?? "", Opacity = 0.6, FontSize = 11.5, Margin = new Thickness(12, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = sg?.Label ?? "", FontFamily = Fonts.Mono, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
            },
        });
        // Tunnel, so the pick happens before the item takes focus away from the text box.
        _quickList.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(true)?.DataContext is not Suggestion sg) return;
            e.Handled = true;
            AcceptSuggestion(sg);
        }, RoutingStrategies.Tunnel);
        _quickPopup = new Popup
        {
            PlacementTarget = _quick,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            IsLightDismissEnabled = false,
            Child = _quickList,
        };

        _followBtn = Toggle("Follow", "Follow new lines (Ctrl+T)");
        _followBtn.Click += (_, _) => SetFollow(_followBtn.IsChecked == true);
        _histBtn = Toggle("Timeline", "Timeline (Ctrl+Shift+H)");
        _histBtn.IsChecked = _settings.ShowHistogram;
        _histBtn.Click += (_, _) => ToggleHistogram(_histBtn.IsChecked == true);
        _detailBtn = Toggle("Details", "Details pane (Ctrl+Shift+D)");
        _detailBtn.IsChecked = _settings.ShowDetail;
        _detailBtn.Click += (_, _) => ToggleDetail(_detailBtn.IsChecked == true);
        _format = new ComboBox { Width = 150, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(_format, "Log format");
        _format.SelectionChanged += (_, _) =>
        {
            if (!_suppressFormatChange) Run(FormatChosen);
        };
        RebuildFormatItems();
        var theme = ToolButton("◐", "Toggle dark mode", CycleTheme);
        var help = ToolButton("?", "Keyboard shortcuts", () => Run(() => Dialogs.Help(this)));

        var toolbar = new DockPanel { Margin = new Thickness(8, 6) };
        var leftTools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                open, merge, Sep(),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { _search, _sCase, _sWord, _sRegex, _sCount, sPrev, sNext, sFilter } },
                Sep(),
            },
        };
        var clearBtn = ToolButton("Clear", "Hide everything so far (Ctrl+K)", ClearSoFar);
        var patternsBtn = ToolButton("Patterns", "Group messages by pattern (Ctrl+Shift+A)", TogglePatterns);
        var rightTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { patternsBtn, clearBtn, _followBtn, _histBtn, _detailBtn, _format, theme, help } };
        DockPanel.SetDock(leftTools, Dock.Left);
        DockPanel.SetDock(rightTools, Dock.Right);
        toolbar.Children.Add(leftTools);
        toolbar.Children.Add(rightTools);
        _quick.Margin = new Thickness(0, 0, 8, 0);
        toolbar.Children.Add(new Panel { Children = { _quick, _quickPopup } });

        // --------------------------------------------------------------- menu
        _recentMenu = new MenuItem { Header = "Open _recent" };
        var menu = _mainMenu = new Menu
        {
            Items =
            {
                new MenuItem
                {
                    Header = "_File",
                    Items =
                    {
                        Item("_Open…", () => Run(() => PickAndOpen(false)), new KeyGesture(Key.O, KeyModifiers.Control)),
                        Item("Open and _merge…", () => Run(() => PickAndOpen(true)), new KeyGesture(Key.O, KeyModifiers.Control | KeyModifiers.Shift)),
                        _recentMenu,
                        new Separator(),
                        Item("Open _workspace…", () => Run(PickOpenWorkspace)),
                        Item("_Save workspace…", () => Run(PickSaveWorkspace), new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Alt)),
                        new Separator(),
                        Item("_Export view…", () => Run(Export), new KeyGesture(Key.E, KeyModifiers.Control)),
                        Item("Reopen with _encoding…", () => Run(ChooseEncoding)),
                        new Separator(),
                        Item("_Close tab", () => CloseTab(_active), new KeyGesture(Key.W, KeyModifiers.Control)),
                        Item("E_xit", Close),
                    },
                },
                new MenuItem
                {
                    Header = "_Edit",
                    Items =
                    {
                        Item("_Copy selected entries", () => { if (_active is { } t) Run(() => CopyRowsAsync(t.SelectionStart, t.SelectionEnd)); }, new KeyGesture(Key.C, KeyModifiers.Control)),
                        Item("_Find", FocusSearch, new KeyGesture(Key.F, KeyModifiers.Control)),
                        Item("_Quick filter", FocusQuick, new KeyGesture(Key.L, KeyModifiers.Control)),
                        Item("_Go to line…", () => Run(GoToLine), new KeyGesture(Key.G, KeyModifiers.Control)),
                        new Separator(),
                        Item("Toggle _bookmark", ToggleBookmarkAtFocus, new KeyGesture(Key.B, KeyModifiers.Control)),
                        Item("Next bookmark", () => NextBookmark(1), new KeyGesture(Key.F2)),
                        Item("Previous bookmark", () => NextBookmark(-1), new KeyGesture(Key.F2, KeyModifiers.Shift)),
                        Item("Clear all filters", ClearFilters),
                        Item("C_lear (hide everything so far)", ClearSoFar, new KeyGesture(Key.K, KeyModifiers.Control)),
                    },
                },
                new MenuItem
                {
                    Header = "_View",
                    Items =
                    {
                        Item("_Follow new lines", () => SetFollow(!(_active?.Follow ?? false)), new KeyGesture(Key.T, KeyModifiers.Control)),
                        Item("_Timeline", () => ToggleHistogram(!_settings.ShowHistogram), new KeyGesture(Key.H, KeyModifiers.Control | KeyModifiers.Shift)),
                        Item("_Details pane", () => ToggleDetail(!_settings.ShowDetail), new KeyGesture(Key.D, KeyModifiers.Control | KeyModifiers.Shift)),
                        Item("_Sidebar", () => ToggleSidebar(!_settings.ShowSidebar)),
                        Item("Dark / light _theme", CycleTheme),
                        new Separator(),
                        Check("Tint whole rows by level", () => _settings.TintRows, () => SetDisplay(() => _settings.TintRows = !_settings.TintRows)),
                        Check("Colour tokens in messages", () => _settings.TokenColors, () => SetDisplay(() => _settings.TokenColors = !_settings.TokenColors)),
                        Check("Comfortable rows", () => _settings.Comfortable, () => SetDisplay(() => _settings.Density = _settings.Comfortable ? "compact" : "comfortable")),
                        Check("_Wrap long messages", () => _settings.Wrap, () => SetDisplay(() => _settings.Wrap = !_settings.Wrap), new KeyGesture(Key.Z, KeyModifiers.Alt)),
                        Check("Details on the right", () => DockRight, () => SetDetailDock(!DockRight)),
                        TimeMenu(new MenuItem { Header = "Time _column" }),
                        new Separator(),
                        Check("_Patterns", () => _patternsShown, TogglePatterns, new KeyGesture(Key.A, KeyModifiers.Control | KeyModifiers.Shift)),
                        GroupMenu(),
                        Check("Fold framework frames", () => _settings.FoldFrames, () => { SetDisplay(() => _settings.FoldFrames = !_settings.FoldFrames); UpdateDetail(); }),
                        new Separator(),
                        Item("Next tab", () => CycleTab(1), new KeyGesture(Key.Tab, KeyModifiers.Control)),
                    },
                },
                new MenuItem
                {
                    Header = "_Help",
                    Items =
                    {
                        Item("_Command palette", OpenPalette, new KeyGesture(Key.P, KeyModifiers.Control | KeyModifiers.Shift)),
                        Item("_Keyboard shortcuts", () => Run(() => Dialogs.Help(this))),
                        Item("_About", () => Run(() => Dialogs.Message(this, "About UltraLogViewer",
                            $"UltraLogViewer {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}\nA fast viewer for large log files, built with Avalonia and .NET {Environment.Version}."))),
                    },
                },
            },
        };
        _recentMenu.SubmenuOpened += (_, _) => RebuildRecentMenu();
        RebuildRecentMenu();

        // --------------------------------------------------------- tab strip
        var tabBar = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 1),
            BorderBrush = new SolidColorBrush(Color.Parse("#808080"), 0.25),
            Child = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _tabStrip },
            MinHeight = 32,
        };

        // ------------------------------------------------------------ center
        _grid = new LogGrid(this);
        _hist = new HistogramView { IsVisible = _settings.ShowHistogram };
        _hist.Jump += t =>
        {
            if (_active is { } tab) _grid.Select(tab.Doc.RowForTime(t), center: true);
        };
        _hist.RangeSelected += (a, b) =>
        {
            if (_active is not { } tab) return;
            tab.Filter.TimeFrom = a;
            tab.Filter.TimeTo = b;
            ApplyFilter();
        };
        _detailBar = new TextBlock { FontSize = 12, Margin = new Thickness(8, 4), TextTrimming = TextTrimming.CharacterEllipsis };
        _detail = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = Fonts.Mono,
            FontSize = 12.5,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Watermark = "Select a row to see the full entry.",
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_detail, ScrollBarVisibility.Auto);
        var copyEntry = new Button { Classes = { "mini" }, Content = "Copy entry", Margin = new Thickness(6, 2) };
        copyEntry.Click += (_, _) => Run(async () =>
        {
            if (_active is { } t && await RowAsync(t, t.Focus) is { } r && Clipboard is { } cb)
            {
                await cb.SetTextAsync(r.Text);
                Toast("Entry copied");
            }
        });
        _unfoldBtn.Click += (_, _) => UnfoldFocused();
        var detailHead = new DockPanel { Children = { copyEntry, _unfoldBtn, _detailBar } };
        DockPanel.SetDock(copyEntry, Dock.Right);
        DockPanel.SetDock(_unfoldBtn, Dock.Right);
        ScrollViewer.SetVerticalScrollBarVisibility(_detail, ScrollBarVisibility.Disabled);
        var detailBody = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel { Children = { _detailFields, _detail } },
        };
        var detailPane = new DockPanel { Children = { detailHead, detailBody } };
        DockPanel.SetDock(detailHead, Dock.Top);
        _detailSplitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Height = 5, Background = new SolidColorBrush(Color.Parse("#808080"), 0.25) };

        _contextBanner = BuildContextBanner();
        // timeline (resizable) | context banner | work area: the grid with the details pane docked bottom or right
        _histSplitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Height = 4, Background = new SolidColorBrush(Color.Parse("#808080"), 0.25) };
        _hist.Height = double.NaN;
        _center = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        _center.RowDefinitions[0].MinHeight = 0;
        _center.RowDefinitions[0].MaxHeight = 240;
        Grid.SetRow(_hist, 0);
        Grid.SetRow(_histSplitter, 1);
        Grid.SetRow(_work, 2);
        _center.Children.Add(_hist);
        _center.Children.Add(_histSplitter);
        _center.Children.Add(_work);
        var gridHost = new DockPanel { Children = { _contextBanner, BuildGridArea() } };
        DockPanel.SetDock(_contextBanner, Dock.Top);
        _work.Children.Add(gridHost);
        _work.Children.Add(_detailSplitter);
        _work.Children.Add(detailPane);
        _detailPane = detailPane;
        // never let a saved pane size (from a bigger window) squeeze the grid out
        _work.SizeChanged += (_, e) =>
        {
            _work.ColumnDefinitions[2].MaxWidth = Math.Max(200, e.NewSize.Width * 0.8);
            _work.RowDefinitions[2].MaxHeight = Math.Max(80, e.NewSize.Height * 0.8);
        };
        ApplyHistogramSize();
        ApplyDock();
        ToggleDetail(_settings.ShowDetail, save: false);

        _welcome = BuildWelcome();
        _toast = new Border
        {
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(16),
            Padding = new Thickness(12, 8),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.Parse("#1D2129"), 0.92),
            Child = _toastText,
        };
        _toastText.Foreground = Brushes.White;
        var centerHost = new Panel { Children = { _center, _welcome, _toast } };

        _sidebar = new Sidebar(this);
        var sideBorder = new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = new SolidColorBrush(Color.Parse("#808080"), 0.25),
            Child = _sidebar,
        };
        _sideSplitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Width = 4, Background = Brushes.Transparent };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        Grid.SetColumn(_sideSplitter, 1);
        Grid.SetColumn(centerHost, 2);
        body.Children.Add(sideBorder);
        body.Children.Add(_sideSplitter);
        body.Children.Add(centerHost);
        _sideBorder = sideBorder;
        ToggleSidebar(_settings.ShowSidebar, save: false);

        // -------------------------------------------------------- status bar
        _statusLeft = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        _statusSel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
        _statusFormat = new Button { Classes = { "mini" }, Padding = new Thickness(6, 0), MinHeight = 18, FontSize = 12, IsVisible = false };
        _statusFormat.Click += (_, _) => _format.IsDropDownOpen = true;
        _statusLive = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
        _statusBusy = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        _statusEnc = new Button { Classes = { "mini" }, Padding = new Thickness(6, 0), MinHeight = 18, FontSize = 12 };
        ToolTip.SetTip(_statusEnc, "Change encoding");
        _statusEnc.Click += (_, _) => Run(ChooseEncoding);
        _progress = new ProgressBar { Width = 140, Minimum = 0, Maximum = 1, IsVisible = false, VerticalAlignment = VerticalAlignment.Center, MinHeight = 4 };
        var status = new DockPanel { Margin = new Thickness(10, 3), LastChildFill = false };
        _statusCleared.Click += (_, _) => Unclear();
        ToolTip.SetTip(_statusCleared, "Show the entries hidden by Clear");
        var sl = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { _statusLeft, _statusCleared, _statusSel } };
        var sr = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _statusBusy, _progress, _statusFormat, _statusEnc, _statusLive } };
        DockPanel.SetDock(sl, Dock.Left);
        DockPanel.SetDock(sr, Dock.Right);
        status.Children.Add(sl);
        status.Children.Add(sr);
        var statusBar = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(Color.Parse("#808080"), 0.25),
            Child = status,
        };

        var root = new DockPanel();
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(tabBar, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(menu);
        root.Children.Add(toolbar);
        root.Children.Add(tabBar);
        root.Children.Add(statusBar);
        root.Children.Add(body);
        Content = root;

        // ------------------------------------------------------------- events
#pragma warning disable CS0618 // IDataObject drag-and-drop API, still supported in Avalonia 11
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var paths = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().Where(File.Exists).ToArray() ?? [];
            if (paths.Length > 0) Run(() => OpenPaths(paths, e.KeyModifiers.HasFlag(KeyModifiers.Shift) && paths.Length > 1));
        });
#pragma warning restore CS0618
        AddHandler(KeyDownEvent, OnGlobalKey, RoutingStrategies.Tunnel);
        _grid.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) Run(RefreshOverviewAsync);
        };
        _hist.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty && ((Rect)e.OldValue!).Width != ((Rect)e.NewValue!).Width) Run(RefreshHistogramAsync);
        };
        Closing += (_, _) => SaveOnClose();
        Opened += (_, _) =>
        {
            if (files.Length > 0) Run(() => OpenPaths(files, false));
        };

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();

        ActualThemeVariantChanged += (_, _) =>
        {
            RebuildTabStrip();
            _sidebar.Render(_active);
        };
        Activate(null);
    }

    private readonly Grid _work = new() { RowDefinitions = new RowDefinitions("*,Auto,Auto"), ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
    private readonly GridSplitter _histSplitter;
    private bool DockRight => _settings.DetailDock == "right";
    private readonly Control _detailPane;
    private readonly Control _sideBorder;

    public AppSettings Settings => _settings;
    public IReadOnlyList<DocumentTab> Tabs => _tabs;
    public DocumentTab? ActiveTab => _active;
    internal LogGrid LogView => _grid;

    public void HighlightsChanged() => _grid.RefreshPatterns();

    // ================================================================ helpers

    private static Button ToolButton(string text, string tip, Action click)
    {
        var b = new Button { Content = text, Classes = { "tb" }, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    private static ToggleButton Toggle(string text, string tip)
    {
        var b = new ToggleButton { Content = text, Classes = { "tb" }, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(b, tip);
        return b;
    }

    private static Control Sep() => new Border { Width = 1, Margin = new Thickness(4, 4), Background = new SolidColorBrush(Color.Parse("#808080"), 0.3) };

    private static MenuItem Item(string header, Action click, KeyGesture? gesture = null)
    {
        var m = new MenuItem { Header = header, InputGesture = gesture };
        m.Click += (_, _) => click();
        return m;
    }

    /// <summary>Runs an async UI action and turns failures into a toast instead of a crash.</summary>
    private void Run(Func<Task> fn)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await fn();
            }
            catch (Exception e)
            {
                Toast(e.Message, true);
            }
        });
    }

    public void Toast(string message, bool error = false)
    {
        _toastText.Text = message;
        _toast.Background = new SolidColorBrush(Color.Parse(error ? "#B3261E" : "#1D2129"), 0.92);
        _toast.IsVisible = true;
        _toastTimer?.Dispose();
        _toastTimer = DispatcherTimer.RunOnce(() => _toast.IsVisible = false, TimeSpan.FromSeconds(error ? 5 : 2.5));
    }

    private Control BuildWelcome()
    {
        var openBtn = new Button { Content = "Open files…", Classes = { "accent" }, Padding = new Thickness(16, 6) };
        openBtn.Click += (_, _) => Run(() => PickAndOpen(false));
        var mergeBtn = new Button { Content = "Open and merge…", Padding = new Thickness(16, 6) };
        mergeBtn.Click += (_, _) => Run(() => PickAndOpen(true));
        return new Border
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 12,
                MaxWidth = 560,
                Children =
                {
                    new TextBlock { Text = "UltraLogViewer", FontSize = 26, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock
                    {
                        Text = "Drop log files here, or open them. Hold Shift while dropping several files to merge them into one timeline. Plain text, log4j/logback/NLog/Serilog, JSON lines, syslog, Apache/Nginx, IIS W3C, logcat and .gz files are detected automatically.",
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center,
                        Opacity = 0.7,
                    },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Children = { openBtn, mergeBtn } },
                    _recentWsTitle,
                    _recentWsList,
                    new TextBlock { Text = "RECENT", Classes = { "h3" }, Margin = new Thickness(0, 12, 0, 0) },
                    _recentList,
                },
            },
        };
    }

    private void RebuildRecent()
    {
        RebuildRecentWorkspaces();
        _recentList.Children.Clear();
        foreach (var p in _settings.Recent.Take(8))
        {
            var b = new Button
            {
                Content = new TextBlock { Text = p, TextTrimming = TextTrimming.PathSegmentEllipsis },
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 3),
            };
            b.Click += (_, _) => Run(() => OpenPaths([p], false));
            _recentList.Children.Add(b);
        }
        if (_settings.Recent.Count == 0) _recentList.Children.Add(new TextBlock { Classes = { "hint" }, Text = "Nothing yet." });
    }

    private void RebuildRecentMenu()
    {
        _recentMenu.Items.Clear();
        foreach (var w in _settings.RecentWorkspaces)
            _recentMenu.Items.Add(Item(w.Replace("_", "__"), () => Run(() => OpenWorkspaceAsync(w))));
        if (_settings.RecentWorkspaces.Count > 0) _recentMenu.Items.Add(new Separator());
        foreach (var p in _settings.Recent)
            _recentMenu.Items.Add(Item(p.Replace("_", "__"), () => Run(() => OpenPaths([p], false))));
        if (_settings.Recent.Count == 0) _recentMenu.Items.Add(new MenuItem { Header = "(empty)", IsEnabled = false });
    }

    // ================================================================= tabs

    private void Activate(DocumentTab? tab)
    {
        _active = tab;
        _grid.SetTab(tab);
        _welcome.IsVisible = tab is null;
        _center.IsVisible = tab is not null;
        if (tab is null) RebuildRecent();
        UpdateTitle();
        if (_patternsShown) ShowPatterns(false);
        _search.Text = tab?.SearchPattern.Text ?? "";
        if (tab is not null)
        {
            _sCase.IsChecked = tab.SearchPattern.CaseSensitive;
            _sWord.IsChecked = tab.SearchPattern.WholeWord;
            _sRegex.IsChecked = tab.SearchPattern.Regex;
        }
        SetQuickFilterText(tab?.Filter.Query ?? "");
        _followBtn.IsChecked = tab?.Follow ?? false;
        SyncFormatCombo();
        RebuildTabStrip();
        _sidebar.Render(tab);
        _hist.Data = null;
        UpdateContextBanner();
        UpdateSearchCount();
        UpdateDetail();
        UpdateStatus();
        if (tab is { Ready: true }) Run(() => RefreshDerivedAsync(tab));
        if (tab is { Ready: true, FieldStats: null }) ScheduleFieldStats(tab);
        _grid.Focus();
    }

    private void RebuildTabStrip()
    {
        _tabStrip.Children.Clear();
        var p = Palette.Current;
        foreach (var t in _tabs)
        {
            var close = new Button { Content = "×", Padding = new Thickness(5, 0), MinHeight = 0, Background = Brushes.Transparent, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            close.Click += (_, e) =>
            {
                e.Handled = true;
                CloseTab(t);
            };
            var label = new TextBlock { Text = t.Title, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis };
            var cell = new Border
            {
                Padding = new Thickness(12, 4, 4, 4),
                BorderThickness = new Thickness(0, 0, 0, 2),
                BorderBrush = t == _active ? p.Accent : Brushes.Transparent,
                Background = t == _active ? p.Bg : Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { label, close } },
            };
            ToolTip.SetTip(cell, string.Join("\n", t.Paths));
            cell.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(cell).Properties.IsMiddleButtonPressed) CloseTab(t);
                else if (t != _active) Activate(t);
            };
            _tabStrip.Children.Add(cell);
        }
    }

    private void CycleTab(int dir)
    {
        if (_tabs.Count < 2 || _active is null) return;
        var i = (_tabs.IndexOf(_active) + dir + _tabs.Count) % _tabs.Count;
        Activate(_tabs[i]);
    }

    public void CloseTab(DocumentTab? tab)
    {
        if (tab is null) return;
        var i = _tabs.IndexOf(tab);
        if (i < 0) return;
        _tabs.RemoveAt(i);
        tab.Dispose();
        if (_active == tab) Activate(_tabs.Count == 0 ? null : _tabs[Math.Min(i, _tabs.Count - 1)]);
        else RebuildTabStrip();
    }

    // ================================================================ opening

    private async Task PickAndOpen(bool merge)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = merge ? "Open and merge log files" : "Open log files",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Log files") { Patterns = ["*.log", "*.txt", "*.json", "*.jsonl", "*.gz", "*.out", "*.err", "*.*"] }, FilePickerFileTypes.All],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0) await OpenPaths(paths, merge && paths.Length > 1);
    }

    public async Task OpenPaths(string[] paths, bool merge)
    {
        paths = paths.Select(Path.GetFullPath).ToArray();
        _settings.AddRecent(paths);
        _settings.Save();
        if (merge)
        {
            await OpenTab(paths, "auto", null);
            return;
        }
        foreach (var p in paths)
        {
            var existing = _tabs.FirstOrDefault(t => t.Paths.Length == 1 && string.Equals(t.Paths[0], p, StringComparison.Ordinal));
            if (existing is not null)
            {
                Activate(existing);
                continue;
            }
            await OpenTab([p], "auto", null);
        }
    }

    private async Task<DocumentTab?> OpenTab(string[] paths, string encoding, DocumentTab? replacing, Action<DocumentTab>? init = null)
    {
        var doc = new LogDocument(paths.Length == 1 ? Path.GetFileName(paths[0]) : "Merged");
        var tab = new DocumentTab(paths, doc) { Encoding = encoding, BusyLabel = "Indexing", BusyProgress = 0 };
        init?.Invoke(tab);
        if (replacing is not null)
        {
            tab.Filter = replacing.Filter.Clone();
            tab.FormatId = replacing.FormatId;
            var i = _tabs.IndexOf(replacing);
            _tabs[i] = tab;
            replacing.Dispose();
        }
        else _tabs.Add(tab);
        Activate(tab);
        try
        {
            for (var i = 0; i < paths.Length; i++)
            {
                var idx = i;
                var opts = new SourceOptions
                {
                    Encoding = encoding,
                    CustomFormats = _settings.CustomFormats,
                    Format = tab.FormatId.Length > 0 ? tab.FormatId : null,
                };
                await doc.AddSourceAsync(paths[i], opts, new SyncProgress(f => tab.BusyProgress = (idx + f) / paths.Length));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Toast($"Could not open {Path.GetFileName(paths[0])}: {e.Message}", true);
            if (doc.Sources.Count == 0)
            {
                CloseTab(tab);
                return null;
            }
        }
        if (!_tabs.Contains(tab)) return null; // closed while loading
        tab.Ready = true;
        tab.BusyProgress = null;
        tab.BusyLabel = "";
        tab.Info = doc.Info();
        RestoreBookmarks(tab);
        if (!tab.Filter.IsTrivial || tab.Bookmarks.Count > 0 && tab.Filter.BookmarksOnly) await ApplyFilterAsync(tab);
        if (_active == tab)
        {
            _grid.Invalidate();
            if (tab.Info.ViewCount > 0 && tab.Focus == 0) _grid.Select(0);
            SyncFormatCombo();
            _sidebar.Render(tab);
            UpdateStatus();
            await RefreshOverviewAsync();
            await RefreshHistogramAsync();
        }
        ScheduleFieldStats(tab);
        return tab;
    }

    // ================================================================== tick

    private void OnTick()
    {
        _tickCount++;
        var tab = _active;
        if (tab is not null && tab.BusyProgress is not null)
        {
            if (tab.Doc.Indexing)
            {
                // Show rows while the file is still being indexed.
                var before = tab.Info.Total;
                tab.Info = tab.Doc.Info();
                if (before == 0 && tab.Info.Total > 0) _sidebar.Render(tab);
                _grid.CountChanged();
                if (_tickCount % 4 == 0) _sidebar.UpdateCounts(tab);
            }
            UpdateStatus();
        }
        if (tab is { Ready: true, BusyProgress: null, StaleViews: true } && !_refreshing.Contains(tab) && DateTime.UtcNow - _lastHeavyRefresh > TimeSpan.FromSeconds(1.5))
            Run(() => RefreshDerivedAsync(tab));
        if (_tickCount % 2 == 0)
            foreach (var t in _tabs.ToList())
                if (t.Ready && t.BusyProgress is null && !_refreshing.Contains(t) && t.Info.Sources.Any(s => s.Live))
                    _ = TailAsync(t);
    }

    /// <summary>After new data: recompute the overview, timeline and search hits, at most every 1.5 s.</summary>
    private async Task RefreshDerivedAsync(DocumentTab tab)
    {
        _lastHeavyRefresh = DateTime.UtcNow;
        tab.StaleViews = false;
        await RefreshOverviewAsync();
        await RefreshHistogramAsync();
        if (tab.SearchResult is not null && tab == _active) await RunSearch(false);
    }

    private async Task TailAsync(DocumentTab tab)
    {
        _refreshing.Add(tab);
        try
        {
            var before = tab.Info.ViewCount;
            if (!await tab.Doc.RefreshAsync() || !_tabs.Contains(tab)) return;
            tab.Info = tab.Doc.Info();
            OnRowsAppended(tab, before, tab.Info.ViewCount);
            tab.StaleViews = true;
            UpdateBookmarkRows(tab);
            if (DateTime.UtcNow - tab.StatsTime > TimeSpan.FromSeconds(10)) ScheduleFieldStats(tab);
            if (tab != _active) return;
            _grid.Invalidate();
            if (tab.Follow) _grid.ScrollTo(int.MaxValue);
            _sidebar.UpdateCounts(tab);
            UpdateStatus();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // file temporarily locked or deleted during rotation: try again on the next tick
        }
        finally
        {
            _refreshing.Remove(tab);
        }
    }

    // ================================================================ filter

    public void SetQuickFilterText(string text)
    {
        if (_quick.Text == text) return;
        _quick.Text = text;
        _quickDebounce.Stop();
    }

    private void CommitQuickFilter()
    {
        if (_active is not { } tab) return;
        var q = _quick.Text ?? "";
        if (q == tab.Filter.Query) return;
        tab.Filter.Query = q;
        ApplyFilter();
    }

    public void ApplyFilter()
    {
        if (_active is { } tab) Run(() => ApplyFilterAsync(tab));
    }

    private void ClearFilters()
    {
        if (_active is not { } tab) return;
        tab.Filter = new FilterSpec { Context = 0 };
        SetQuickFilterText("");
        ApplyFilter();
    }

    public async Task ApplyFilterAsync(DocumentTab tab)
    {
        if (!tab.Ready) return;
        var spec = tab.Filter.Clone();
        spec.Bookmarks = tab.Bookmarks.Keys.ToHashSet();
        if (!string.IsNullOrWhiteSpace(spec.Query))
        {
            try
            {
                Query.CompileQuery(spec.Query, spec.QueryCaseSensitive);
            }
            catch (ArgumentException e)
            {
                Toast($"Quick filter: {e.Message}", true);
                return;
            }
        }
        var focusKey = (_grid.GetCached(tab.Focus) ?? (tab.Info.ViewCount > 0 ? (await Task.Run(() => tab.Doc.GetRows(tab.Focus, 1))).FirstOrDefault() : null))?.Key;
        if (tab.Doc.IgnoreFilter)
        {
            // A filter change ends "show in context".
            tab.Doc.IgnoreFilter = false;
            if (tab == _active) UpdateContextBanner();
        }
        tab.BusyLabel = "Filtering";
        tab.BusyProgress = 0;
        UpdateStatus();
        bool applied;
        try
        {
            applied = await tab.Doc.ApplyFilterAsync(spec, new SyncProgress(f => tab.BusyProgress = f));
        }
        finally
        {
            tab.BusyProgress = null;
            tab.BusyLabel = "";
        }
        if (!applied || !_tabs.Contains(tab)) return;
        tab.Info = tab.Doc.Info();
        tab.NewRows = 0;
        var row = focusKey is null ? null : tab.Doc.RowForKey(focusKey);
        tab.Focus = tab.Anchor = row ?? Math.Clamp(tab.Focus, 0, Math.Max(0, tab.Info.ViewCount - 1));
        UpdateBookmarkRows(tab);
        if (tab != _active) return;
        _grid.Invalidate();
        _grid.Reveal(tab.Focus, center: true);
        _sidebar.Render(tab);
        UpdateStatus();
        UpdateDetail();
        UpdatePill();
        RefreshChecks();
        ScheduleFieldStats(tab);
        if (tab.SearchPattern.Text.Length > 0) await RunSearch(false);
        await RefreshOverviewAsync();
        await RefreshHistogramAsync();
    }

    // ================================================================ search

    private TextPattern CurrentPattern() => new(_search.Text ?? "", _sRegex.IsChecked == true, _sCase.IsChecked == true, _sWord.IsChecked == true);

    private void FocusSearch()
    {
        _search.Focus();
        _search.SelectAll();
    }

    private void FocusQuick()
    {
        _quick.Focus();
        _quick.SelectAll();
    }

    private async Task RunSearch(bool jump)
    {
        if (_active is not { } tab || !tab.Ready) return;
        var pattern = CurrentPattern();
        tab.SearchPattern = pattern;
        var gen = ++_searchGen;
        if (pattern.Text.Length == 0)
        {
            tab.SearchResult = null;
            _grid.RefreshPatterns();
            UpdateSearchCount();
            return;
        }
        if (Query.Validate(pattern) is { } err)
        {
            _sCount.Text = "invalid";
            ToolTip.SetTip(_sCount, err);
            return;
        }
        tab.BusyLabel = "Searching";
        tab.BusyProgress = 0;
        SearchResult res;
        try
        {
            res = await tab.Doc.SearchAsync(pattern, new SyncProgress(f => tab.BusyProgress = f));
        }
        finally
        {
            tab.BusyProgress = null;
            tab.BusyLabel = "";
        }
        if (gen != _searchGen || tab != _active) return;
        tab.SearchResult = res;
        var at = Array.BinarySearch(res.Rows, tab.Focus);
        tab.SearchCurrent = at >= 0 ? at : Math.Min(~at, Math.Max(0, res.Rows.Length - 1));
        _grid.RefreshPatterns();
        UpdateSearchCount();
        UpdateStatus();
        if (jump && res.Rows.Length > 0) _grid.Select(res.Rows[tab.SearchCurrent], center: true);
    }

    private async Task NextHit(int dir)
    {
        if (_active is not { } tab) return;
        if (tab.SearchResult is null || tab.SearchPattern != CurrentPattern())
        {
            await RunSearch(true);
            return;
        }
        var hits = tab.SearchResult.Rows;
        if (hits.Length == 0) return;
        var at = Array.BinarySearch(hits, tab.Focus);
        int next;
        if (dir > 0) next = at >= 0 ? at + 1 : ~at;
        else next = (at >= 0 ? at : ~at) - 1;
        next = (next + hits.Length) % hits.Length;
        tab.SearchCurrent = next;
        _grid.Select(hits[next], center: true);
        UpdateSearchCount();
    }

    private void UpdateSearchCount()
    {
        ToolTip.SetTip(_sCount, null);
        var r = _active?.SearchResult;
        if (r is null)
        {
            _sCount.Text = "";
            return;
        }
        _sCount.Text = r.Rows.Length == 0 ? "no matches" : $"{_active!.SearchCurrent + 1:N0} / {r.Total:N0}{(r.Truncated ? "+" : "")}";
    }

    private void SearchToFilter()
    {
        if (_active is not { } tab) return;
        var p = CurrentPattern();
        if (p.Text.Length == 0 || Query.Validate(p) is not null) return;
        tab.Filter.Rules.Add(new FilterRule { Text = p.Text, Regex = p.Regex, CaseSensitive = p.CaseSensitive, WholeWord = p.WholeWord });
        ApplyFilter();
    }

    // ============================================================= navigation

    private async Task JumpLevel(Level level, int dir)
    {
        if (_active is not { } tab) return;
        var from = tab.Focus;
        var row = await Task.Run(() => tab.Doc.FindLevel(from, dir, level));
        if (row is int r) _grid.Select(r, center: true);
        else Toast($"No {(dir > 0 ? "next" : "previous")} {(level == Level.Warn ? "warning" : "error")}");
    }

    private async Task GoToLine()
    {
        if (_active is not { } tab) return;
        var merged = tab.Info.Merged;
        var max = tab.Info.Sources.Count > 0 ? tab.Info.Sources[0].Lines : 0;
        var label = merged
            ? "Line number in the first file, or file:line where file is 1–" + tab.Info.Sources.Count
            : $"Line number (1–{max:N0})";
        var text = await Dialogs.Prompt(this, "Go to line", label, "", null, v => ParseLine(v, tab) is null ? "Enter a line number" : null);
        if (text is null || ParseLine(text, tab) is not { } target) return;
        _grid.Select(tab.Doc.RowForLine(target.Source, target.Line), center: true);
    }

    private static (int Source, int Line)? ParseLine(string text, DocumentTab tab)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length == 1 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) && l > 0) return (0, l);
        if (parts.Length == 2 && int.TryParse(parts[0], out var s) && int.TryParse(parts[1], out var l2) && s >= 1 && s <= tab.Info.Sources.Count && l2 > 0) return (s - 1, l2);
        return null;
    }

    private void SetFollow(bool on)
    {
        if (_active is not { } tab) return;
        tab.Follow = on;
        _followBtn.IsChecked = on;
        if (on) _grid.Select(Math.Max(0, tab.Info.ViewCount - 1));
        UpdatePill();
    }

    // ============================================================= bookmarks

    private void ToggleBookmarkAtFocus()
    {
        if (_active is not { } tab) return;
        Run(async () =>
        {
            if (await RowAsync(tab, tab.Focus) is { } row) ToggleBookmark(row);
        });
    }

    public void ToggleBookmark(RowData row)
    {
        if (_active is not { } tab) return;
        if (!tab.Bookmarks.Remove(row.Key))
        {
            tab.Bookmarks[row.Key] = new Bookmark { Key = row.Key, Line = row.Line, Source = row.Source, Preview = Preview(row.Text), Row = row.Row };
        }
        SaveBookmarks(tab);
        _grid.InvalidateVisual();
        _sidebar.Render(tab);
        if (tab.Filter.BookmarksOnly) ApplyFilter();
    }

    private static string Preview(string text)
    {
        var nl = text.IndexOf('\n');
        var first = (nl >= 0 ? text[..nl] : text).Trim();
        return first.Length > 160 ? first[..160] + "…" : first;
    }

    public void JumpToBookmark(Bookmark b)
    {
        if (_active is not { } tab) return;
        if (b.Row is int r) _grid.Select(r, center: true);
        else Toast("That bookmark is hidden by the current filter");
        _ = tab;
    }

    public async Task EditBookmarkNote(Bookmark b)
    {
        if (_active is not { } tab) return;
        var note = await Dialogs.Prompt(this, "Bookmark note", $"Note for line {b.Line}", b.Note, "What's interesting here?");
        if (note is null) return;
        b.Note = note.Trim();
        SaveBookmarks(tab);
        _sidebar.Render(tab);
        UpdateDetail();
    }

    public void RemoveBookmark(Bookmark b)
    {
        if (_active is not { } tab) return;
        tab.Bookmarks.Remove(b.Key);
        SaveBookmarks(tab);
        _grid.InvalidateVisual();
        _sidebar.Render(tab);
        if (tab.Filter.BookmarksOnly) ApplyFilter();
    }

    private void NextBookmark(int dir)
    {
        if (_active is not { } tab) return;
        var rows = tab.Bookmarks.Values.Select(b => b.Row).OfType<int>().Order().ToList();
        if (rows.Count == 0)
        {
            Toast("No bookmarks in this view");
            return;
        }
        var next = dir > 0 ? rows.FirstOrDefault(r => r > tab.Focus, rows[0]) : rows.LastOrDefault(r => r < tab.Focus, rows[^1]);
        _grid.Select(next, center: true);
    }

    private void UpdateBookmarkRows(DocumentTab tab)
    {
        foreach (var b in tab.Bookmarks.Values) b.Row = tab.Doc.RowForKey(b.Key);
    }

    private void SaveBookmarks(DocumentTab tab)
    {
        for (var i = 0; i < tab.Info.Sources.Count; i++)
        {
            var path = tab.Info.Sources[i].Path;
            var list = tab.Bookmarks.Values.Where(b => b.Source == i).OrderBy(b => b.Line).Select(b => new SavedBookmark { Line = b.Line, Note = b.Note }).ToList();
            if (list.Count == 0) _settings.Bookmarks.Remove(path);
            else _settings.Bookmarks[path] = list;
        }
        _settings.Save();
    }

    private void RestoreBookmarks(DocumentTab tab)
    {
        for (var i = 0; i < tab.Info.Sources.Count; i++)
        {
            if (!_settings.Bookmarks.TryGetValue(tab.Info.Sources[i].Path, out var saved)) continue;
            foreach (var sb in saved)
            {
                if (tab.Doc.KeyForLine(i, sb.Line) is not { } key) continue;
                var row = tab.Doc.RowForKey(key);
                var preview = row is int r ? tab.Doc.GetRows(r, 1).FirstOrDefault()?.Text ?? "" : "";
                tab.Bookmarks[key] = new Bookmark { Key = key, Line = sb.Line, Source = i, Note = sb.Note, Preview = Preview(preview), Row = row };
            }
        }
    }

    // ================================================================ export

    private async Task Export()
    {
        if (_active is not { Ready: true } tab) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export the current view",
            SuggestedFileName = Path.GetFileNameWithoutExtension(tab.Paths[0]) + "-export.log",
            FileTypeChoices =
            [
                new FilePickerFileType("Log text") { Patterns = ["*.log", "*.txt"] },
                new FilePickerFileType("CSV") { Patterns = ["*.csv"] },
                new FilePickerFileType("JSON lines") { Patterns = ["*.jsonl", "*.json"] },
            ],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var fmt = ext == ".csv" ? "csv" : ext is ".jsonl" or ".json" ? "jsonl" : "text";
        tab.BusyLabel = "Exporting";
        tab.BusyProgress = 0;
        try
        {
            var n = await tab.Doc.ExportAsync(path, fmt, new SyncProgress(f => tab.BusyProgress = f));
            Toast($"Exported {n:N0} entries to {Path.GetFileName(path)}");
        }
        finally
        {
            tab.BusyProgress = null;
            tab.BusyLabel = "";
            UpdateStatus();
        }
    }

    public void CopyRows(int from, int to) => Run(() => CopyRowsAsync(from, to));

    private async Task CopyRowsAsync(int from, int to)
    {
        if (_active is not { } tab || Clipboard is not { } cb || tab.Info.ViewCount == 0) return;
        const int Max = 200_000;
        var count = Math.Min(Max, to - from + 1);
        var rows = await Task.Run(() => tab.Doc.GetRows(from, count));
        await cb.SetTextAsync(string.Join(Environment.NewLine, rows.Select(r => r.Text)));
        Toast(count < to - from + 1 ? $"Copied the first {count:N0} entries" : $"Copied {count:N0} {(count == 1 ? "entry" : "entries")}");
    }

    // =============================================================== formats

    /// <summary>The tab's format: one label (or "Mixed formats" for merged files that differ) and per-file detection details.</summary>
    internal static (string Label, string Tip, bool Auto)? FormatSummary(DocumentTab tab)
    {
        var srcs = tab.Info.Sources;
        if (srcs.Count == 0) return null;
        var labels = srcs.Select(s => s.FormatLabel).Distinct().ToList();
        var lines = srcs.Select(s =>
        {
            var how = !s.FormatAuto
                ? "chosen by you" + (s.FormatId == "plain" ? "" : $"; {s.FormatMatched:N0} of {s.FormatSampled:N0} sampled lines start an entry")
                : s.FormatId == "plain"
                    ? "detected: no known log layout, so each line is one entry"
                    : $"detected: {s.FormatMatched:N0} of {s.FormatSampled:N0} sampled lines start an entry";
            return srcs.Count > 1 ? $"{s.DisplayName}: {s.Format}\n  {how}" : $"{s.Format}\n{how}";
        });
        return (labels.Count > 1 ? "Mixed formats" : labels[0], $"Log format\n\n{string.Join("\n", lines)}\n\nClick to choose another format.", srcs.All(s => s.FormatAuto));
    }

    private string _autoFormatLabel = "Auto-detect format";

    /// <summary>The auto option of the format picker names what detection picked for the active tab.</summary>
    private void SyncAutoFormatLabel()
    {
        var fmt = _active is { FormatId: "" } tab ? FormatSummary(tab) : null;
        var label = fmt is null ? "Auto-detect format" : $"Auto: {fmt.Value.Label}";
        ToolTip.SetTip(_format, fmt?.Tip ?? "Log format");
        if (label == _autoFormatLabel) return;
        _autoFormatLabel = label;
        RebuildFormatItems();
    }

    /// <summary>The active file has no timestamps at all (plain text): the timeline has nothing to show.</summary>
    private static bool Timeless(DocumentTab? tab) => tab is { Info: { Indexing: false, Total: > 0, TimeMin: null } };

    private bool HistogramOn => _settings.ShowHistogram && !Timeless(_active);

    private bool _timeless;

    private void SyncTimeless()
    {
        var timeless = Timeless(_active);
        if (timeless == _timeless) return;
        _timeless = timeless;
        SaveHistogramSize();
        _hist.IsVisible = HistogramOn;
        ApplyHistogramSize();
        _histBtn.IsEnabled = !timeless;
        ToolTip.SetTip(_histBtn, timeless ? "Timeline: this file has no timestamps" : "Timeline (Ctrl+Shift+H)");
        if (HistogramOn) Run(RefreshHistogramAsync);
    }

    internal string FormatStatusForTest => _statusFormat.IsVisible ? _statusFormat.Content as string ?? "" : "";
    internal string FormatPickerTextForTest => _format.SelectedItem as string ?? "";
    internal bool TimelineButtonEnabledForTest => _histBtn.IsEnabled;

    internal async Task ChooseFormatForTest(string id)
    {
        _format.SelectedIndex = FormatItems().FindIndex(i => (i.Id ?? "") == id);
        await Task.Yield();
    }

    private List<(string? Id, string Label)> FormatItems()
    {
        var items = new List<(string? Id, string Label)> { (null, _autoFormatLabel) };
        items.AddRange(FormatDetector.BuiltIns.Select(b => ((string?)b.Id, b.Label)));
        items.AddRange(_settings.CustomFormats.Select(c => ((string?)$"custom:{c.Name}", $"Custom: {c.Name}")));
        items.Add(("__new", "New custom format…"));
        return items;
    }

    private void RebuildFormatItems()
    {
        _suppressFormatChange = true;
        _format.ItemsSource = FormatItems().Select(i => i.Label).ToList();
        _suppressFormatChange = false;
        SyncFormatCombo();
    }

    private void SyncFormatCombo()
    {
        _suppressFormatChange = true;
        var items = FormatItems();
        var id = _active?.FormatId ?? "";
        _format.SelectedIndex = Math.Max(0, items.FindIndex(i => (i.Id ?? "") == id));
        _format.IsEnabled = _active is { Ready: true };
        _suppressFormatChange = false;
    }

    private async Task FormatChosen()
    {
        if (_active is not { Ready: true } tab) return;
        var items = FormatItems();
        var idx = _format.SelectedIndex;
        if (idx < 0 || idx >= items.Count) return;
        var id = items[idx].Id;
        if (id == "__new")
        {
            var sample = tab.Doc.GetRows(0, 20).Select(r => r.Text.Split('\n')[0]).ToList();
            var def = await Dialogs.CustomFormatEditor(this, sample);
            if (def is null)
            {
                SyncFormatCombo();
                return;
            }
            _settings.CustomFormats.RemoveAll(c => c.Name == def.Name);
            _settings.CustomFormats.Add(def);
            _settings.Save();
            id = $"custom:{def.Name}";
            tab.FormatId = id;
            RebuildFormatItems();
        }
        await Reformat(tab, id);
    }

    private async Task Reformat(DocumentTab tab, string? id)
    {
        tab.BusyLabel = "Re-indexing";
        tab.BusyProgress = 0;
        UpdateStatus();
        try
        {
            await tab.Doc.ReformatAsync(id, _settings.CustomFormats);
        }
        finally
        {
            tab.BusyProgress = null;
            tab.BusyLabel = "";
        }
        tab.FormatId = id ?? "";
        tab.Info = tab.Doc.Info();
        UpdateBookmarkRows(tab);
        if (tab != _active) return;
        _grid.Invalidate();
        SyncFormatCombo();
        _sidebar.Render(tab);
        UpdateStatus();
        UpdateDetail();
        ScheduleFieldStats(tab);
        await RefreshOverviewAsync();
        await RefreshHistogramAsync();
    }

    private async Task ChooseEncoding()
    {
        if (_active is not { Ready: true } tab) return;
        var i = await Dialogs.Choose(this, "Encoding", "Reopen this file with encoding:", Encodings, Math.Max(0, Array.IndexOf(Encodings, tab.Encoding)));
        if (i < 0 || Encodings[i] == tab.Encoding) return;
        await OpenTab(tab.Paths, Encodings[i], tab);
    }

    // ============================================================ view state

    private void ToggleHistogram(bool on)
    {
        SaveHistogramSize();
        _settings.ShowHistogram = on;
        _histBtn.IsChecked = on;
        _hist.IsVisible = HistogramOn;
        ApplyHistogramSize();
        _settings.Save();
        if (on) Run(RefreshHistogramAsync);
    }

    private void ToggleDetail(bool on, bool save = true)
    {
        if (!on) SaveDetailSize();
        _settings.ShowDetail = on;
        _detailBtn.IsChecked = on;
        _detailPane.IsVisible = on;
        _detailSplitter.IsVisible = on;
        ApplyDetailSize();
        if (save) _settings.Save();
        if (on) UpdateDetail();
    }

    /// <summary>Remembers the details pane's current height (bottom dock) or width (right dock).</summary>
    private void SaveDetailSize()
    {
        if (!_detailPane.IsVisible) return;
        if (DockRight)
        {
            if (_work.ColumnDefinitions[2].ActualWidth > 0) _settings.DetailWidth = Math.Max(200, _work.ColumnDefinitions[2].ActualWidth);
        }
        else if (_work.RowDefinitions[2].ActualHeight > 0) _settings.DetailHeight = Math.Max(80, _work.RowDefinitions[2].ActualHeight);
    }

    private void ApplyDetailSize()
    {
        var on = _settings.ShowDetail;
        _work.RowDefinitions[2].Height = on && !DockRight ? new GridLength(_settings.DetailHeight) : new GridLength(0);
        _work.ColumnDefinitions[2].Width = on && DockRight ? new GridLength(_settings.DetailWidth) : new GridLength(0);
    }

    /// <summary>Places the details pane under the grid or to its right.</summary>
    private void ApplyDock()
    {
        var right = DockRight;
        _detailSplitter.ResizeDirection = right ? GridResizeDirection.Columns : GridResizeDirection.Rows;
        _detailSplitter.Width = right ? 5 : double.NaN;
        _detailSplitter.Height = right ? double.NaN : 5;
        Grid.SetRow(_detailSplitter, right ? 0 : 1);
        Grid.SetColumn(_detailSplitter, right ? 1 : 0);
        Grid.SetRowSpan(_detailSplitter, right ? 3 : 1);
        Grid.SetColumnSpan(_detailSplitter, right ? 1 : 3);
        Grid.SetRow(_detailPane, right ? 0 : 2);
        Grid.SetColumn(_detailPane, right ? 2 : 0);
        Grid.SetRowSpan(_detailPane, right ? 3 : 1);
        Grid.SetColumnSpan(_detailPane, right ? 1 : 3);
        _work.ColumnDefinitions[2].MinWidth = 0;
        _work.RowDefinitions[2].MinHeight = 0;
        ApplyDetailSize();
    }

    private void SetDetailDock(bool right)
    {
        if (right == DockRight) return;
        SaveDetailSize();
        _settings.DetailDock = right ? "right" : "bottom";
        ApplyDock();
        if (!_settings.ShowDetail) ToggleDetail(true, save: false);
        _settings.Save();
        _grid.LayoutChanged();
    }

    private void SaveHistogramSize()
    {
        if (_hist.IsVisible && _center.RowDefinitions[0].ActualHeight >= 40) _settings.HistogramHeight = Math.Clamp(_center.RowDefinitions[0].ActualHeight, 40, 240);
    }

    private void ApplyHistogramSize()
    {
        var on = HistogramOn;
        _center.RowDefinitions[0].Height = on ? new GridLength(Math.Clamp(_settings.HistogramHeight, 40, 240)) : new GridLength(0);
        _center.RowDefinitions[0].MinHeight = on ? 40 : 0;
        _histSplitter.IsVisible = on;
    }

    private void ToggleSidebar(bool on, bool save = true)
    {
        _settings.ShowSidebar = on;
        _sideBorder.IsVisible = on;
        _sideSplitter.IsVisible = on;
        if (save) _settings.Save();
    }

    private void ApplyTheme()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = _settings.Theme switch
            {
                "dark" => ThemeVariant.Dark,
                "light" => ThemeVariant.Light,
                _ => ThemeVariant.Default,
            };
    }

    private void CycleTheme()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        _settings.Theme = dark ? "light" : "dark";
        _settings.Save();
        ApplyTheme();
        _grid.InvalidateVisual();
        _hist.InvalidateVisual();
        RebuildTabStrip();
        _sidebar.Render(_active);
    }

    private void SaveOnClose()
    {
        SaveDetailSize();
        SaveHistogramSize();
        _settings.Save();
        _tick.Stop();
        foreach (var t in _tabs) t.Dispose();
        _tabs.Clear();
    }

    // ===================================================== overview & timeline

    private async Task RefreshOverviewAsync()
    {
        if (_active is not { Ready: true } tab) return;
        var buckets = Math.Max(50, (int)_grid.Bounds.Height);
        var ov = await Task.Run(() => tab.Doc.Overview(buckets));
        if (tab == _active) _grid.SetOverview(ov);
    }

    private async Task RefreshHistogramAsync()
    {
        if (_active is not { Ready: true } tab || !HistogramOn) return;
        var buckets = Math.Clamp((int)(_hist.Bounds.Width / 5), 20, 400);
        var h = await Task.Run(() => tab.Doc.Histogram(buckets));
        if (tab != _active) return;
        _hist.Data = h;
        UpdateViewport();
    }

    private void UpdateViewport()
    {
        if (_active is not { } tab) return;
        var top = _grid.GetCached(tab.TopRow)?.Time;
        var bottom = _grid.GetCached(Math.Min(tab.Info.ViewCount - 1, tab.TopRow + _grid.VisibleRows - 1))?.Time;
        _hist.SetViewport(top, bottom);
    }

    // ======================================================= ILogGridHost

    public IReadOnlyList<HighlightRule> Highlights => _settings.Highlights;

    public double ColumnWidth(ColumnDef col) => col.Width == 0 ? 0 : _settings.ColumnWidths.TryGetValue(col.Key, out var w) ? w : col.Width;

    public void SetColumnWidth(string key, double width) => _settings.ColumnWidths[key] = Math.Round(width);

    public bool IsColumnHidden(string key) => _settings.HiddenColumns.Contains(key);

    public void SelectionChanged()
    {
        UpdateDetail();
        UpdateStatus();
        if (_active is { } t && t.Follow && t.Focus < t.Info.ViewCount - 1)
        {
            t.Follow = false;
            _followBtn.IsChecked = false;
        }
    }

    public void TopRowChanged()
    {
        if (_active is { } t) _followBtn.IsChecked = t.Follow;
        if (_newPill.IsVisible) UpdatePill();
        UpdateViewport();
        if (_contextBanner.IsVisible) UpdateContextBanner();
        if (_detail.Text is null or "") UpdateDetail();
    }


    public bool HandleKey(KeyEventArgs e)
    {
        if (e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift)) return false;
        var shift = e.KeyModifiers == KeyModifiers.Shift;
        switch (e.Key)
        {
            case Key.E:
                Run(() => JumpLevel(Level.Error, shift ? -1 : 1));
                return true;
            case Key.W:
                Run(() => JumpLevel(Level.Warn, shift ? -1 : 1));
                return true;
            case Key.N:
                Run(() => NextHit(shift ? -1 : 1));
                return true;
            case Key.B when !shift:
                ToggleBookmarkAtFocus();
                return true;
            case Key.F when !shift:
                SetFollow(!(_active?.Follow ?? false));
                return true;
            case Key.Oem2 or Key.Divide when !shift:
                FocusSearch();
                return true;
            case Key.C when !shift:
                Run(() => SetContextMode(!(_active?.Doc.IgnoreFilter ?? false)));
                return true;
            case Key.Escape when _active?.Doc.IgnoreFilter == true:
                Run(() => SetContextMode(false));
                return true;
            default:
                return false;
        }
    }

    private void OnGlobalKey(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var handled = true;
        switch (e.Key)
        {
            case Key.O when ctrl: Run(() => PickAndOpen(shift)); break;
            case Key.F when ctrl: FocusSearch(); break;
            case Key.L when ctrl: FocusQuick(); break;
            case Key.G when ctrl && !shift: Run(GoToLine); break;
            case Key.B when ctrl: ToggleBookmarkAtFocus(); break;
            case Key.T when ctrl: SetFollow(!(_active?.Follow ?? false)); break;
            case Key.E when ctrl: Run(Export); break;
            case Key.W when ctrl: CloseTab(_active); break;
            case Key.Tab when ctrl: CycleTab(shift ? -1 : 1); break;
            case Key.H when ctrl && shift: ToggleHistogram(!_settings.ShowHistogram); break;
            case Key.D when ctrl && shift: ToggleDetail(!_settings.ShowDetail); break;
            case Key.Z when e.KeyModifiers == KeyModifiers.Alt: SetDisplay(() => _settings.Wrap = !_settings.Wrap); break;
            case Key.F1: OpenPalette(); break;
            case Key.P when ctrl && shift: OpenPalette(); break;
            case Key.A when ctrl && shift: TogglePatterns(); break;
            case Key.K when ctrl && !shift: ClearSoFar(); break;
            case Key.S when ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Alt): Run(PickSaveWorkspace); break;
            case Key.F2: NextBookmark(shift ? -1 : 1); break;
            case Key.F3: Run(() => NextHit(shift ? -1 : 1)); break;
            case Key.G when ctrl && shift: Run(() => NextHit(-1)); break;
            default: handled = false; break;
        }
        if (handled) e.Handled = true;
    }

    // ========================================================= detail & status

    private async Task<RowData?> RowAsync(DocumentTab tab, int row)
    {
        if (row < 0 || row >= tab.Info.ViewCount) return null;
        return _grid.GetCached(row) ?? (await Task.Run(() => tab.Doc.GetRows(row, 1))).FirstOrDefault();
    }

    private int _detailGen;

    private void UpdateDetail()
    {
        if (!_settings.ShowDetail) return;
        var tab = _active;
        var gen = ++_detailGen;
        if (UpdatePatternDetail()) return;
        if (tab is null || tab.Info.ViewCount == 0)
        {
            _detail.Text = "";
            _detailBar.Text = "";
            _detailFields.Children.Clear();
            return;
        }
        Run(async () =>
        {
            var row = await RowAsync(tab, tab.Focus);
            RowData? other = tab.SelectionStart != tab.SelectionEnd ? await RowAsync(tab, tab.Anchor) : null;
            if (gen != _detailGen || row is null) return;
            var parts = new List<string> { row.LineCount > 1 ? $"Line {row.Line} (+{row.LineCount - 1} lines)" : $"Line {row.Line}" };
            if (tab.Info.Merged && row.Source < tab.Info.Sources.Count) parts.Add(tab.Info.Sources[row.Source].DisplayName);
            if (row.Time is long t) parts.Add(Timestamps.Format(t));
            if (row.Level != Level.None) parts.Add(LevelNames.Labels[(int)row.Level]);
            if (other?.Time is long a && row.Time is long b) parts.Add($"Δ selection {Timestamps.FormatDuration(Math.Abs(b - a))}");
            if (row.Repeat > 1) parts.Add($"Repeated {row.Repeat:N0} times");
            if (tab.Bookmarks.TryGetValue(row.Key, out var bm)) parts.Add(bm.Note.Length > 0 ? $"🔖 {bm.Note}" : "🔖 bookmarked");
            _detailBar.Text = string.Join("   ·   ", parts);
            RenderDetailFields(tab, row);
            _detail.Text = FoldDetail(tab, row, Pretty.Entry(row.Text));
        });
    }

    private void UpdateStatus()
    {
        var tab = _active;
        if (tab is null)
        {
            _statusLeft.Text = "No file open";
            _statusSel.Text = _statusLive.Text = _statusBusy.Text = "";
            _statusEnc.IsVisible = _statusFormat.IsVisible = false;
            SyncAutoFormatLabel();
            SyncTimeless();
            _progress.IsVisible = false;
            UpdateClearedStatus(null);
            return;
        }
        var i = tab.Info;
        _statusLeft.Text = tab.Doc.IgnoreFilter ? $"All {i.Total:N0} entries (filters ignored)"
            : i.ViewCount == i.Total && !tab.HasCustomFilter ? $"{i.Total:N0} entries"
            : $"{i.ViewCount:N0} of {i.Total:N0} entries (filtered)";
        if (!tab.Doc.IgnoreFilter && tab.Filter.Grouped) _statusLeft.Text += " · grouped";
        UpdateClearedStatus(tab);
        var n = tab.SelectionEnd - tab.SelectionStart + 1;
        _statusSel.Text = i.ViewCount == 0 ? "" : n > 1 ? $"{n:N0} selected" : $"Row {tab.Focus + 1:N0}";
        var src = i.Sources.FirstOrDefault();
        var fmt = FormatSummary(tab);
        _statusFormat.IsVisible = fmt is not null;
        _statusFormat.Content = fmt is null ? "" : fmt.Value.Auto ? $"{fmt.Value.Label} · auto" : fmt.Value.Label;
        ToolTip.SetTip(_statusFormat, fmt?.Tip);
        SyncAutoFormatLabel();
        SyncTimeless();
        _statusEnc.IsVisible = src is not null;
        _statusEnc.Content = src is null ? "" : src.Encoding + (src.Compressed ? " · gzip" : "");
        _statusLive.Text = src is null ? "" : i.Sources.Any(s => s.Live) ? (tab.Follow ? "● Following" : "● Live") : "Static";
        _progress.IsVisible = tab.BusyProgress is not null;
        _progress.Value = tab.BusyProgress ?? 0;
        _statusBusy.Text = tab.BusyProgress is double p ? $"{tab.BusyLabel} {p:P0}" : "";
    }
}
