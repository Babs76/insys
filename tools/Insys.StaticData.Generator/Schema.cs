/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : StaticData.Generator
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
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

/// <summary>
/// Reads the static data tables from the migrations, in order: the CREATE TABLE statements of V0002, static tables
/// created by later migrations in the same form (a table is static data when it has the record_status CHECK), and
/// single-line <c>ALTER TABLE … ADD COLUMN</c> and <c>ADD CONSTRAINT …_ckN CHECK</c> statements on them. The SQL is
/// authoritative for names and types (PROMPT §7).
/// </summary>
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

    [GeneratedRegex(@"^ALTER TABLE (?<schema>[a-z_]+)\.(?<table>[a-z0-9_]+) ADD COLUMN (?<name>[a-z0-9_]+)\s+(?<type>[a-z0-9_]+(?:\([0-9, ]+\))?(?:\[\])?)(?<rest>[^,]*);$")]
    private static partial Regex AddColumn();

    [GeneratedRegex(@"^ALTER TABLE (?<schema>[a-z_]+)\.(?<table>[a-z0-9_]+) ADD (?<constraint>CONSTRAINT [a-z0-9_]+_ck\d+ CHECK .*);$")]
    private static partial Regex AddCheck();

    [GeneratedRegex(@"^V(?<number>\d+)__.*\.sql$")]
    private static partial Regex MigrationFile();

    /// <summary>The static data tables after every versioned migration in <paramref name="directory"/>.</summary>
    public static Dictionary<string, Table> ReadAll(string directory)
    {
        var tables = new Dictionary<string, Table>();
        var files = Directory.GetFiles(directory, "V*.sql").Select(f => (Path: f, Match: MigrationFile().Match(Path.GetFileName(f))))
            .Where(f => f.Match.Success).OrderBy(f => int.Parse(f.Match.Groups["number"].Value)).Select(f => f.Path);
        foreach (var file in files)
        {
            foreach (var table in Read(file).Where(t => t.Statuses.Length > 0))
            {
                tables[table.FullName] = table;
            }

            foreach (var line in File.ReadLines(file))
            {
                if (AddColumn().Match(line) is { Success: true } add && tables.TryGetValue($"{add.Groups["schema"].Value}.{add.Groups["table"].Value}", out var table))
                {
                    table.Columns.Add(new Column(add.Groups["name"].Value, add.Groups["type"].Value, add.Groups["rest"].Value.Contains("NOT NULL")));
                }
                else if (AddCheck().Match(line) is { Success: true } check && tables.TryGetValue($"{check.Groups["schema"].Value}.{check.Groups["table"].Value}", out var checkedTable))
                {
                    Table? ignored = checkedTable;
                    ReadConstraint(checkedTable, ref ignored, "  " + check.Groups["constraint"].Value);
                }
            }
        }

        return tables;
    }

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
