using System.Text.RegularExpressions;

namespace Insys.StaticData.Generator;

internal sealed record Column(string Name, string SqlType, bool NotNull)
{
    public List<string>? AllowedValues { get; set; }

    public bool ArrayValues { get; set; }

    public (int Min, int Max)? Between { get; set; }

    public string? Pattern { get; set; }

    public int? MaxLength => Regex.Match(SqlType, @"^(?:varchar|char)\((\d+)\)$") is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;
}

internal sealed record Table(string Schema, string Name, List<Column> Columns, string[] Statuses)
{
    public string FullName => $"{Schema}.{Name}";
}

/// <summary>Reads the CREATE TABLE statements in V0002. The SQL is authoritative for names and types (PROMPT §7).</summary>
internal static partial class Schema
{
    public static readonly HashSet<string> CommonColumns =
    [
        "id", "tenant_id", "code", "version", "effective_from", "effective_to", "record_status", "supersedes_id", "labels", "description",
        "record_source", "record_source_version", "external_mappings", "record_class", "sort_order", "display_flags", "change_request_id",
        "created_at", "created_by", "approved_at", "approved_by", "change_reason", "row_version",
    ];

    [GeneratedRegex(@"^CREATE TABLE (?<schema>[a-z_]+)\.(?<table>[a-z0-9_]+) \($")]
    private static partial Regex CreateTable();

    [GeneratedRegex(@"^  (?<name>[a-z0-9_]+)\s+(?<type>[a-z0-9_]+(?:\([0-9, ]+\))?(?:\[\])?)(?<rest>.*)$")]
    private static partial Regex ColumnLine();

    [GeneratedRegex(@"_stat CHECK \(record_status IN \((?<values>[^)]*)\)\)")]
    private static partial Regex StatusCheck();

    [GeneratedRegex(@"_ck\d+ CHECK \((?<column>[a-z0-9_]+) IN \((?<values>[^)]*)\)\)")]
    private static partial Regex InCheck();

    [GeneratedRegex(@"_ck\d+ CHECK \((?<column>[a-z0-9_]+) <@ ARRAY\[(?<values>[^\]]*)\]::text\[\]\)")]
    private static partial Regex ArrayCheck();

    [GeneratedRegex(@"_ck\d+ CHECK \((?<column>[a-z0-9_]+) BETWEEN (?<min>-?\d+) AND (?<max>-?\d+)\)")]
    private static partial Regex BetweenCheck();

    [GeneratedRegex(@"_ck\d+ CHECK \((?<column>[a-z0-9_]+) ~ '(?<pattern>[^']*)'\)")]
    private static partial Regex PatternCheck();

    [GeneratedRegex(@"'([^']*)'")]
    private static partial Regex Quoted();

    public static List<Table> Read(string path)
    {
        var tables = new List<Table>();
        Table? current = null;
        foreach (var line in File.ReadLines(path))
        {
            if (CreateTable().Match(line) is { Success: true } create)
            {
                current = new Table(create.Groups["schema"].Value, create.Groups["table"].Value, [], []);
                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (line.StartsWith(");", StringComparison.Ordinal))
            {
                tables.Add(current);
                current = null;
                continue;
            }

            if (line.StartsWith("  CONSTRAINT", StringComparison.Ordinal))
            {
                ReadConstraint(current, ref current, line);
                continue;
            }

            if (ColumnLine().Match(line) is { Success: true } column)
            {
                current.Columns.Add(new Column(column.Groups["name"].Value, column.Groups["type"].Value, column.Groups["rest"].Value.Contains("NOT NULL")));
            }
        }

        return tables;
    }

    private static void ReadConstraint(Table table, ref Table? current, string line)
    {
        if (StatusCheck().Match(line) is { Success: true } status)
        {
            current = table with { Statuses = Values(status.Groups["values"].Value) };
            return;
        }

        if (InCheck().Match(line) is { Success: true } check)
        {
            Find(table, check.Groups["column"].Value).AllowedValues = [.. Values(check.Groups["values"].Value)];
        }
        else if (ArrayCheck().Match(line) is { Success: true } array)
        {
            var column = Find(table, array.Groups["column"].Value);
            column.AllowedValues = [.. Values(array.Groups["values"].Value)];
            column.ArrayValues = true;
        }
        else if (BetweenCheck().Match(line) is { Success: true } between)
        {
            Find(table, between.Groups["column"].Value).Between = (int.Parse(between.Groups["min"].Value), int.Parse(between.Groups["max"].Value));
        }
        else if (PatternCheck().Match(line) is { Success: true } pattern)
        {
            Find(table, pattern.Groups["column"].Value).Pattern = pattern.Groups["pattern"].Value;
        }
        else if (line.Contains("_ck", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{table.FullName}: unsupported CHECK constraint: {line.Trim()}");
        }
    }

    private static Column Find(Table table, string name) =>
        table.Columns.SingleOrDefault(c => c.Name == name) ?? throw new InvalidOperationException($"{table.FullName}: CHECK on unknown column {name}.");

    private static string[] Values(string list) => Quoted().Matches(list).Select(m => m.Groups[1].Value).ToArray();
}
