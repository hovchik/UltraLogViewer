using UltraLogViewer.Core;
using Xunit;

namespace UltraLogViewer.Core.Tests;

public class QueryLanguageTests
{
    private static readonly long Now = Timestamps.Build(2024, 5, 1, 12, 0, 0, 0, 0);

    private static string D(string q) => Query.Describe(Query.ParseExpr(q, now: Now));

    /// <summary>Evaluates a query against an entry text and a field map (keys are matched case-insensitively, like LogDocument).</summary>
    private static bool E(string q, string text = "", Dictionary<string, string>? fields = null, bool cs = false)
    {
        var f = new Dictionary<string, string>(fields ?? [], StringComparer.OrdinalIgnoreCase);
        return Query.CompileQuery(q, cs, Now)(text, k => f.TryGetValue(k, out var v) ? v : "");
    }

    private static Dictionary<string, string> F(params (string K, string V)[] kv) => kv.ToDictionary(x => x.K, x => x.V);

    private static string Error(string q) => Assert.Throws<ArgumentException>(() => Query.CompileQuery(q, false, Now)).Message;

    [Fact]
    public void KeepsTheOldSyntax()
    {
        Assert.Equal("(and \"a\" \"b\")", D("a b"));
        Assert.Equal("(and (or \"timeout\" \"refused\") (not \"retry\") logger:\"Db\")", D("timeout OR refused -retry logger:Db"));
        Assert.Equal("(or \"a\" \"b\")", D("a | b"));
        Assert.Equal("(and \"a\" (not \"b\"))", D("a AND NOT b"));
        Assert.Equal("\"exact phrase\"", D("\"exact phrase\""));
        Assert.Equal("logger:/Db$/", D("logger:/Db$/"));
        Assert.Equal("level:\"err\"", D("level:\"err")); // unclosed quote while typing
        Assert.Equal("", D("logger:\"\" \"\""));
        Assert.Equal("/usr/", D("/usr/")); // regex token, as before
        Assert.Equal("(not \"-verbose\")", D("--verbose"));
        Assert.Equal("\"-\"", D("-"));
        Assert.Equal("http:\"//example.com\"", D("http://example.com")); // field:value as before
        Assert.Equal("\"me@host:22\"", D("me@host:22"));
        Assert.Equal("\"foo()\"", D("foo()"));
        Assert.Equal("\"a)\"", D("a)"));
    }

    [Fact]
    public void ParsesBooleanOperatorsWithParentheses()
    {
        Assert.Equal("(and \"a\" (or \"b\" \"c\"))", D("a b OR c")); // OR binds tighter, as it always did
        Assert.Equal("(and (or \"a\" \"b\") (not \"c\"))", D("(a or b) and not c"));
        Assert.Equal("(and \"a\" (or \"b\" (and \"c\" \"d\")))", D("a AND (b OR (c d))"));
        Assert.Equal("(not (and \"a\" \"b\"))", D("NOT (a b)"));
        Assert.Equal("(not (or \"a\" \"b\"))", D("-(a OR b)"));
        Assert.Equal("\"a\"", D("not not a"));
        Assert.Equal("(or (not \"a\") \"b\")", D("not a or b"));
        Assert.Equal("(and \"x\" \"foo()\")", D("(x foo())"));
        Assert.Equal("(or level:\"error\" exists(traceid))", D("(level:error or exists(traceId))"));
        Assert.Equal("(and \"a\" (or \"b\" \"c\"))", D("a && (b || c)"));
    }

    [Fact]
    public void ToleratesQueriesStillBeingTyped()
    {
        Assert.Equal("\"a\"", D("a or"));
        Assert.Equal("\"a\"", D("a and"));
        Assert.Equal("\"a\"", D("a not"));
        Assert.Equal("\"a\"", D("OR a"));
        Assert.Equal("(or \"a\" \"b\")", D("(a or b"));
        Assert.Equal("", D("("));
        Assert.Equal("\"a\"", D("a status>="));
        Assert.Equal("\"a\"", D("a exists(tra"));
        Assert.Equal("\"a\"", D("a user in (ali"));
        Assert.Equal("\"a\"", D("a since:"));
    }

