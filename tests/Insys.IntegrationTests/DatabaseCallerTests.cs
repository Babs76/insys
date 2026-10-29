/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : IntegrationTests
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

namespace Insys.IntegrationTests;

/// <summary>
/// Every call to one of our database functions, from any SQL file outside the applied migrations (bootstrap, roles,
/// repeatable scripts) and from the application's C#, names a function that exists with that many arguments. A function's
/// signature changed by a migration (as V0093 changed audit.ensure_monthly_partitions) fails here at every caller left behind,
/// not only where a test happens to run it.
/// </summary>
public sealed partial class DatabaseCallerTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task EveryCallToOurFunctions_MatchesAFunctionInForce_V0093()
    {
        var schemas = (await fixture.Database.ScalarAsOwner<string>("""
            SELECT string_agg(nspname, ',') FROM pg_namespace
            WHERE nspname NOT IN ('pg_catalog', 'information_schema', 'public', 'pg_toast', 'test_mod') AND nspname NOT LIKE 'pg\_%'
            """))!.Split(',').ToHashSet(StringComparer.Ordinal);
        var functions = (await fixture.Database.ScalarAsOwner<string>("""
            SELECT string_agg(n.nspname || '.' || p.proname || ':' || (p.pronargs - p.pronargdefaults) || ':' || p.pronargs, ',')
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            """))!.Split(',').Select(f => f.Split(':')).GroupBy(f => f[0], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(f => (Min: int.Parse(f[1]), Max: int.Parse(f[2]))).ToList(), StringComparer.Ordinal);
        var relations = (await fixture.Database.ScalarAsOwner<string>("""
            SELECT string_agg(n.nspname || '.' || c.relname, ',') FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            """))!.Split(',').ToHashSet(StringComparer.Ordinal);

        var root = RepositoryPaths.Root;
        var files = Directory.EnumerateFiles(Path.Combine(root, "db"), "*.sql", SearchOption.AllDirectories)
            .Where(f => !Regex.IsMatch(Path.GetFileName(f), @"^V\d{4}__"))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)));
        var problems = new List<string>();
        var checkedCalls = 0;
        foreach (var file in files)
        {
            var text = string.Join('\n', File.ReadLines(file).Select(l => Regex.Replace(l, @"--.*$|^\s*//.*$", "")));
            foreach (Match call in Call().Matches(text))
            {
                var (schema, name) = (call.Groups["schema"].Value, call.Groups["name"].Value);
                var qualified = $"{schema}.{name}";
                if (!schemas.Contains(schema) || relations.Contains(qualified))
                {
                    continue;
                }

                var where = $"{Path.GetRelativePath(root, file)}: {qualified}";
                if (!functions.TryGetValue(qualified, out var overloads))
                {
                    problems.Add($"{where} does not exist");
                    continue;
                }

                checkedCalls++;
                var arguments = Arguments(text, call.Index + call.Length - 1);
                if (!overloads.Any(o => arguments >= o.Min && arguments <= o.Max))
                {
                    problems.Add($"{where} is called with {arguments} arguments; it takes {string.Join(" or ", overloads.Select(o => o.Min == o.Max ? $"{o.Max}" : $"{o.Min} to {o.Max}"))}");
                }
            }
        }

        checkedCalls.ShouldBeGreaterThan(5, "the scan finds our functions' callers");
        problems.ShouldBeEmpty();
    }

    /// <summary>The top-level arguments between the parenthesis at <paramref name="open"/> and its match (an ARRAY[...] is one).</summary>
    private static int Arguments(string text, int open)
    {
        var depth = 0;
        var commas = 0;
        var any = false;
        var quoted = false;
        for (var i = open + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'')
            {
                quoted = !quoted;
            }

            if (quoted)
            {
                any = true;
                continue;
            }

            if (c is '(' or '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
            }
            else if (c == ')')
            {
                if (depth == 0)
                {
                    return any ? commas + 1 : 0;
                }

                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                commas++;
            }

            any |= !char.IsWhiteSpace(c);
        }

        return any ? commas + 1 : 0;
    }

    [GeneratedRegex(@"(?<![\w.{])(?<schema>[a-z][a-z_]*)\.(?<name>[a-z_][a-z0-9_]*)\s*\(")]
    private static partial Regex Call();
}
