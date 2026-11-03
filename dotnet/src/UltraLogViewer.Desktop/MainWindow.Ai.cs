using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop;

/// <summary>
/// AI features: "Extract fields…" (with an offline guess when AI is off), "Explain this entry / pattern" and a quick
/// filter written from a question ("? errors from the db logger in the last hour"). Opt-in: nothing is sent until AI is
/// turned on, and every request first shows the exact (masked, editable) text in a consent preview.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Tests swap in a fake handler so no request reaches the network.</summary>
    internal static HttpMessageHandler? AiHandlerForTest { get; set; }
    private static HttpClient? s_aiHttp;
    private static HttpClient AiHttp => AiHandlerForTest is { } h ? new HttpClient(h, disposeHandler: false) : s_aiHttp ??= new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>"Don't ask again for this session" in the consent preview; never saved.</summary>
    private bool _aiNoConsent;
    /// <summary>The running request per tab, cancelled when the tab closes (or a new request starts on it).</summary>
    private readonly Dictionary<DocumentTab, CancellationTokenSource> _aiRuns = [];

    // The answer pane at the top of the details pane: a title, Copy / Cancel / close, a spinner and the text.
    private readonly TextBlock _aiTitle = new() { FontWeight = FontWeight.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly SelectableTextBlock _aiText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0) };
    private readonly ProgressBar _aiSpinner = new() { IsIndeterminate = true, Height = 3, MinHeight = 3, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _aiCopy = new() { Classes = { "mini" }, Content = "Copy" };
    private readonly Button _aiCancel = new() { Classes = { "mini" }, Content = "Cancel" };
    private readonly Button _aiClose = new() { Classes = { "mini" }, Content = "✕" };
    private Border? _aiPane;
    private DocumentTab? _aiPaneTab;

    private AiKeyStore AiKeys => new(Path.GetDirectoryName(_settings.FilePath ?? AppSettings.DefaultPath)!);

    internal Border AiPane()
    {
        if (_aiPane is not null) return _aiPane;
        ToolTip.SetTip(_aiClose, "Close the answer");
        _aiCopy.Click += (_, _) => Run(() => CopyText(_aiText.Text ?? "", "Answer copied"));
        _aiCancel.Click += (_, _) => { if (_aiPaneTab is { } t) CancelAi(t); };
        _aiClose.Click += (_, _) =>
        {
            if (_aiPaneTab is { } t) CancelAi(t);
            _aiPane!.IsVisible = false;
        };
        var head = new DockPanel { Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, [DockPanel.DockProperty] = Dock.Right, Children = { _aiCopy, _aiCancel, _aiClose } }, _aiTitle } };
        return _aiPane = new Border
        {
            IsVisible = false,
            Margin = new Thickness(8, 6, 8, 4),
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.Parse("#7048E8"), 0.08),
            BorderBrush = new SolidColorBrush(Color.Parse("#7048E8"), 0.35),
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { head, _aiSpinner, _aiText } },
        };
    }

    private void ShowAiBusy(DocumentTab tab, string title)
    {
        AiPane();
        _aiPaneTab = tab;
        _aiTitle.Text = title;
        _aiText.Text = "";
        _aiText.IsVisible = false;
        _aiSpinner.IsVisible = _aiCancel.IsVisible = true;
        _aiCopy.IsVisible = false;
        _aiPane!.IsVisible = true;
        if (!_settings.ShowDetail) ToggleDetail(true);
    }

    private void ShowAiAnswer(string title, string? text)
    {
        if (_aiPane is null) return;
        _aiSpinner.IsVisible = _aiCancel.IsVisible = false;
        if (text is null)
        {
            _aiPane.IsVisible = false;
            return;
        }
        _aiTitle.Text = title;
        _aiText.Text = AiPrompts.PlainAnswer(text);
        _aiText.IsVisible = _aiCopy.IsVisible = true;
    }

    internal string? AiAnswerForTest => _aiPane is { IsVisible: true } && _aiText.IsVisible ? _aiText.Text : null;
    internal bool AiBusyForTest => _aiPane is { IsVisible: true } && _aiSpinner.IsVisible;
    internal List<MenuItem> AiRowItemsForTest(RowData row) => _active is { } t ? AiRowItems(t, row).OfType<MenuItem>().ToList() : [];
    internal void AskFilterForTest(string text)
    {
        SetQuickFilterText(text);
        Run(AskFilter);
    }
    internal string QuickTextForTest => _quick.Text ?? "";
    internal void CancelAiButtonForTest() => _aiCancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    // ================================================================= menus

    private MenuItem AiMenu() => new()
    {
        Header = "_AI",
        Items =
        {
            Item("_Explain selected entry", () => Run(async () => { if (_active is { } t && await RowAsync(t, t.Focus) is { } r) await ExplainEntry(t, r); })),
            Item("E_xtract fields from selected line…", () => Run(async () => { if (_active is { } t && await RowAsync(t, t.Focus) is { } r) await ExtractFields(t, r); })),
            Item("_Ask AI for a filter… (? in the quick filter)", StartAiQuestion),
            new Separator(),
            Item("AI _settings…", () => Run(EditAiSettings)),
        },
    };

    /// <summary>Row-menu items: "Extract fields…" for plain-text rows (also without AI), "Explain this entry".</summary>
    private IEnumerable<Control> AiRowItems(DocumentTab tab, RowData row)
    {
        var src = row.Source < tab.Info.Sources.Count ? tab.Info.Sources[row.Source] : null;
        if (src?.FormatId == "plain" || row.Fields.Keys.All(k => k == "message"))
            yield return Item("Extract fields…", () => Run(() => ExtractFields(tab, row)));
        yield return Item("Explain this entry", () => Run(() => ExplainEntry(tab, row)));
    }

    private async Task EditAiSettings()
    {
        if (!await AiDialogs.Settings(this, _settings.Ai, AiKeys)) return;
        _settings.Save();
        Toast(_settings.Ai.Enabled ? $"AI is on ({AiConfigNow().Host}). Every request asks first." : "AI is off: nothing is sent");
    }

    private AiConfig AiConfigNow()
    {
        var a = _settings.Ai;
        return a.Provider == AiConfig.OpenAi
            ? new AiConfig(AiConfig.OpenAi, a.OpenAiModel, a.OpenAiBaseUrl, AiKeys.Load(AiConfig.OpenAi))
            : new AiConfig(AiConfig.Anthropic, a.AnthropicModel, AiConfig.AnthropicUrl, AiKeys.Load(AiConfig.Anthropic));
    }

    /// <summary>AI is on and configured; otherwise offers the settings. Nothing is sent when this returns false.</summary>
    private async Task<bool> EnsureAi()
    {
        if (!_settings.Ai.Enabled)
        {
            if (!await Dialogs.Confirm(this, "AI is off", "AI features are off, so nothing is sent anywhere. Open the AI settings to turn them on?", "AI settings…")) return false;
            await EditAiSettings();
            if (!_settings.Ai.Enabled) return false;
        }
        if (_settings.Ai.Provider == AiConfig.Anthropic && !AiKeys.Has(AiConfig.Anthropic))
        {
            Toast("Add your Claude API key under AI › AI settings…", true);
            return false;
        }
        return true;
    }

    public void CancelAi(DocumentTab tab)
    {
        if (_aiRuns.Remove(tab, out var cts)) cts.Cancel();
    }

    /// <summary>
    /// Masks the prompt, shows the consent preview (unless skipped for this session), sends it and returns the answer.
    /// Returns null when cancelled or failed (failures are toasted). The busy pane shows from the moment it is sent.
    /// </summary>
    private async Task<string?> AskAi(DocumentTab tab, AiPrompt prompt, string busyTitle)
    {
        var config = AiConfigNow();
        var (user, masked) = AiRedaction.Redact(prompt.User);
        if (!_aiNoConsent)
        {
            var dest = config.Provider == AiConfig.Anthropic ? $"Anthropic (Claude API, {config.Host}), model {config.Model}" : $"{config.Host}, model {config.Model}";
            if (await AiDialogs.Consent(this, dest, prompt.System, user, masked) is not { } ok) return null;
            user = ok.User;
            _aiNoConsent = ok.DontAsk;
        }
        CancelAi(tab);
        var cts = _aiRuns[tab] = new CancellationTokenSource();
        ShowAiBusy(tab, busyTitle);
        try
        {
            return await AiClient.SendAsync(AiHttp, config, prompt with { User = user }, AiClient.DefaultTimeout, cts.Token);
        }
        catch (AiException e)
        {
            Toast(e.Message, true);
        }
        catch (OperationCanceledException)
        {
            if (_tabs.Contains(OwnerOf(tab)!)) Toast("AI request cancelled");
        }
        finally
        {
            if (_aiRuns.TryGetValue(tab, out var c) && c == cts) _aiRuns.Remove(tab);
            cts.Dispose();
        }
        return null;
    }

    private static string FirstLine(string s) => s.Split('\n')[0].TrimEnd('\r');

    // ======================================================== extract fields

    private async Task ExtractFields(DocumentTab tab, RowData row)
    {
        var line = FirstLine(row.Text);
        var more = await Task.Run(() => tab.Doc.GetRows(row.Row + 1, 8));
        var sample = new List<string> { line };
        sample.AddRange(more.Select(r => FirstLine(r.Text)).Where(l => l.Trim().Length > 0).Take(4));
        string name = "Extracted", pattern;
        if (!_settings.Ai.Enabled)
        {
            pattern = AiRegexGuess.Guess(line);
            Toast("AI is off, so the fields are guessed from the line's tokens. Check them, then save.");
        }
        else
        {
            if (!await EnsureAi()) return;
            var answer = await AskAi(tab, AiPrompts.Extract(sample), "Extracting fields…");
            ShowAiAnswer("", null);
            if (answer is null) return;
            // Checked against the real line, not the masked one the model saw: this is the line the format must parse.
            if (AiPrompts.ParseExtract(answer, line, out var err) is { } ok) (name, pattern) = ok;
            else
            {
                pattern = AiRegexGuess.Guess(line);
                Toast($"The AI suggestion was not usable ({err}), so this is a guess from the line's tokens", true);
            }
        }
        var def = await Dialogs.CustomFormatEditor(this, sample, name, pattern);
        if (def is null) return;
        _settings.CustomFormats.RemoveAll(c => c.Name == def.Name);
        _settings.CustomFormats.Add(def);
        _settings.Save();
        var id = $"custom:{def.Name}";
        tab.FormatId = id;
        RebuildFormatItems();
        await Reformat(tab, id);
    }

    // =============================================================== explain

    private string FormatLabel(DocumentTab tab) => FormatSummary(tab)?.Label ?? "unknown";

    private async Task ExplainEntry(DocumentTab tab, RowData row)
    {
        if (!await EnsureAi()) return;
        var start = Math.Max(0, row.Row - 20);
        var (before, after) = await Task.Run(() => (tab.Doc.GetRows(start, row.Row - start), tab.Doc.GetRows(row.Row + 1, 20)));
        var prompt = AiPrompts.ExplainEntry(FormatLabel(tab), before.Select(r => r.Text).ToList(), row.Text, after.Select(r => r.Text).ToList(),
            filtered: tab.HasCustomFilter && !tab.Doc.IgnoreFilter);
        var title = $"AI · line {row.Line}";
        ShowAiAnswer(title, await AskAi(tab, prompt, $"Explaining line {row.Line}…"));
    }

    private async Task ExplainPattern(DocumentTab tab, PatternInfo p)
    {
        if (!await EnsureAi()) return;
        var prompt = AiPrompts.ExplainPattern(FormatLabel(tab), p.Template, p.Count, [p.Sample]);
        ShowAiAnswer("AI · pattern", await AskAi(tab, prompt, "Explaining the pattern…"));
    }

    // ======================================================== question → filter

    internal static bool IsAiQuestion(string? text) => text is not null && text.TrimStart().StartsWith('?');

    private void StartAiQuestion()
    {
        FocusQuick();
        if (!IsAiQuestion(_quick.Text)) _quick.Text = "? ";
        _quick.CaretIndex = _quick.Text?.Length ?? 0;
    }

    private async Task AskFilter()
    {
        if (_active is not { Ready: true } tab) return;
        var question = (_quick.Text ?? "").TrimStart()[1..].Trim();
        if (question.Length == 0)
        {
            Toast("Type a question after the ?, e.g. ? errors from the db logger in the last hour");
            return;
        }
        if (!await EnsureAi()) return;
        var fields = (tab.FieldStats?.Fields ?? [])
            .Select(f => (f.Key, (IReadOnlyList<string>)f.Top.Select(t => t.Value).ToList())).ToList();
        var levels = Enumerable.Range(1, LevelNames.Count - 1).Where(i => tab.Info.LevelCounts[i] > 0)
            .Select(i => (LevelNames.Labels[i], tab.Info.LevelCounts[i])).ToList();
        var prompt = AiPrompts.Filter(question, fields, levels, tab.Info.TimeMin, tab.Info.TimeMax, DateTime.Now);
        var answer = await AskAi(tab, prompt, "Writing a filter…");
        ShowAiAnswer("", null);
        if (answer is null) return;
        if (AiPrompts.ParseFilter(answer, out var err) is not { } r)
        {
            Toast($"The AI answer was not usable: {err}", true);
            return;
        }
        var q = await AiDialogs.ReviewFilter(this, question, r.Query, r.Explanation, s => Query.ValidateQuery(s, tab.Filter.QueryCaseSensitive));
        if (q is null || tab != _active) return;
        SetQuickFilterText(q);
        CommitQuickFilter();
        RememberQuery(q);
    }
}