    [Fact]
    public void ReportsBadQueriesClearly()
    {
        Assert.Equal("Unmatched \")\"", Error("a )"));
        Assert.Equal("Empty parentheses \"()\"", Error("a ()"));
        Assert.Equal("exists() needs a field name, e.g. exists(traceId)", Error("exists()"));
        Assert.Equal("user in (): add at least one value, e.g. user in (alice,bob)", Error("user in ()"));
        Assert.Contains("since:soon: use a relative time such as -3h", Error("since:soon"));
        Assert.Contains("duration>2x: unknown unit", Error("duration>2x"));
        Assert.Contains("\"loud\" is not a level", Error("level>=loud"));
        Assert.NotNull(Query.ValidateQuery("/(unclosed/"));
        Assert.Null(Query.ValidateQuery("status>=500 and (user in (a,b) or exists(x)) since:-1h"));
    }


    [Fact]
    public void ParsesComparisonsListsAndExists()
    {
        Assert.Equal("status>=500", D("status>=500"));
        Assert.Equal("status!=200", D("STATUS!=200"));
        Assert.Equal("user?=bob", D("user?=bob"));
        Assert.Equal("x==1", D("x==1"));
        Assert.Equal("a<b", D("a<b"));
        Assert.Equal("user==john smith", D("user==\"john smith\""));
        Assert.Equal("http.status<=499", D("http.status<=499"));
        Assert.Equal("user in (alice,bob)", D("user in (alice, bob)"));
        Assert.Equal("user in (a b,c)", D("user IN (\"a b\",c)"));
        Assert.Equal("(not user in (alice))", D("-user in (alice)"));
        Assert.Equal("(and exists(traceid) \"x\")", D("exists(traceId) x"));
        Assert.Equal("\"a->b\"", D("a->b"));
        Assert.Equal("\"x=>y\"", D("x=>y"));
        Assert.Equal("\"a=b\"", D("a=b")); // a single "=" stays text, so "status=200" still finds that text
    }

    [Fact]
    public void ResolvesRelativeAndAbsoluteTimes()
    {
        TimeNode T(string q) => Assert.IsType<TimeNode>(Query.ParseExpr(q, now: Now));
        Assert.Equal(Now - 3 * 3_600_000L, T("since:-3h").Ms);
        Assert.True(T("since:-3h").Since);
        Assert.Equal(Now - 10 * 60_000L, T("until:-10m").Ms);
        Assert.False(T("until:-10m").Since);
        Assert.Equal(Now - 90 * 60_000L, T("since:1h30m").Ms);
        Assert.Equal(Now, T("until:now").Ms);
        Assert.Equal(Timestamps.Build(2024, 5, 1, 10, 0, 0, 0, null), T("since:2024-05-01T10:00").Ms);
        Assert.Equal(Timestamps.Build(2024, 5, 1, 10, 0, 0, 0, 0), T("since:2024-05-01T10:00Z").Ms);
        Assert.Equal(Timestamps.Build(2024, 5, 1, 10, 0, 5, 500, 120), T("since:\"2024-05-01 10:00:05.5+02:00\"").Ms);
        Assert.Equal(Timestamps.Build(2024, 5, 1, 0, 0, 0, 0, null), T("since:2024-05-01").Ms);
        var today = T("since:today").Ms;
        Assert.Equal(today - 86_400_000, T("since:yesterday").Ms);
        Assert.True(today <= Now && Now - today < 86_400_000);
    }

