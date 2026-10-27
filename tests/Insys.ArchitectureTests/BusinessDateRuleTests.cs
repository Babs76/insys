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
/// Business dates come from the business clock (IBusinessDate, TenantBusinessDates), in the tenant's time zone (ADR-025,
/// GAP-044, front-end request BR-040). No production code takes a date from the UTC clock outside it: not IClock.UtcToday,
/// not DateOnly.FromDateTime of a UTC instant, not DateTime.Today, not SQL CURRENT_DATE. A technical date that is UTC by
/// design (audit partitions, the time zone lookup itself) says so on its line with "technical UTC date:" and a reason.
/// </summary>
public sealed partial class BusinessDateRuleTests
{
    /// <summary>The business clock itself, where the UTC instant becomes the tenant's date.</summary>
    private static readonly string[] BusinessClock =
    [
        "src/BuildingBlocks/Insys.BuildingBlocks.Application/Context/BusinessDate.cs",
        "src/BuildingBlocks/Insys.BuildingBlocks.Application/Context/IClock.cs",
    ];

    [Fact]
    public void NoProductionCode_TakesABusinessDateFromTheUtcClock_BR040()
    {
        var root = RepositoryPaths.Root;
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.EndsWith(".g.cs", StringComparison.Ordinal))
            .Select(f => (Full: f, Relative: Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/')))
            .Where(f => !BusinessClock.Contains(f.Relative))
            .ToList();
        files.Count.ShouldBeGreaterThan(500);

        var found = new List<string>();
        foreach (var (full, relative) in files)
        {
            var lines = File.ReadAllLines(full);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('*') || line.Contains("technical UTC date:", StringComparison.Ordinal))
                {
                    continue;
                }

                if (UtcDate().IsMatch(line))
                {
                    found.Add($"{relative}:{i + 1}: {line}");
                }
            }
        }

        found.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("var today = clock.UtcToday;")]
    [InlineData("if (eventDate > DateOnly.FromDateTime(now.UtcDateTime))")]
    [InlineData("command.Parameters.AddWithValue(\"day\", DateOnly.FromDateTime(DateTime.UtcNow));")]
    [InlineData("var day = DateTime.UtcNow.Date;")]
    [InlineData("var day = DateTime.Today;")]
    [InlineData("WHERE effective_from <= CURRENT_DATE")]
    [InlineData("AND valid_to > now()::date")]
    public void TheRule_FindsEachFormOfAUtcDate(string line) => UtcDate().IsMatch(line).ShouldBeTrue();

    [Theory]
    [InlineData("var today = businessDate.Today;")]
    [InlineData("var day = dates.TodayFor(tenantId);")]
    [InlineData("var date = businessDate.DateOf(claim.NotifiedAt);")]
    [InlineData("var now = clock.UtcNow;")]
    public void TheRule_LeavesBusinessDatesAndInstantsAlone(string line) => UtcDate().IsMatch(line).ShouldBeFalse();

    [GeneratedRegex(@"\.UtcToday\b|DateOnly\.FromDateTime\([^;]*(UtcNow|UtcDateTime|DateTime\.Now)|\bUtcNow\.Date\b|\bDateTime\.(Now|UtcNow)\.Date\b|\bDateTime\.Today\b|\bCURRENT_DATE\b|\bcurrent_date\b|now\(\)::date",
        RegexOptions.CultureInvariant)]
    private static partial Regex UtcDate();
}
