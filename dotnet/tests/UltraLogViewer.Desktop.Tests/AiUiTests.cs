using System.Net;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop.Tests;

/// <summary>The AI menu items, the consent preview (Cancel sends nothing), the answer pane and the reviewed filter, with a fake HTTP handler.</summary>
public sealed class AiUiTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ulv-ai-").FullName;
    private readonly FakeAi _http = new();

    public AiUiTests() => MainWindow.AiHandlerForTest = _http;

    public void Dispose()
    {
        MainWindow.AiHandlerForTest = null;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class FakeAi : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];
        public string Answer { get; set; } = "Nothing to worry about.";
        public bool Hang { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, await request.Content!.ReadAsStringAsync(ct)));
            if (Hang) await Task.Delay(Timeout.Infinite, ct);
            var body = System.Text.Json.JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = Answer } }, stop_reason = "end_turn" });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private (MainWindow Window, AppSettings Settings) NewWindow(bool ai)
    {
        var settings = new AppSettings { FilePath = Path.Combine(_dir, "settings.json") };
        if (ai)
        {
            settings.Ai.Enabled = true;
            new AiKeyStore(_dir).Save(AiConfig.Anthropic, "sk-ant-test-key");
        }
        var w = new MainWindow(settings, []) { Width = 1200, Height = 800 };
        w.Show();
        return (w, settings);
    }

    private string PlainLog()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 60; i++) sb.Append($"| 10.0.0.{i % 200} | {i} | worker{i % 3} handled job for bob{i}@example.com\n");
        var path = Path.Combine(_dir, "plain.txt");
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

    private static async Task<DocumentTab> Open(MainWindow w, string path)
    {
        await w.OpenPaths([path], false);
        var tab = w.ActiveTab!;
        await Until(() => tab.Ready && tab.BusyProgress is null);
        return tab;
    }

    private static async Task<Window> Dialog(MainWindow w, string title)
    {
        await Until(() => w.OwnedWindows.Any(o => o.Title == title));
        return w.OwnedWindows.Single(o => o.Title == title);
    }

    private static void Click(Window dialog, string content) =>
        dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == content).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static TextBox Box(Window dialog, Func<TextBox, bool> pick) => dialog.GetLogicalDescendants().OfType<TextBox>().First(pick);

    private static void Invoke(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    [AvaloniaFact]
    public async Task Plain_rows_offer_extract_fields_and_with_ai_off_it_guesses_offline()
    {
        var (w, _) = NewWindow(ai: false);
        var tab = await Open(w, PlainLog());
        Assert.Equal("plain", tab.Info.Sources[0].FormatId);
        var row = tab.Doc.GetRows(3, 1)[0];
        var items = w.AiRowItemsForTest(row);
        Assert.Equal(["Extract fields…", "Explain this entry"], items.Select(i => i.Header as string));
        Assert.Contains(w.PaletteActions(), a => a.Label == "AI › Ask AI for a filter… (? in the quick filter)");
        Assert.Contains(w.PaletteActions(), a => a.Label == "AI › AI settings…");

        Invoke(items[0]);
        var editor = await Dialog(w, "Custom format");
        Assert.Equal(AiRegexGuess.Guess(row.Text), Box(editor, b => b.FontFamily == Fonts.Mono).Text);
        Assert.Equal("Extracted", Box(editor, b => b.Watermark?.StartsWith("Name") == true).Text);
        Assert.Contains("guessed from the line's tokens", w.ToastForTest);
        Click(editor, "Save and apply");
        await Until(() => tab.FormatId == "custom:Extracted" && tab.BusyProgress is null);
        await Until(() => tab.Info.Columns.Any(c => c.Key == "ip"));
        Assert.Empty(_http.Seen);

        // Explain with AI off asks to open the settings; declining sends nothing.
        Invoke(w.AiRowItemsForTest(tab.Doc.GetRows(0, 1)[0]).Single(i => i.Header as string == "Explain this entry"));
        Click(await Dialog(w, "AI is off"), "Cancel");
        await Until(() => w.OwnedWindows.Count == 0);
        Assert.Empty(_http.Seen);
    }

    [AvaloniaFact]
    public async Task Consent_preview_shows_masked_text_and_cancel_sends_nothing()
    {
        var (w, settings) = NewWindow(ai: true);
        var tab = await Open(w, PlainLog());
        var row = tab.Doc.GetRows(25, 1)[0];
        Invoke(w.AiRowItemsForTest(row).Single(i => i.Header as string == "Explain this entry"));
        var consent = await Dialog(w, "Send to AI?");
        var text = Box(consent, b => b.Name == "ai-consent-text").Text!;
        Assert.Contains(">>> Entry to explain:\n| <ip> | 25 | worker1 handled job for <email>", text);
        Assert.DoesNotContain("@example.com", text);
        Assert.Contains("api.anthropic.com", string.Join(" ", consent.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text)));
        Click(consent, "Cancel");
        await Until(() => w.OwnedWindows.Count == 0);
        Assert.Empty(_http.Seen);
        Assert.False(w.AiBusyForTest);
        Assert.Null(w.AiAnswerForTest);
        // the key never lands in the settings file
        settings.Save();
        Assert.DoesNotContain("sk-ant-test-key", File.ReadAllText(settings.FilePath!));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_dir, "ai-key-anthropic.bin")));
    }

    [AvaloniaFact]
    public async Task Send_shows_the_answer_in_the_details_pane_and_sends_the_edited_text()
    {
        var (w, _) = NewWindow(ai: true);
        var tab = await Open(w, PlainLog());
        _http.Answer = "## Meaning\n- **Job** finished normally.";
        Invoke(w.AiRowItemsForTest(tab.Doc.GetRows(25, 1)[0]).Single(i => i.Header as string == "Explain this entry"));
        var consent = await Dialog(w, "Send to AI?");
        var box = Box(consent, b => b.Name == "ai-consent-text");
        box.Text += "\nEXTRA NOTE";
        consent.GetLogicalDescendants().OfType<CheckBox>().Single().IsChecked = true; // don't ask again
        Click(consent, "Send");
        await Until(() => w.AiAnswerForTest is not null);
        Assert.Equal("Meaning\n• Job finished normally.", w.AiAnswerForTest);
        var (req, body) = _http.Seen.Single();
        Assert.Equal("sk-ant-test-key", req.Headers.GetValues("x-api-key").Single());
        Assert.Contains("EXTRA NOTE", body);
        Assert.Contains("\\u003Cemail\\u003E", body); // System.Text.Json escapes < >
        Assert.DoesNotContain("example.com", body);

        // "don't ask again": the next request goes straight out; cancelling it stops the spinner
        _http.Hang = true;
        Invoke(w.AiRowItemsForTest(tab.Doc.GetRows(26, 1)[0]).Single(i => i.Header as string == "Explain this entry"));
        await Until(() => w.AiBusyForTest && _http.Seen.Count == 2);
        Assert.Empty(w.OwnedWindows);
        w.CancelAiButtonForTest();
        await Until(() => !w.AiBusyForTest);
        Assert.Equal("AI request cancelled", w.ToastForTest);

        // closing the tab aborts a running request
        Invoke(w.AiRowItemsForTest(tab.Doc.GetRows(27, 1)[0]).Single(i => i.Header as string == "Explain this entry"));
        await Until(() => _http.Seen.Count == 3 && w.AiBusyForTest);
        w.CloseTab(tab);
        await Until(() => !w.AiBusyForTest || w.ActiveTab is null);
    }

    [AvaloniaFact]
    public async Task Question_in_the_quick_filter_becomes_a_reviewed_query()
    {
        var (w, _) = NewWindow(ai: true);
        var tab = await Open(w, PlainLog());
        _http.Answer = "{\"query\": \"worker1 -job:x\", \"explanation\": \"Entries from worker1\"}";
        w.AskFilterForTest("? only worker1");
        var consent = await Dialog(w, "Send to AI?");
        Assert.StartsWith("Request: only worker1", Box(consent, b => b.Name == "ai-consent-text").Text);
        Click(consent, "Send");
        var review = await Dialog(w, "AI filter");
        var q = Box(review, b => b.FontFamily == Fonts.Mono);
        Assert.Equal("worker1 -job:x", q.Text);
        Assert.Equal("", tab.Filter.Query); // nothing runs before Apply
        q.Text = "worker1";
        Click(review, "Apply filter");
        await Until(() => tab.Filter.Query == "worker1" && tab.Info.ViewCount == 20);
        Assert.Equal("worker1", w.QuickTextForTest);
        Assert.Contains("Ask AI for a filter", string.Join("\n", Dialogs.Shortcuts.Select(s => s.What)));
    }
}
