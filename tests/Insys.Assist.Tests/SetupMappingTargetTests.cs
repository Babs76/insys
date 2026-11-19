/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist.Tests
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
using System.Text.Json;
using System.Text.RegularExpressions;
using Insys.Assist.Application;
using Insys.Testing;
using Shouldly;
using Xunit;

namespace Insys.Assist.Tests;

/// <summary>
/// Batch 7.8, Ade 19 November 2026: the columns AI-3's checker writes and checks for its three mapped tables (OQ-186 (a)) are pinned to the specification. The
/// three are static data tables, so their fields are in docs/specs/static-data-catalogue.json (docs/specs/database-tables.json holds the core tables only), and
/// each column must also be one the table's generated import takes (StaticDataFields, generated from the migrations).
/// </summary>
public sealed partial class SetupMappingTargetTests
{
    /// <summary>The catalogue names a field by its business name where the column differs: V0002 made the limit's column limit_value.</summary>
    private static readonly Dictionary<string, string> CatalogueName = new(StringComparer.Ordinal) { ["limit_value"] = "limit" };

    private static Dictionary<string, JsonElement> Fields(string table)
    {
        using var catalogue = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docs", "specs", "static-data-catalogue.json")));
        return catalogue.RootElement.GetProperty("entities").EnumerateArray().Single(e => e.GetProperty("table").GetString() == table)
            .GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!, f => f.Clone(), StringComparer.Ordinal);
    }

    public static TheoryData<string> Tables => [SetupMappingTargets.ChartOfAccounts, SetupMappingTargets.ApprovalAuthority, SetupMappingTargets.Roles];

    [Theory]
    [MemberData(nameof(Tables))]
    public void EveryColumnTheCheckerWritesAndChecks_IsInTheSpecification_WithACompatibleTypeAndRequiredness_OQ186(string table)
    {
        var target = SetupMappingTargets.For(["CLAIMS_HANDLER"]).Single(t => t.Table == table);
        var fields = Fields(table);
        var imported = Insys.BuildingBlocks.Application.StaticDataFields.Of[table];
        foreach (var column in target.Columns)
        {
            if (table == SetupMappingTargets.Roles && column.Name == "title")
            {
                // A role's title is written as its English label, a column every static table has (labels jsonb), never as a field of its own.
                imported.ShouldNotContain("title");
                continue;
            }

            imported.ShouldContain(Camel(column.Name), $"{table}.{column.Name} is a column the import takes");
            fields.TryGetValue(CatalogueName.GetValueOrDefault(column.Name, column.Name), out var field).ShouldBeTrue($"{table}.{column.Name} is in the catalogue");
            var type = field.GetProperty("logicalType").GetString()!;
            if (field.GetProperty("mandatory").GetBoolean())
            {
                column.Required.ShouldBeTrue($"{table}.{column.Name} is mandatory, so the checker requires it");
            }

            switch (column.Kind)
            {
                case MappedColumnKind.Amount:
                    type.ShouldBe("Money");
                    break;
                case MappedColumnKind.Currency:
                    type.ShouldStartWith("Ref Currency");
                    break;
                case MappedColumnKind.Flag:
                    type.ShouldBe("Boolean");
                    break;
                case MappedColumnKind.Copied:
                    Length(type).ShouldNotBeNull($"{table}.{column.Name} is text of a declared length");
                    column.MaxLength.ShouldNotBeNull().ShouldBeLessThanOrEqualTo(Length(type)!.Value);
                    break;
                case MappedColumnKind.InsysCode when type.StartsWith("Enum{", StringComparison.Ordinal):
                    var values = type[5..^1].Split(',').Select(v => v.Trim().ToUpperInvariant()).ToList();
                    column.Allowed.ShouldNotBeNull().ShouldAllBe(a => values.Contains(a), $"{table}.{column.Name}'s allowed codes are the catalogue's");
                    break;
                case MappedColumnKind.InsysCode:
                    (type.StartsWith("Code(", StringComparison.Ordinal) || type.StartsWith("Ref ", StringComparison.Ordinal)).ShouldBeTrue($"{table}.{column.Name} is a code: {type}");
                    if (Length(type) is { } length)
                    {
                        column.MaxLength.ShouldNotBeNull().ShouldBeLessThanOrEqualTo(length);
                    }

                    break;
            }
        }

        // And the other way round: every mandatory field, apart from the retired permissions, is one the checker writes.
        foreach (var (name, field) in fields.Where(f => f.Value.GetProperty("mandatory").GetBoolean()))
        {
            target.Columns.ShouldContain(c => CatalogueName.GetValueOrDefault(c.Name, c.Name) == name, $"{table}.{name} is mandatory");
        }
    }

    private static int? Length(string type) => LengthOf().Match(type) is { Success: true } m ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;

    private static string Camel(string snake) => JsonNamingPolicy.CamelCase.ConvertName(string.Concat(snake.Split('_').Select((p, i) => i == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..])));

    [GeneratedRegex(@"^(?:String|Code)\((\d+)\)$")]
    private static partial Regex LengthOf();
}
