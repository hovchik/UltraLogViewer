using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop;

/// <summary>The AI settings, the consent preview shown before every request, and the review of a proposed filter.</summary>
public static class AiDialogs
{
    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
    private static TextBlock Hint(string text) => new() { Text = text, Classes = { "hint" }, TextWrapping = TextWrapping.Wrap };

    /// <summary>Edits <paramref name="s"/> and the stored keys. Returns false when cancelled (nothing changed).</summary>
    public static async Task<bool> Settings(Window owner, AiSettings s, AiKeyStore keys)
    {
        var enabled = new CheckBox { Content = "Turn on AI features", IsChecked = s.Enabled };
        var anthropic = new RadioButton { GroupName = "ai-provider", Content = "Claude API (Anthropic)", IsChecked = s.Provider != AiConfig.OpenAi };
        var openai = new RadioButton { GroupName = "ai-provider", Content = "OpenAI-compatible endpoint (local: Ollama, LM Studio, llama.cpp, …)", IsChecked = s.Provider == AiConfig.OpenAi };
        TextBox KeyBox(string provider) => new()
        {
            PasswordChar = '•',
            Watermark = keys.Has(provider) ? "A key is saved; type to replace it" : provider == AiConfig.Anthropic ? "sk-ant-…" : "Optional",
        };
        var aKey = KeyBox(AiConfig.Anthropic);
        var aModel = new TextBox { Text = s.AnthropicModel, Watermark = AiConfig.DefaultAnthropicModel };
        var aForget = new CheckBox { Content = "Forget the saved key", IsVisible = keys.Has(AiConfig.Anthropic) };
        var oUrl = new TextBox { Text = s.OpenAiBaseUrl, Watermark = AiConfig.DefaultOpenAiUrl };
        var oKey = KeyBox(AiConfig.OpenAi);
        var oModel = new TextBox { Text = s.OpenAiModel, Watermark = "llama3.1" };
        var oForget = new CheckBox { Content = "Forget the saved key", IsVisible = keys.Has(AiConfig.OpenAi) };
        var aPanel = new StackPanel { Spacing = 4, Children = { Label("API key"), aKey, aForget, Label("Model"), aModel } };
        var oPanel = new StackPanel { Spacing = 4, Children = { Label("Base URL"), oUrl, Label("API key"), oKey, oForget, Label("Model"), oModel } };
        void Sync()
        {
            aPanel.IsVisible = anthropic.IsChecked == true;
            oPanel.IsVisible = openai.IsChecked == true;
        }
        anthropic.IsCheckedChanged += (_, _) => Sync();
        openai.IsCheckedChanged += (_, _) => Sync();
        Sync();
        var error = new TextBlock { Foreground = Brushes.IndianRed, FontSize = 12, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        var ok = new Button { Content = "Save", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var body = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                enabled,
                Hint("Off by default. When on, the app asks before every request and shows exactly the text that will be sent, after masking "
                     + AiRedaction.Summary + ". Extract fields… works without AI too."),
                Label("Provider"), anthropic, openai, aPanel, oPanel,
                Hint(AiKeyStore.Protection + " Keys are never written to settings or workspace files."),
                error,
                Dialogs.Buttons(ok, cancel),
            },
        };
        var w = Dialogs.Shell("AI settings", body, 520);
        ok.Click += (_, _) =>
        {
            var url = (oUrl.Text ?? "").Trim();
            if (openai.IsChecked == true && !(Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"))
            {
                error.Text = "Enter a base URL such as http://localhost:11434/v1";
                error.IsVisible = true;
                return;
            }
            s.Enabled = enabled.IsChecked == true;
            s.Provider = openai.IsChecked == true ? AiConfig.OpenAi : AiConfig.Anthropic;
            s.AnthropicModel = string.IsNullOrWhiteSpace(aModel.Text) ? AiConfig.DefaultAnthropicModel : aModel.Text.Trim();
            s.OpenAiBaseUrl = url.Length == 0 ? AiConfig.DefaultOpenAiUrl : url;
            s.OpenAiModel = string.IsNullOrWhiteSpace(oModel.Text) ? "llama3.1" : oModel.Text.Trim();
            foreach (var (provider, box, forget) in new[] { (AiConfig.Anthropic, aKey, aForget), (AiConfig.OpenAi, oKey, oForget) })
            {
                if (!string.IsNullOrWhiteSpace(box.Text)) keys.Save(provider, box.Text);
                else if (forget.IsChecked == true) keys.Delete(provider);
            }
            w.Close(true);
        };
        cancel.Click += (_, _) => w.Close(false);
        return await w.ShowDialog<bool>(owner);
    }

    /// <summary>What the consent preview returns: the (possibly edited) text to send, and "don't ask again for this session".</summary>
    public sealed record ConsentResult(string User, bool DontAsk);

    /// <summary>
    /// Shows exactly what will be sent: the fixed instructions (read only) and the redacted, editable request text.
    /// Returns null when the user cancels, and then nothing is sent.
    /// </summary>
    public static async Task<ConsentResult?> Consent(Window owner, string destination, string system, string user, int redacted)
    {
        var instructions = new TextBox
        {
            Text = system,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = Fonts.Mono,
            FontSize = 11.5,
            MaxHeight = 110,
            Opacity = 0.8,
        };
        var text = new TextBox
        {
            Name = "ai-consent-text",
            Text = user,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = Fonts.Mono,
            FontSize = 12,
            MinHeight = 160,
            MaxHeight = 360,
        };
        var dontAsk = new CheckBox { Content = "Don't ask again for this session (text is still masked automatically)" };
        var send = new Button { Content = "Send", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var body = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = $"This will be sent to {destination}:", TextWrapping = TextWrapping.Wrap },
                Label("Instructions"), instructions,
                Label("Your data (you can edit it)"), text,
                Hint(redacted == 0
                    ? "Nothing needed masking. Masked automatically: " + AiRedaction.Summary + "."
                    : $"{redacted} value{(redacted == 1 ? " was" : "s were")} masked (" + AiRedaction.Summary + ")."),
                dontAsk,
                Dialogs.Buttons(send, cancel),
            },
        };
        var w = Dialogs.Shell("Send to AI?", body, 720);
        w.SizeToContent = SizeToContent.Height;
        send.Click += (_, _) => w.Close(new ConsentResult(text.Text ?? "", dontAsk.IsChecked == true));
        cancel.Click += (_, _) => w.Close(null);
        return await w.ShowDialog<ConsentResult?>(owner);
    }

