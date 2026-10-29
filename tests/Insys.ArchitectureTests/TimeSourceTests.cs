/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ArchitectureTests
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
using Insys.Testing;

namespace Insys.ArchitectureTests;

/// <summary>
/// One time source (V0093, Ade, 29 October 2026): the application decides by its own clock (IClock, TimeProvider) and passes
/// it into SQL as a parameter. SQL written in the application never reads the database's clock, so a database whose clock
/// differs (a test clock, a drifting server) never makes a due-time, cut-over or month decision on another time.
/// </summary>
public sealed partial class TimeSourceTests
{
    [Fact]
    public void ApplicationSql_NeverReadsTheDatabasesClock_V0093()
    {
        var found = Directory.EnumerateFiles(Path.Combine(RepositoryPaths.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetRelativePath(RepositoryPaths.Root, f), Line: i + 1, Text: line)))
            .Where(l => !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.Text.TrimStart().StartsWith("///", StringComparison.Ordinal)
                        && DatabaseClock().IsMatch(l.Text))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();
        found.ShouldBeEmpty("pass the application's time as a parameter instead");
    }

    /// <summary>
    /// Every SQL file in the repository except the applied migrations (immutable; the functions they leave in force are
    /// checked in the database by RecordTimeSourceTests): the database's clock only on reviewed lines, each with its reason.
    /// A new folder or file is scanned as it appears.
    /// </summary>
    [Fact]
    public void EverySqlFile_ReadsTheDatabasesClock_OnlyWhereReviewed_V0093()
    {
        var root = RepositoryPaths.Root;
        var found = Directory.EnumerateFiles(root, "*.sql", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(f => !f.StartsWith(".git/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal) && !f.Contains("/obj/", StringComparison.Ordinal)
                        && !AppliedMigration().IsMatch(f))
            .SelectMany(f => File.ReadLines(Path.Combine(root, f)).Select((line, i) => (File: f, Line: i + 1, Text: line.Trim())))
            .Where(l => !l.Text.StartsWith("--", StringComparison.Ordinal) && DatabaseClock().IsMatch(l.Text))
            .Where(l => !(Reviewed.TryGetValue(l.File, out var reviewed) && reviewed.Any(r => r.Line.IsMatch(l.Text))))
            .Select(l => $"{l.File}:{l.Line}: {l.Text}")
            .ToList();
        found.ShouldBeEmpty("pass the application's time as a parameter, or review the line into TimeSourceTests.Reviewed with its reason");
    }

    /// <summary>The reviewed uses of the database's clock in SQL files, by file: the lines they may be on, and why.</summary>
    private static readonly Dictionary<string, (Regex Line, string Why)[]> Reviewed = new(StringComparer.Ordinal)
    {
        ["db/bootstrap/operator.sql"] =
        [
            (new Regex(@"^SELECT audit\.ensure_monthly_partitions\(current_date, 3\);$"),
                "Provisioning runs before any host exists, so there is no application clock yet; the hosts then keep the partitions by theirs (Ade, 29 October 2026)"),
            (new Regex(@"'bootstrap'|admin_id|'ACTIVE', now\(\)\)$"), "Stamps the rows provisioning inserts (created, approved, effective from); nothing is decided by the time"),
        ],
        ["db/migrations/R__error_code_catalogue.sql"] = [(new Regex(@"'migration', now\(\), 'migration'"), "Stamps the seeded rows' created_at at release")],
        ["db/migrations/R__function_catalogue.sql"] =
        [
            (new Regex(@"'migration', now\(\), 'migration'"), "Stamps the seeded rows' created_at at release"),
            (new Regex(@"effective_from < current_date|ELSE current_date END"), "Versions the catalogue on the release's date, run by the migrator with no application running (ADR-026 §7)"),
        ],
    };

    [GeneratedRegex(@"^db/migrations/V\d{4}__[^/]+\.sql$")]
    private static partial Regex AppliedMigration();

    [Theory]
    [InlineData("WHERE retires_at > now()")]
    [InlineData("AND due_at <= CURRENT_TIMESTAMP")]
    [InlineData("effective_from <= current_date")]
    [InlineData("VALUES (clock_timestamp())")]
    public void TheRule_FindsTheDatabasesClock(string sql) => DatabaseClock().IsMatch(sql).ShouldBeTrue();

    /// <summary>The database's clock in SQL text: now(), CURRENT_TIMESTAMP, current_date and the like (not DateTime.Now, which is C#).</summary>
    [GeneratedRegex(@"(?<![\w.])(now\s*\(\s*\)|current_timestamp|current_date|current_time|localtimestamp|localtime|clock_timestamp\s*\(|statement_timestamp\s*\(|transaction_timestamp\s*\()",
        RegexOptions.IgnoreCase)]
    private static partial Regex DatabaseClock();
}
