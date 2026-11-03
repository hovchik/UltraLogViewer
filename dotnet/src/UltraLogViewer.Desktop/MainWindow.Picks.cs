using Avalonia.Controls;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop;

/// <summary>
/// Several values of one field at once (Ctrl+click in the Fields panel) and logger packages (com.acme.*). The picks are
/// one clause of the quick filter (<see cref="Query.SetFieldPicks"/>), so the query box always shows what is filtered.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Replaces the quick filter and applies it (a bad query is reported and not applied).</summary>
    public void ApplyQuery(string query)
    {
        if (_active is not { } tab) return;
        SetQuickFilterText(query);
        if (query == tab.Filter.Query) return;
        if (Query.ValidateQuery(query, tab.Filter.QueryCaseSensitive) is { } err)
        {
            Toast($"Quick filter: {err}", true);
            return;
        }
        tab.Filter.Query = query;
        ApplyFilter();
    }

    /// <summary>Adds a clause to the quick filter (a legend item of the timeline, for instance).</summary>
    public void AddQueryClause(string clause)
    {
        if (_active is not { } tab || clause.Length == 0) return;
        ApplyQuery((tab.Filter.Query.TrimEnd() + " " + clause).Trim());
    }

    public (List<string> Values, List<string> Prefixes) FieldPicks(DocumentTab tab, string field) => Query.FieldPicks(tab.Filter.Query, field);

    public void SetFieldPicks(string field, IEnumerable<string> values, IEnumerable<string> prefixes)
    {
        if (_active is { } tab) ApplyQuery(Query.SetFieldPicks(tab.Filter.Query, field, values, prefixes));
    }

    /// <summary>Ctrl+click on a value: adds it to the field's picks, or takes it out again.</summary>
    public void TogglePick(string field, string value)
    {
        if (_active is not { } tab) return;
        var (values, prefixes) = FieldPicks(tab, field);
        if (!values.Remove(value)) values.Add(value);
        SetFieldPicks(field, values, prefixes);
    }

    /// <summary>A package node: only that package (click), or added to / taken out of the picks (Ctrl+click).</summary>
    public void PickPackage(string field, string prefix, bool toggle)
    {
        if (_active is not { } tab) return;
        var (values, prefixes) = FieldPicks(tab, field);
        if (!toggle) SetFieldPicks(field, [], [prefix]);
        else
        {
            if (!prefixes.Remove(prefix)) prefixes.Add(prefix);
            SetFieldPicks(field, values, prefixes);
        }
    }

    /// <summary>"Only package com.acme.*" for a dotted value such as a logger name: its package and the one above.</summary>
    private List<Control> PackageItems(string field, string value)
    {
        var items = new List<Control>();
        if (!Query.IsDottedName(value)) return items;
        var prefixes = Query.PackagePrefixes(value);
        foreach (var prefix in prefixes.AsEnumerable().Reverse().Take(2))
            items.Add(Item($"Only package {prefix}*".Replace("_", "__"), () => PickPackage(field, prefix, toggle: false)));
        return items;
    }
}
