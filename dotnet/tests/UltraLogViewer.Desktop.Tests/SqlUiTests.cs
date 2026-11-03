using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using UltraLogViewer.Core;
using UltraLogViewer.Desktop.Controls;

namespace UltraLogViewer.Desktop.Tests;

/// <summary>The SQL panel in the real window: shortcut, palette, run, results, chart, errors and the jump to an entry.</summary>
public sealed class SqlUiTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ulv-sql-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Sample(int entries)
    {
        var sb = new StringBuilder();
        var t0 = new DateTime(2024, 3, 1, 10, 0, 0);
        for (var i = 0; i < entries; i++)
        {
            var t = t0.AddSeconds(i * 7).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            if (i % 13 == 12)
            {
                sb.Append($"{t} [worker-{i % 4}] ERROR com.shop.PaymentService - Payment {i} failed after {i * 10} ms\n");
                sb.Append("\tat com.shop.PaymentService.charge(PaymentService.java:41)\n");
            }
            else sb.Append($"{t} [main] INFO  com.shop.Api - Handled request {i} in {i % 50} ms\n");
        }
        var path = Path.Combine(_dir, "sql.log");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private static async Task Until(Func<bool> cond, int timeoutMs = 10_000)
    {
        var start = Environment.TickCount64;
        while (!cond())
        {
            if (Environment.TickCount64 - start > timeoutMs) throw new TimeoutException("Condition not met in time");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Screenshot(MainWindow w, string name)
    {
        var dir = Environment.GetEnvironmentVariable("ULV_SCREENSHOTS") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, name));
    }

    [AvaloniaFact]
    public async Task Runs_a_query_charts_it_and_jumps_to_a_result()
    {
        var w = new MainWindow(new AppSettings { FilePath = Path.Combine(_dir, "settings.json") }, []) { Width = 1400, Height = 900 };
        w.Show();
        await w.OpenPaths([Sample(400)], false);
        var tab = w.ActiveTab!;
        await Until(() => tab.Ready);

        // Ctrl+Shift+Q opens the panel; View › SQL is in the palette
        Assert.Contains(w.PaletteActions(), a => a.Label == "View › SQL" && a.Gesture.Contains('Q'));
        w.KeyPressQwerty(PhysicalKey.Q, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.True(w.SqlShownForTest);
        await Until(() => w.SqlExamplesForTest.Count == 5);

        // Ctrl+Enter runs the query in the editor
        w.SetSqlForTest("SELECT line, level, logger, message FROM logs WHERE level = 'ERROR' ORDER BY line");
        w.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        await Until(() => w.SqlResultForTest is { Rows.Count: 30 });
        Assert.Equal(["line", "level", "logger", "message"], w.SqlResultForTest!.Columns);
        Assert.Equal("com.shop.PaymentService", w.SqlResultForTest.Rows[0][2]);
        Assert.StartsWith("30 rows in", w.SqlStatusForTest);
        Assert.False(w.SqlChartButtonForTest.IsEnabled);
        Screenshot(w, "sql-panel.png");

        // a click on the third result row selects that entry in the grid
        var view = w.SqlResultsViewForTest;
        var at = view.TranslatePoint(new Point(20, SqlResultsView.RowH * 3 + 5), w)!.Value;
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        var expected = tab.Doc.RowForLine(0, (int)(long)w.SqlResultForTest.Rows[2][0]!);
        await Until(() => tab.Focus == expected);
        Assert.Equal(Level.Error, tab.Doc.GetRows(expected, 1)[0].Level);

        // errors per minute is a (label, number) result: the chart can draw it
        w.PickSqlExampleForTest(1);
        await Until(() => w.SqlResultForTest?.Columns is ["minute", "errors"]);
        Assert.True(w.SqlChartButtonForTest.IsEnabled);
        w.SqlChartButtonForTest.IsChecked = true;
        Assert.True(w.SqlChartForTest.IsVisible);
        Screenshot(w, "sql-chart.png");
        w.SqlChartButtonForTest.IsChecked = false;

        // errors show inline; ATTACH is refused
        w.SetSqlForTest("SELECT nope FROM logs");
        await w.RunSqlForTest();
        Assert.Equal("no such column: nope", w.SqlErrorForTest);
        w.SetSqlForTest("ATTACH DATABASE 'x.db' AS x");
        await w.RunSqlForTest();
        Assert.Contains("ATTACH is not allowed", w.SqlErrorForTest);

        // the view table follows the filter
        tab.Filter.Levels[(int)Level.Info] = false;
        await w.ApplyFilterAsync(tab);
        w.SetSqlForTest("SELECT count(*) AS n FROM view");
        await w.RunSqlForTest();
        Assert.Equal(30L, w.SqlResultForTest!.Rows[0][0]);

        w.KeyPressQwerty(PhysicalKey.Q, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.False(w.SqlShownForTest);
        w.Close();
    }
}