    [Fact]
    public void ComparesNumbersAndDurations()
    {
        Assert.True(E("status>=500", fields: F(("status", "503"))));
        Assert.False(E("status>=500", fields: F(("status", "200"))));
        Assert.False(E("status>=500"));
        Assert.False(E("status>=500", fields: F(("status", "abc"))));
        Assert.True(E("status<400", fields: F(("status", "399"))));
        Assert.True(E("status>4e2", fields: F(("status", "401"))));
        Assert.True(E("status!=200", fields: F(("status", "404"))));
        Assert.False(E("status!=200", fields: F(("status", "200"))));
        Assert.False(E("status!=200")); // comparisons need the field...
        Assert.True(E("-status==200")); // ...but a negated one does not
        Assert.True(E("status==200", fields: F(("status", "200.0"))));

        Assert.False(E("duration>2s", fields: F(("duration", "250ms"))));
        Assert.True(E("duration>2s", fields: F(("duration", "2.5s"))));
        Assert.True(E("duration>2s", fields: F(("duration", "1m"))));
        Assert.True(E("duration>=2m", fields: F(("duration", "2m30s"))));
        Assert.True(E("duration>2s", fields: F(("duration", "3")))); // a bare number is read in the query's unit
        Assert.False(E("duration>2s", fields: F(("duration", "1.5"))));
        Assert.True(E("duration>500ms", fields: F(("duration", "1500"))));
        Assert.True(E("duration>2", fields: F(("duration", "2.5s")))); // and the other way round
        Assert.True(E("duration<1h", fields: F(("duration", "59m"))));
        Assert.True(E("duration==1s", fields: F(("duration", "1000ms"))));
        Assert.True(E("latency>100us", fields: F(("latency", "0.2ms"))));
    }

    [Fact]
    public void ComparesTextDatesAndLevels()
    {
        Assert.True(E("user==bob", fields: F(("user", "Bob"))));
        Assert.False(E("user==bob", fields: F(("user", "Bob")), cs: true));
        Assert.True(E("user!=bob", fields: F(("user", "alice"))));
        Assert.True(E("name>m", fields: F(("name", "zed"))));
        Assert.False(E("name>m", fields: F(("name", "abe"))));
        Assert.True(E("time>2024-05-01T10:00", fields: F(("time", "2024-05-01 10:30:00"))));
        Assert.False(E("time>2024-05-01T10:00", fields: F(("time", "2024-05-01 09:30:00"))));

        Assert.True(E("level>=warn", fields: F(("level", "WARNING"))));
        Assert.True(E("level>=warn", fields: F(("level", "FATAL"))));
        Assert.False(E("level>=warn", fields: F(("level", "INFO"))));
        Assert.True(E("level<info", fields: F(("level", "DEBUG"))));
        Assert.True(E("level==e", fields: F(("level", "ERROR"))));
        Assert.True(E("level:warn", fields: F(("level", "WARNING")))); // contains, as before
    }

    [Fact]
    public void MatchesListsExistsAndOptionalFields()
    {
        Assert.True(E("user in (alice,bob)", fields: F(("user", "Alice"))));
        Assert.False(E("user in (alice,bob)", fields: F(("user", "carol"))));
        Assert.False(E("user in (alice,bob)"));
        Assert.True(E("status in (500, 503)", fields: F(("status", "503"))));
        Assert.True(E("user in (\"Mary Ann\")", fields: F(("user", "mary ann"))));

        Assert.True(E("exists(traceId)", fields: F(("traceId", "abc"))));
        Assert.False(E("exists(traceId)", fields: F(("traceId", ""))));
        Assert.False(E("exists(traceId)"));
        Assert.True(E("not exists(traceId)"));

        Assert.True(E("env?=prod"));
        Assert.True(E("env?=prod", fields: F(("env", "PROD"))));
        Assert.False(E("env?=prod", fields: F(("env", "dev"))));
        Assert.True(E("http.status>=500", fields: F(("http.status", "502")))); // dotted JSON extras
    }

    [Fact]
    public void FiltersByIndexedTime()
    {
        Dictionary<string, string> At(long ms) => F((Query.TimeField, ms.ToString()));
        Assert.True(E("since:-3h", fields: At(Now - 3_600_000)));
        Assert.False(E("since:-3h", fields: At(Now - 4 * 3_600_000L)));
        Assert.False(E("since:-3h")); // no timestamp: like the time-range filter, it does not pass
        Assert.True(E("until:-10m", fields: At(Now - 3_600_000)));
        Assert.False(E("until:-10m", fields: At(Now - 60_000)));
        Assert.True(E("since:-1h until:-10m error", "an error", At(Now - 30 * 60_000L)));
    }

