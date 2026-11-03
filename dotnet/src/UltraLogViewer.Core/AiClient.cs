using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UltraLogViewer.Core;

/// <summary>Where AI requests go. <see cref="Key"/> is never logged, saved in settings or put in an error message.</summary>
/// <param name="Provider">"anthropic" (Claude API) or "openai" (any OpenAI-compatible endpoint, e.g. Ollama at http://localhost:11434/v1).</param>
public sealed record AiConfig(string Provider, string Model, string BaseUrl, string? Key)
{
    public const string Anthropic = "anthropic";
    public const string OpenAi = "openai";
    public const string DefaultAnthropicModel = "claude-opus-5-5";
    public const string AnthropicUrl = "https://api.anthropic.com";
    public const string DefaultOpenAiUrl = "http://localhost:11434/v1";

    /// <summary>"api.anthropic.com" or the endpoint's host, for the consent dialog.</summary>
    public string Host => Uri.TryCreate(Provider == Anthropic ? AnthropicUrl : BaseUrl, UriKind.Absolute, out var u) ? u.Authority : BaseUrl;
}

/// <summary>A failed AI request, with a message meant for a toast.</summary>
public sealed class AiException(string message, int status = 0) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// The two wire formats (Claude Messages API and OpenAI-compatible chat completions) over plain HTTP, no SDK.
/// Keep in sync with src/core/aiClient.ts.
/// </summary>
public static class AiClient
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);
    public const string AnthropicVersion = "2026-10-21";
    public const string FallbackBeta = "server-side-fallback-2026-07-01";

    /// <summary>Models that take the server-side refusal fallback (<c>fallbacks: "default"</c>).</summary>
    public static bool UsesFallback(string model) => model is "claude-opus-5-5" or "claude-opus-5" or "claude-fable-5-1" or "claude-sonnet-5-5";

    public static HttpRequestMessage BuildRequest(AiConfig c, AiPrompt p)
    {
        HttpRequestMessage req;
        if (c.Provider == AiConfig.Anthropic)
        {
            var body = new JsonObject
            {
                ["model"] = c.Model,
                ["max_tokens"] = 16000,
                ["system"] = p.System,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = p.User }),
            };
            req = new HttpRequestMessage(HttpMethod.Post, AiConfig.AnthropicUrl + "/v1/messages");
            req.Headers.Add("x-api-key", c.Key ?? "");
            req.Headers.Add("anthropic-version", AnthropicVersion);
            if (UsesFallback(c.Model))
            {
                // On a safety decline the API re-runs the request on a fallback model instead of just stopping.
                body["fallbacks"] = "default";
                req.Headers.Add("anthropic-beta", FallbackBeta);
            }
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        else
        {
            var body = new JsonObject
            {
                ["model"] = c.Model,
                ["stream"] = false,
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "system", ["content"] = p.System },
                    new JsonObject { ["role"] = "user", ["content"] = p.User }),
            };
            req = new HttpRequestMessage(HttpMethod.Post, c.BaseUrl.TrimEnd('/') + "/chat/completions");
            if (!string.IsNullOrEmpty(c.Key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.Key);
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        return req;
    }

    /// <summary>The answer text of a response, or an <see cref="AiException"/> that says what went wrong.</summary>
    public static string ParseResponse(string provider, int status, string body)
    {
        JsonNode? json = null;
        try
        {
            json = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
        }
        if (status is < 200 or > 299)
        {
            var detail = json?["error"] is JsonObject e ? e["message"]?.GetValue<string>() : json?["error"] is JsonValue v ? v.ToString() : null;
            // The server's message is masked too, in case it echoes a key or other data back.
            throw new AiException(StatusMessage(status) + (string.IsNullOrWhiteSpace(detail) ? "" : $": {AiPrompts.Cap(AiRedaction.Redact(detail).Text, 200)}"), status);
        }
        if (json is null) throw new AiException("The AI service sent an answer that is not valid JSON", status);
        string? text;
        if (provider == AiConfig.Anthropic)
        {
            if (json["stop_reason"]?.ToString() == "refusal") throw new AiException("The model declined to answer this request", status);
            // Only text blocks: thinking blocks (empty by default) are not part of the answer.
            text = json["content"] is JsonArray blocks
                ? string.Concat(blocks.OfType<JsonObject>().Where(b => b["type"]?.ToString() == "text").Select(b => b["text"]?.ToString()))
                : null;
        }
        else text = json["choices"]?[0]?["message"]?["content"]?.ToString();
        if (string.IsNullOrWhiteSpace(text)) throw new AiException("The AI service sent an empty answer", status);
        return text.Trim();
    }

    public static string StatusMessage(int status) => status switch
    {
        400 => "The AI service rejected the request (400)",
        401 => "The API key was rejected (401). Check it under AI › AI settings…",
        403 => "The API key may not use this model (403)",
        404 => "Model or endpoint not found (404). Check the model name and base URL",
        413 => "The request is too large for the AI service (413)",
        429 => "Rate limited by the AI service (429). Try again in a moment",
        529 or 503 => $"The AI service is overloaded ({status}). Try again in a moment",
        >= 500 => $"The AI service had an error ({status})",
        _ => $"The AI service answered with HTTP {status}",
    };

    /// <summary>Sends one prompt and returns the answer text. Throws <see cref="AiException"/> (or OperationCanceledException when <paramref name="ct"/> fires).</summary>
    public static async Task<string> SendAsync(HttpClient http, AiConfig c, AiPrompt p, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var req = BuildRequest(c, p);
        try
        {
            using var res = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return ParseResponse(c.Provider, (int)res.StatusCode, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiException($"No answer from {c.Host} after {timeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException e)
        {
            // e.Message names the host and the socket error; it never contains the key (that is a header).
            throw new AiException($"Could not reach {c.Host}: {e.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "the name could not be resolved (offline?)",
                HttpRequestError.ConnectionError => "the connection failed",
                HttpRequestError.SecureConnectionError => "the secure connection failed",
                _ => e.Message,
            }}", (int?)e.StatusCode ?? 0);
        }
    }
}
