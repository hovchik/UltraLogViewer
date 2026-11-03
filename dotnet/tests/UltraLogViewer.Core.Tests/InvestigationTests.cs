using System.Text;
using UltraLogViewer.Core;
using Xunit;

namespace UltraLogViewer.Core.Tests;

/// <summary>Engine parts of the investigation workflow: wildcard and picked-value queries, source links, grouped timelines.</summary>
public class InvestigationTests
{
    private static bool E(string q, Dictionary<string, string> fields, bool cs = false)
    {
        var f = new Dictionary<string, string>(fields, StringComparer.OrdinalIgnoreCase);
        return Query.CompileQuery(q, cs)("", k => f.TryGetValue(k, out var v) ? v : "");
    }

    private static Dictionary<string, string> Logger(string l) => new() { ["logger"] = l };

    // ------------------------------------------------------------ wildcards

    [Fact]
    public void FieldWildcardsMatchTheWholeValue()
    {
        Assert.True(E("logger:com.acme.*", Logger("com.acme.Db")));
        Assert.True(E("logger:com.acme.*", Logger("com.acme.web.Api")));
        Assert.True(E("logger:com.acme.*", Logger("com.acme"))); // the package itself
        Assert.True(E("logger:com.acme.*", Logger("COM.ACME.Db"))); // case-insensitive like other field matches
        Assert.False(E("logger:com.acme.*", Logger("com.acmeX.Db")));
        Assert.False(E("logger:com.acme.*", Logger("org.com.acme.Db"))); // anchored, not "contains"
        Assert.False(E("logger:com.acme.*", Logger("COM.ACME.Db"), cs: true));
        Assert.True(E("thread:worker-*", new() { ["thread"] = "worker-12" }));
        Assert.False(E("thread:worker-*", new() { ["thread"] = "main" }));
        Assert.True(E("logger:*Service", Logger("com.shop.PaymentService")));
        Assert.True(E("-logger:com.acme.*", Logger("org.other.X")));
        // quoted values stay literal; values without "*" keep their "contains" meaning
        Assert.False(E("logger:\"com.acme.*\"", Logger("com.acme.Db")));
        Assert.True(E("logger:acme", Logger("com.acme.Db")));
        Assert.Equal("logger:/^com\\.acme(?:\\..*)?$/", Query.Describe(Query.ParseExpr("logger:com.acme.*")));
    }

    // ---------------------------------------------------------- field picks

    [Fact]
    public void PicksBecomeOneClauseOfTheQuery()
    {
        Assert.Equal("error logger in (a,b)", Query.SetFieldPicks("error", "logger", ["a", "b"], []));
        Assert.Equal("logger:com.acme.*", Query.SetFieldPicks("", "logger", [], ["com.acme."]));
        Assert.Equal("(logger in (a) or logger:com.acme.*)", Query.SetFieldPicks("", "logger", ["a"], ["com.acme."]));
        Assert.Equal("logger in (\"two words\",x)", Query.SetFieldPicks("", "logger", ["two words", "x"], []));
        // replaced in place, the rest of the query untouched
        Assert.Equal("error logger in (a,b,c) -retry", Query.SetFieldPicks("error logger in (a,b) -retry", "logger", ["a", "b", "c"], []));
        Assert.Equal("error -retry", Query.SetFieldPicks("error logger in (a,b) -retry", "logger", [], []));
        Assert.Equal("x thread in (main)", Query.SetFieldPicks("x (logger in (a) or logger:com.*)", "logger", [], []) + " " + Query.PickClause("thread", ["main"], []));
        // another field's clause is left alone
        Assert.Equal("thread in (main) logger in (a)", Query.SetFieldPicks("thread in (main)", "logger", ["a"], []));
    }

    [Fact]
    public void PicksAreReadBackFromTheQuery()
    {
        var (v, p) = Query.FieldPicks("error (logger in (a, \"b c\") or logger:com.acme.* or LOGGER:org.*) -x", "logger");
        Assert.Equal(["a", "b c"], v);
        Assert.Equal(["com.acme.", "org."], p);
        Assert.Empty(Query.FieldPicks("logger:acme", "logger").Values); // a plain field match is not a pick clause
        Assert.Empty(Query.FieldPicks("(logger in (a) or level:error)", "logger").Values); // part of the user's own expression
        Assert.Equal(["main"], Query.FieldPicks("thread in (main)", "thread").Values);
        // what the clause selects
        var q = Query.SetFieldPicks("", "logger", ["com.shop.Api"], ["com.acme."]);
        Assert.True(E(q, Logger("com.shop.Api")));
        Assert.True(E(q, Logger("com.acme.Db")));
        Assert.False(E(q, Logger("com.shop.Cache")));
        Assert.Equal(["com.", "com.acme.", "com.acme.web."], Query.PackagePrefixes("com.acme.web.Api"));
        Assert.Empty(Query.PackagePrefixes("main"));
    }

