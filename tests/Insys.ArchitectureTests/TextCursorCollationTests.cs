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
/// BR-087: a list paged by a text key compares and orders that key bytewise in the database (COLLATE "C"), so the cursor,
/// the database's order and any ordinal sort in memory agree. Under the database's own collation (en_US) underscores are
/// ignored, and a code on a page boundary was skipped. A text comparison in a query must name the collation.
/// </summary>
public sealed partial class TextCursorCollationTests
{
    [Fact]
    public void ATextKeyCursor_IsComparedBytewise_InTheDatabase_BR087()
    {
        var root = RepositoryPaths.Root;
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        var comparisons = 0;
        var found = new List<string>();
        foreach (var file in files)
        {
            foreach (var line in File.ReadLines(file).Where(l => QueryCompare().IsMatch(l)))
            {
                comparisons++;
                if (!line.Contains("EF.Functions.Collate(", StringComparison.Ordinal))
                {
                    found.Add($"{Path.GetRelativePath(root, file)}: {line.Trim()}");
                }
            }
        }

        comparisons.ShouldBeGreaterThanOrEqualTo(2, "the static data lists and the product list");
        found.ShouldBeEmpty();
    }

    /// <summary>A text comparison inside a query predicate: string.Compare, or CompareTo on a string, in a Where.</summary>
    [GeneratedRegex(@"\.Where\(.*(?:string\.Compare\(|\b\w+\.(?:Code|Name|Key|Number|PeriodCode)\.CompareTo\()")]
    private static partial Regex QueryCompare();
}
