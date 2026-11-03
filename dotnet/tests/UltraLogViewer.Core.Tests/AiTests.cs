using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace UltraLogViewer.Core.Tests;

/// <summary>
/// Redaction, the offline regex guesser, prompt building, answer checking and both wire formats (through a fake HTTP
/// handler: no real network). Redaction and guesses use test/fixtures/ai-vectors.json, which the Electron tests share.
/// </summary>
public class AiTests
{
    private static JsonNode Vectors()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "test", "fixtures", "ai-vectors.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return JsonNode.Parse(File.ReadAllText(Path.Combine(dir.FullName, "test", "fixtures", "ai-vectors.json")))!;
    }

    [Fact]
    public void Redaction_matches_the_shared_vectors()
    {
        foreach (var v in Vectors()["redact"]!.AsArray())
        {
            var (text, count) = AiRedaction.Redact(v!["in"]!.ToString());
            Assert.Equal(v["out"]!.ToString(), text);
            Assert.Equal(v["count"]!.GetValue<int>(), count);
        }
    }

    [Fact]
    public void Redaction_masks_each_kind_and_keeps_ordinary_text()
    {
        Assert.Equal("mail <email>", AiRedaction.Redact("mail a.b+c@mail.example.org").Text);
        Assert.Equal("Authorization: Basic <token>", AiRedaction.Redact("Authorization: Basic dXNlcjpwYXNz").Text);
        Assert.Equal("pwd: <redacted>; secret=<redacted>", AiRedaction.Redact("pwd: abc; secret='x y'").Text);
        Assert.Equal("gh <key>", AiRedaction.Redact("gh ghp_" + new string('a', 36)).Text);
        Assert.Equal("card <card>", AiRedaction.Redact("card 5555-5555-5555-4444").Text);
        Assert.Equal("ok 2024-05-01T10:00:00.123Z took 15ms status=500", AiRedaction.Redact("ok 2024-05-01T10:00:00.123Z took 15ms status=500").Text);
        Assert.True(AiRedaction.Luhn("4111111111111111"));
        Assert.False(AiRedaction.Luhn("4111111111111112"));
    }

    [Fact]
    public void Guess_matches_the_shared_vectors_and_parses_the_line()
    {
        foreach (var v in Vectors()["guess"]!.AsArray())
        {
            var line = v!["line"]!.ToString();
            var p = AiRegexGuess.Guess(line);
            Assert.Equal(v["pattern"]!.ToString(), p);
            var f = new CustomRegexFormat(new CustomFormat("g", p), 2024);
            Assert.True(f.IsEntryStart(line), p);
            var fields = f.Parse(line);
            foreach (var (k, val) in v["groups"]!.AsObject())
                Assert.Equal(val!.ToString(), fields[k]);
        }
    }

    [Theory]
    [InlineData("2024-05-01 10:00:00 INFO x", "level")]
    [InlineData("WARN: disk almost full", "level")]
    [InlineData("10.1.2.3 GET /", "ip")]
    [InlineData("a=1 b=\"two words\" rest of it", "b")]
    [InlineData("com.acme.Db query slow", "logger")]
    public void Guess_finds_the_token_classes(string line, string group)
    {
        var p = AiRegexGuess.Guess(line);
        var m = new Regex(p).Match(line);
        Assert.True(m.Success, p);
        Assert.True(m.Groups[group].Success, $"{group} missing in {p}");
        Assert.EndsWith("(?<message>.*)$", p);
    }

    [Fact]
    public void Guess_always_produces_a_regex_that_matches()
    {
        foreach (var line in new[] { "", "   ", "[unterminated", "\"open quote", "message=hi there", "### header ###", "x=1 x=2 x=3", "€ ünïcödé 文字" })
        {
            var p = AiRegexGuess.Guess(line);
            Assert.Matches(new Regex(p), line);
        }
        Assert.Contains("(?<msg>", AiRegexGuess.Guess("message=hello"));
        Assert.Equal(@"a\.b\*\(c\)\/d #", AiRegexGuess.Escape("a.b*(c)/d #"));
    }

    [Fact]
    public void Prompts_carry_the_context_and_the_rules()
    {
        var x = AiPrompts.Extract(["line one", "line two"]);
        Assert.Contains("(?<name>...)", x.System);
        Assert.Contains("JSON only", x.System);
        Assert.StartsWith("Sample lines", x.User);
        Assert.Contains("line one\nline two", x.User);

        var e = AiPrompts.ExplainEntry("Log4j", ["b1", "b2"], "the entry", ["a1"], filtered: true);
        Assert.Contains("Log format: Log4j", e.User);
        Assert.Contains("filtered view", e.User);
        Assert.Contains("b1\nb2\n\n>>> Entry to explain:\nthe entry\n<<<\n\nContext after:\na1", e.User);
        var big = AiPrompts.ExplainEntry("x", [], new string('z', 10_000), [], false);
        Assert.True(big.User.Length < 5000);

        var pt = AiPrompts.ExplainPattern("JSON", "user <*> logged in", 1234, ["user 7 logged in"]);
        Assert.Contains("seen 1,234 times", pt.User);

        var f = AiPrompts.Filter("errors from the db logger in the last hour",
            [("logger", (IReadOnlyList<string>)["com.acme.Db", "com.acme.Api"]), ("status", (IReadOnlyList<string>)["200"])],
            [("ERROR", 5), ("INFO", 90)], 1714557600000, 1714561200000, new DateTime(2024, 5, 1, 12, 0, 0));
        Assert.Contains("Request: errors from the db logger", f.User);
        Assert.Contains("- logger: com.acme.Db, com.acme.Api", f.User);
        Assert.Contains("Levels present: error (5), info (90)", f.User);
        Assert.Contains("Now: 2024-05-01T12:00:00", f.User);
        Assert.Contains("since:-3h", f.System);
        Assert.Contains("\"query\"", f.System);
    }

    [Fact]
    public void Extract_answers_are_checked()
    {
        const string sample = "2024-05-01 10:00:00 INFO hello";
        var ok = AiPrompts.ParseExtract("Here you go:\n```json\n{\"name\": \"My app\", \"pattern\": \"^(?<time>\\\\S+ \\\\S+) (?<level>\\\\w+) (?<message>.*)$\"}\n```", sample, out var err);
        Assert.Null(err);
        Assert.Equal("My app", ok!.Value.Name);
        Assert.Equal(@"^(?<time>\S+ \S+) (?<level>\w+) (?<message>.*)$", ok.Value.Pattern);

        Assert.Null(AiPrompts.ParseExtract("I cannot do that", sample, out err));
        Assert.Contains("JSON", err);
        Assert.Null(AiPrompts.ParseExtract("{\"pattern\": \"unterminated}", sample, out err));
        Assert.Contains("JSON", err);
        Assert.Null(AiPrompts.ParseExtract("{\"pattern\": \"(?<a>[x\"}", sample, out err));
        Assert.Contains("does not compile", err);
        Assert.Null(AiPrompts.ParseExtract("{\"pattern\": \"^\\\\d+\"}", sample, out err));
        Assert.Contains("no named groups", err);
        Assert.Null(AiPrompts.ParseExtract("{\"pattern\": \"^(?<x>ERROR)\"}", sample, out err));
        Assert.Contains("does not match", err);
        Assert.Equal("Extracted", AiPrompts.ParseExtract("{\"pattern\": \"(?<message>.*)\"}", sample, out _)!.Value.Name);
    }

    [Fact]
    public void Filter_answers_are_read_and_validated()
    {
        var r = AiPrompts.ParseFilter("{\"query\": \" level>=error logger:*Db* since:-1h \", \"explanation\": \"Errors from Db\"}", out var err);
        Assert.Null(err);
        Assert.Equal("level>=error logger:*Db* since:-1h", r!.Value.Query);
        Assert.Equal("Errors from Db", r.Value.Explanation);
        Assert.Null(Query.ValidateQuery(r.Value.Query));
        Assert.Null(AiPrompts.ParseFilter("level:error", out err));
        Assert.NotNull(err);
        Assert.Null(AiPrompts.ParseFilter("{\"query\": \"\"}", out _));
        // A syntactically broken query comes back as text; the validator catches it before it runs.
        Assert.NotNull(Query.ValidateQuery(AiPrompts.ParseFilter("{\"query\": \"/[/\"}", out _)!.Value.Query));
    }

    // ---------------------------------------------------------------- wire formats

    private sealed class FakeHandler(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request, body));
            return await respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static readonly AiPrompt P = new("SYS", "USER TEXT");

    [Fact]
    public async Task Anthropic_wire_format_end_to_end()
    {
        var h = new FakeHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
            """{"id":"msg_1","type":"message","role":"assistant","content":[{"type":"thinking","thinking":""},{"type":"text","text":"Hello "},{"type":"text","text":"there"}],"stop_reason":"end_turn"}""")));
        var c = new AiConfig(AiConfig.Anthropic, "claude-opus-5-5", "", "sk-test-key");
        var answer = await AiClient.SendAsync(new HttpClient(h), c, P, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("Hello there", answer);
        var (req, body) = h.Seen.Single();
        Assert.Equal("https://api.anthropic.com/v1/messages", req.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("sk-test-key", req.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2026-10-21", req.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("server-side-fallback-2026-07-01", req.Headers.GetValues("anthropic-beta").Single());
        Assert.Null(req.Headers.Authorization);
        var j = JsonNode.Parse(body)!;
        Assert.Equal("claude-opus-5-5", j["model"]!.ToString());
        Assert.Equal("SYS", j["system"]!.ToString());
        Assert.Equal("user", j["messages"]![0]!["role"]!.ToString());
        Assert.Equal("USER TEXT", j["messages"]![0]!["content"]!.ToString());
        Assert.Equal(16000, j["max_tokens"]!.GetValue<int>());
        Assert.Equal("default", j["fallbacks"]!.ToString());
        Assert.Null(j["temperature"]);

        // Other models get no fallback parameter (it would be rejected).
        var h2 = new FakeHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"content":[{"type":"text","text":"x"}]}""")));
        await AiClient.SendAsync(new HttpClient(h2), c with { Model = "claude-haiku-4-5" }, P, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.False(h2.Seen[0].Request.Headers.Contains("anthropic-beta"));
        Assert.Null(JsonNode.Parse(h2.Seen[0].Body)!["fallbacks"]);
    }

    [Fact]
    public async Task OpenAi_compatible_wire_format_end_to_end()
    {
        var h = new FakeHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
            """{"choices":[{"index":0,"message":{"role":"assistant","content":"  local answer "}}]}""")));
        var c = new AiConfig(AiConfig.OpenAi, "llama3.1", "http://localhost:11434/v1/", null);
        Assert.Equal("local answer", await AiClient.SendAsync(new HttpClient(h), c, P, TimeSpan.FromSeconds(5), CancellationToken.None));
        var (req, body) = h.Seen.Single();
        Assert.Equal("http://localhost:11434/v1/chat/completions", req.RequestUri!.ToString());
        Assert.Null(req.Headers.Authorization); // no key: no header
        var j = JsonNode.Parse(body)!;
        Assert.Equal("llama3.1", j["model"]!.ToString());
        Assert.Equal("system", j["messages"]![0]!["role"]!.ToString());
        Assert.Equal("SYS", j["messages"]![0]!["content"]!.ToString());
        Assert.Equal("USER TEXT", j["messages"]![1]!["content"]!.ToString());
        Assert.False(j["stream"]!.GetValue<bool>());

        var h2 = new FakeHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"y"}}]}""")));
        await AiClient.SendAsync(new HttpClient(h2), c with { Key = "k-123" }, P, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("Bearer k-123", h2.Seen[0].Request.Headers.Authorization!.ToString());
    }

    [Theory]
    [InlineData(401, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", "API key was rejected (401)")]
    [InlineData(429, """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""", "Rate limited")]
    [InlineData(529, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", "overloaded (529)")]
    [InlineData(500, "<html>oops</html>", "had an error (500)")]
    [InlineData(200, "not json", "not valid JSON")]
    [InlineData(200, """{"content":[],"stop_reason":"refusal"}""", "declined")]
    [InlineData(200, """{"content":[]}""", "empty answer")]
    public async Task Errors_become_clear_messages(int status, string body, string expected)
    {
        var h = new FakeHandler((_, _) => Task.FromResult(Json((HttpStatusCode)status, body)));
        var c = new AiConfig(AiConfig.Anthropic, "claude-opus-5-5", "", "sk-secret-value");
        var e = await Assert.ThrowsAsync<AiException>(() => AiClient.SendAsync(new HttpClient(h), c, P, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Contains(expected, e.Message);
        Assert.DoesNotContain("sk-secret-value", e.Message);
    }

    [Fact]
    public async Task Timeout_cancel_and_network_errors()
    {
        var c = new AiConfig(AiConfig.OpenAi, "m", "http://localhost:1/v1", null);
        var e = await Assert.ThrowsAsync<AiException>(() => AiClient.SendAsync(new HttpClient(new CancelAware()), c, P, TimeSpan.FromMilliseconds(100), CancellationToken.None));
        Assert.Contains("No answer from localhost:1 after", e.Message);

        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AiClient.SendAsync(new HttpClient(new CancelAware()), c, P, TimeSpan.FromSeconds(30), cts.Token));

        var down = new FakeHandler((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (localhost:1)"));
        e = await Assert.ThrowsAsync<AiException>(() => AiClient.SendAsync(new HttpClient(down), c, P, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal("Could not reach localhost:1: the connection failed", e.Message);
    }

    /// <summary>Waits until the request is cancelled, like a server that never answers.</summary>
    private sealed class CancelAware : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public void Host_names_the_destination()
    {
        Assert.Equal("api.anthropic.com", new AiConfig(AiConfig.Anthropic, "m", "", null).Host);
        Assert.Equal("localhost:11434", new AiConfig(AiConfig.OpenAi, "m", "http://localhost:11434/v1", null).Host);
    }
}