    // --------------------------------------------------------- source links

    [Fact]
    public void FindsSourceLocationsInStackTraces()
    {
        var text = "java.sql.SQLException: timeout\n\tat com.acme.Db.query(Db.java:42)\n\tat com.acme.App$Inner.main(App.java:10)\n" +
                   "   at Shop.Payments.Charge() in C:\\src\\Shop\\Payments.cs:line 88\n" +
                   "   at Shop.Run() in /home/me/My Shop/Run.cs:line 7\n" +
                   "    at handler (/srv/app/src/server.ts:10:5)\n    at file:///C:/web/app.js:3:14\n" +
                   "  File \"/opt/app/main.py\", line 3, in <module>\n" +
                   "see Foo.kt:12 and http://host:8080/index.html and version 1.2:3";
        var refs = SourceLinks.Find(text);
        Assert.Equal(
            [("Db.java", 42, 0, "com.acme"), ("App.java", 10, 0, "com.acme"), ("C:\\src\\Shop\\Payments.cs", 88, 0, null), ("/home/me/My Shop/Run.cs", 7, 0, null),
             ("/srv/app/src/server.ts", 10, 5, null), ("C:/web/app.js", 3, 14, null), ("/opt/app/main.py", 3, 0, null), ("Foo.kt", 12, 0, null)],
            refs.Select(r => (r.Path, r.Line, r.Column, r.Package)).ToList());
        var db = refs[0];
        Assert.Equal("Db.java:42", text.Substring(db.Start, db.Length));
        Assert.Equal("File \"/opt/app/main.py\", line 3", text.Substring(refs[6].Start, refs[6].Length));
        Assert.Empty(SourceLinks.Find("Handled request 42 for user:7 at 10:00:01"));
    }

    [Fact]
    public void ResolvesAgainstSourceRootsFirstMatchWins()
    {
        var files = new HashSet<string>
        {
            Path.Combine("/code/shop", "src", "main", "java", "com", "acme", "Db.java"),
            Path.Combine("/code/other", "app", "File.cs"),
            Path.Combine("/code/shop", "app", "File.cs"),
            "/abs/x.py",
        };
        bool Exists(string p) => files.Contains(p);
        string[] roots = ["/code/missing", "/code/shop/src/main/java", "/code/shop", "/code/other"];
        // Java frame: package path under a root
        Assert.Equal(Path.Combine("/code/shop/src/main/java", "com", "acme", "Db.java"), SourceLinks.Resolve(new SourceRef(0, 0, "Db.java", 42, 0, "com.acme"), roots, Exists));
        // build-machine path: the longest tail that exists under the first root that has it
        Assert.Equal(Path.Combine("/code/shop", "app", "File.cs"), SourceLinks.Resolve(new SourceRef(0, 0, "D:\\agent\\_work\\1\\s\\app\\File.cs", 1), roots, Exists));
        Assert.Equal("/abs/x.py", SourceLinks.Resolve(new SourceRef(0, 0, "/abs/x.py", 1), [], Exists));
        Assert.Null(SourceLinks.Resolve(new SourceRef(0, 0, "Nope.java", 1), roots, Exists));
    }

    [Fact]
    public void FillsEditorTemplates()
    {
        Assert.Equal("vscode://file/home/me/My%20Shop/Run.cs:7", SourceLinks.ExpandUrl(SourceLinks.TemplateFor("vscode", ""), "/home/me/My Shop/Run.cs", 7, 0));
        Assert.Equal("vscode://file/C:/src/A.cs:3", SourceLinks.ExpandUrl(SourceLinks.TemplateFor("vscode", ""), "C:\\src\\A.cs", 3, 0));
        Assert.Equal("idea://open?file=/src/A.java&line=3", SourceLinks.ExpandUrl(SourceLinks.TemplateFor("intellij", ""), "/src/A.java", 3, 0));
        Assert.Equal("rider://open?file=C:/src/A.cs&line=3", SourceLinks.ExpandUrl(SourceLinks.TemplateFor("rider", ""), "C:\\src\\A.cs", 3, 0));
        var (prog, args) = SourceLinks.ExpandCommand("\"C:\\Program Files\\Ed\\ed.exe\" --goto \"{path}:{line}:{col}\" -n", "/a b/x.cs", 9, 0);
        Assert.Equal("C:\\Program Files\\Ed\\ed.exe", prog);
        Assert.Equal(["--goto", "/a b/x.cs:9:1", "-n"], args);
        Assert.True(SourceLinks.IsUrl("subl://open?url={path}"));
        Assert.False(SourceLinks.IsUrl("code -g {path}:{line}"));
    }