    /// <summary>Shows a proposed quick-filter query for review. Returns the (possibly edited) query to run, or null.</summary>
    public static async Task<string?> ReviewFilter(Window owner, string question, string query, string explanation, Func<string, string?> validate)
    {
        var box = new TextBox { Text = query, FontFamily = Fonts.Mono, MinWidth = 420 };
        var error = new TextBlock { Foreground = Brushes.IndianRed, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        void Check()
        {
            var e = validate(box.Text ?? "");
            error.Text = e is null ? "" : $"Not a valid query: {e}";
            error.IsVisible = e is not null;
        }
        box.TextChanged += (_, _) => Check();
        Check();
        var run = new Button { Content = "Apply filter", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var body = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = $"“{question}”", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 },
                Label("Proposed quick filter"), box,
                new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, IsVisible = explanation.Length > 0 },
                error,
                Hint("Nothing runs until you apply it. You can edit the query first."),
                Dialogs.Buttons(run, cancel),
            },
        };
        var w = Dialogs.Shell("AI filter", body, 560);
        run.Click += (_, _) =>
        {
            Check();
            if (!error.IsVisible) w.Close(box.Text ?? "");
        };
        cancel.Click += (_, _) => w.Close(null);
        return await w.ShowDialog<string?>(owner);
    }
}
