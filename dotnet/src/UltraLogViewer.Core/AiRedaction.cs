using System.Text.RegularExpressions;

namespace UltraLogViewer.Core;

/// <summary>
/// Masks secrets and personal data in text before it is shown in the AI consent preview (and then sent).
/// The rules run in order and each one leaves a placeholder that no later rule matches.
/// Keep in sync with src/core/aiRedaction.ts: both apps share the test vectors.
/// </summary>
public static class AiRedaction
{
    private const RegexOptions O = RegexOptions.CultureInvariant;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(1);

    private const string Hex = "[0-9A-Fa-f]{1,4}";

    private static readonly (string Name, Regex Re, Func<Match, string?> Replace)[] Rules =
    [
        // A PEM private key block, whole (its base64 lines are split by + and /, so the token rule would miss parts).
        ("pem", new Regex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z0-9 ]*PRIVATE KEY-----|$)", O, Limit), _ => "<private key>"),
        // Authorization: Bearer xyz (also "Basic"): the scheme stays, the credential (8+ chars) goes.
        ("bearer", new Regex(@"\b((?i:bearer)|Basic)(\s+)[A-Za-z0-9\-._~+/]{8,}=*", O, Limit), m => $"{m.Groups[1].Value}{m.Groups[2].Value}<token>"),
        // Authorization: <any other scheme> credential, or a bare credential.
        ("authorization", new Regex(@"\b((?:Proxy-)?Authorization)(\s*[:=]\s*)((?:[A-Za-z][\w-]*\s+)?)[A-Za-z0-9\-._~+/]{8,}=*", O | RegexOptions.IgnoreCase, Limit),
            m => $"{m.Groups[1].Value}{m.Groups[2].Value}{m.Groups[3].Value}<token>"),
        ("jwt", new Regex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", O, Limit), _ => "<jwt>"),
        // password=..., "secret": "...", api_key: ..., token=...: keep the key, mask the value.
        // (not \b: DB_PASSWORD and db_password count too)
        ("secret", new Regex(@"(?<![A-Za-z0-9])((?:pass(?:word|wd|phrase)?|pwd|secret|client[_-]?secret|token|access[_-]?token|refresh[_-]?token|api[_-]?key|access[_-]?key|private[_-]?key|credentials?|auth)""?)(\s*[:=]\s*)(""[^""]*""|'[^']*'|[^\s,;&""']+)",
            O | RegexOptions.IgnoreCase, Limit), m => $"{m.Groups[1].Value}{m.Groups[2].Value}<redacted>"),
        // Well-known key formats: OpenAI/Anthropic sk-..., AWS AKIA..., GitHub ghp_..., Slack xox?-..., Google AIza...
        ("key", new Regex(@"\b(?:sk-[A-Za-z0-9_-]{16,}|AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{30,}|xox[abprs]-[A-Za-z0-9-]{10,}|AIza[0-9A-Za-z_-]{35})", O, Limit), _ => "<key>"),
        ("email", new Regex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}\b", O, Limit), _ => "<email>"),
        // 13-19 digits (optionally in groups split by spaces or dashes) that pass the Luhn check and start like a card.
        ("card", new Regex(@"(?<![\d.])[3-6]\d{3}(?:[ -]?\d){9,15}(?![\d.])", O, Limit), m => Luhn(m.Value) ? "<card>" : null),
        ("ipv6", new Regex($@"(?<![\w:.])(?:(?:{Hex}:){{7}}{Hex}|(?:{Hex}:){{1,6}}(?::{Hex}){{1,6}}|(?:{Hex}:){{1,7}}:|::(?:{Hex}:){{0,5}}{Hex})(?![\w:])", O, Limit), _ => "<ipv6>"),
        ("ipv4", new Regex(@"(?<![\w.])(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?![\w.]*\w)", O, Limit), _ => "<ip>"),
        // Long random-looking tokens: 32+ chars mixing upper case, lower case and digits (GUIDs and hex hashes stay).
        ("token", new Regex(@"(?<![\w-])(?=[\w-]*[a-z])(?=[\w-]*[A-Z])(?=[\w-]*\d)[A-Za-z0-9_-]{32,}(?![\w-])", O, Limit), _ => "<key>"),
    ];

    /// <summary>The kinds of values that are masked, for the consent dialog.</summary>
    public const string Summary = "emails, IP addresses, bearer tokens and JWTs, API keys, password=… values and card numbers";

    public static (string Text, int Count) Redact(string text)
    {
        var count = 0;
        foreach (var (_, re, replace) in Rules)
        {
            try
            {
                text = re.Replace(text, m =>
                {
                    var r = replace(m);
                    if (r is null) return m.Value;
                    count++;
                    return r;
                });
            }
            catch (RegexMatchTimeoutException)
            {
                // pathological input: leave this rule out rather than hang; the user still sees and can edit the text
            }
        }
        return (text, count);
    }

    internal static bool Luhn(string s)
    {
        var digits = s.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19) return false;
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i];
            if (i % 2 == 1 && (d *= 2) > 9) d -= 9;
            sum += d;
        }
        return sum % 10 == 0;
    }
}