    [Fact]
    public void TakesTopLevelTimeClausesOutAsARange()
    {
        var r = Query.CompileQueryWithTimeRange("since:-3h until:-1h since:-2h", false, Now);
        Assert.Equal((null, Now - 2 * 3_600_000L, Now - 3_600_000L), (r.Predicate, r.From, r.To));
        var m = Query.CompileQueryWithTimeRange("error since:-1h", false, Now);
        Assert.Equal(Now - 3_600_000, m.From);
        Assert.True(m.Predicate!("an error", _ => "")); // the time is no longer tested per entry
        // Inside OR or NOT the clause stays in the predicate.
        var o = Query.CompileQueryWithTimeRange("since:-1h or level:error", false, Now);
        Assert.Equal((null, null), (o.From, o.To));
        Assert.True(o.Predicate!("", k => k == Query.TimeField ? (Now - 60_000).ToString() : ""));
        Assert.Equal((null, null, null), Query.CompileQueryWithTimeRange("", false, Now));
    }

    [Fact]
    public void CombinesClauses()
    {
        var f = F(("status", "503"), ("user", "bob"), ("level", "ERROR"));
        Assert.True(E("status>=500 and (user in (alice,bob) or exists(traceId))", "x", f));
        Assert.False(E("status>=500 and not user==bob", "x", f));
        Assert.True(E("(timeout or refused) -retry", "connection refused", f));
        Assert.False(E("(timeout or refused) -retry", "refused, retry", f));
        Assert.True(E("level:error OR status<300", "", f));
    }

    [Fact]
    public void RunsIndexedClausesBeforeParsingTheEntry()
    {
        var asked = new List<string>();
        string Field(string k)
        {
            asked.Add(k);
            return k switch { "level" => "INFO", Query.TimeField => (Now - 86_400_000).ToString(), _ => "x" };
        }
        // The parsed-field clause comes first in the text but runs last; level and time reject the entry first.
        Assert.False(Query.CompileQuery("logger:Db level:error", false, Now)("text", Field));
        Assert.Equal(["level"], asked);
        asked.Clear();
        Assert.False(Query.CompileQuery("status>=500 since:-1h", false, Now)("text", Field));
        Assert.Equal([Query.TimeField], asked);
        asked.Clear();
        Assert.True(Query.CompileQuery("logger:Db OR level:info", false, Now)("text", Field));
        Assert.Equal(["level"], asked);
        asked.Clear();
        // Text clauses run before parsed fields too.
        Assert.False(Query.CompileQuery("user==x nomatch", false, Now)("text", Field));
        Assert.Empty(asked);
    }

    [Fact]
    public void SplitsAutocompleteTokensWithOperators()
    {
        Assert.Equal(("", "status", ">=", "5"), Query.SplitToken("status>=5"));
        Assert.Equal(("(", "user", "?=", ""), Query.SplitToken("(user?="));
        Assert.Equal(("-(", "level", ":", "er"), Query.SplitToken("-(level:er"));
        Assert.Equal(("", null, "", "a->b"), Query.SplitToken("a->b"));
        Assert.Equal("status>=500", Query.FieldToken("status", "500", ">="));
        Assert.Equal("user==\"a b\"", Query.FieldToken("user", "a b", "=="));
    }

    [Fact]
    public void TimeShortcutsCountBackFromTheLastEntryUnlessLive()
    {
        Assert.Equal(1000, Query.ShortcutReference(1000, live: false, now: 5000));
        Assert.Equal(5000, Query.ShortcutReference(1000, live: true, now: 5000));
        Assert.Equal(5000, Query.ShortcutReference(null, live: false, now: 5000));
        Assert.Equal(["Now", "-15m", "-1h", "-24h"], Query.TimeShortcuts.Select(s => s.Label));
        Assert.Equal(3_600_000, Query.TimeShortcuts[2].Span);
    }
}
