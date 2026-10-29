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