    // ---------------------------------------------------- grouped timeline

    private static string Log(int entries)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < entries; i++)
        {
            var logger = i % 10 == 9 ? $"com.rare.L{i}" : $"com.acme.L{i % 3}";
            sb.Append($"2024-05-01 10:{i / 60:00}:{i % 60:00},000 [t-{i % 2}] INFO  {logger} - message {i}\n");
        }
        return sb.ToString();
    }

    [Fact]
    public void GroupsTheTimelineByAnyFieldWithTopValuesAndOther()
    {
        var g = new HistogramGroups(0, 10, 3);
        foreach (var (t, v) in new[] { (0L, "a"), (1L, "a"), (11L, "b"), (25L, "a"), (25L, ""), (Timestamps.None, "z") })
            g.Add(t, v.Length == 0 ? HistogramGroups.None : v);
        var h = g.Build("logger");
        Assert.Equal(["a", "(none)", "b"], h.Keys); // ties by name
        Assert.Equal([3, 1, 1], h.Totals);
        Assert.Equal([2, 0, 0], h.Counts[0]);
        Assert.Equal([1, 1, 0], h.Counts[2]);
        Assert.False(h.HasOther);

        var many = new HistogramGroups(0, 1000, 1);
        for (var i = 0; i < 12; i++)
            for (var k = 0; k <= i; k++) many.Add(5, $"v{i:00}");
        var top = many.Build("thread");
        Assert.Equal(9, top.Keys.Count);
        Assert.Equal("v11", top.Keys[0]);
        Assert.Equal(HistogramGroups.Other, top.Keys[^1]);
        Assert.Equal(1 + 2 + 3 + 4, top.Totals[^1]); // v00..v03 fold into "other"
        Assert.Equal(top.Totals.Sum(), top.Counts[0].Sum());
        Assert.Equal("thread==v11", HistogramGroups.LegendClause(top, 0));
        Assert.StartsWith("-thread in (v11,v10,", HistogramGroups.LegendClause(top, 8));
        Assert.Equal("-exists(logger)", HistogramGroups.LegendClause(h, 1));
    }

    [Fact]
    public async Task GroupedTimelineFollowsTheFilterAndMatchesTheLevelTimeline()
    {
        using var d = new LogDocument("t") { ScanBatch = 64 };
        await d.AddSourceAsync(TestFiles.Write("g.log", Log(600)), new SourceOptions());
        var plain = d.Histogram(40)!;
        var byLogger = (await d.HistogramByAsync("logger", 40))!;
        Assert.Equal((plain.Start, plain.BucketMs, plain.Counts.Length), (byLogger.Start, byLogger.BucketMs, byLogger.Counts.Length));
        // every bucket holds the same entries, only split differently
        Assert.Equal(plain.Counts.Select(c => c.Other + c.Warn + c.Error), byLogger.Counts.Select(c => c.Sum()));
        Assert.Equal(["com.acme.L0", "com.acme.L1", "com.acme.L2"], byLogger.Keys.Take(3));
        Assert.True(byLogger.HasOther); // 60 rare loggers
        Assert.Equal(600, byLogger.Totals.Sum());
        Assert.Same(byLogger, await d.HistogramByAsync("logger", 40)); // cached for this view
        Assert.Equal(["t-0", "t-1"], (await d.HistogramByAsync("THREAD", 40))!.Keys);

        await d.ApplyFilterAsync(new FilterSpec { Query = "logger:com.acme.*" });
        var filtered = (await d.HistogramByAsync("logger", 40))!;
        Assert.NotSame(byLogger, filtered);
        Assert.Equal(540, filtered.Totals.Sum());
        Assert.False(filtered.HasOther);

        // the sequential pass gives the same answer
        using var seq = new LogDocument("s") { ScanInParallel = false };
        await seq.AddSourceAsync(TestFiles.Write("g2.log", Log(600)), new SourceOptions());
        var s = (await seq.HistogramByAsync("logger", 40))!;
        Assert.Equal(byLogger.Keys, s.Keys);
        Assert.Equal(byLogger.Counts.SelectMany(c => c), s.Counts.SelectMany(c => c));
    }

    [Fact]
    public async Task GroupsAMergedViewBySourceFile()
    {
        using var d = new LogDocument("m");
        await d.AddSourcesAsync([TestFiles.Write("a.log", Log(30)), TestFiles.Write("b.log", Log(10))], _ => new SourceOptions());
        var h = (await d.HistogramByAsync(HistogramGroups.SourceField, 20))!;
        Assert.Equal(2, h.Keys.Count);
        Assert.EndsWith("a.log", h.Keys[0]);
        Assert.Equal([30, 10], h.Totals);
    }
}
