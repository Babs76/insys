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
using Insys.Testing;

namespace Insys.ArchitectureTests;

/// <summary>
/// Every source file we own carries the OpenFactor header with the right component (Phase 8, Workstream C). Add missing
/// headers with: python3 tools/source-headers.py --apply
/// </summary>
public sealed class SourceHeaderTests
{
    [Fact]
    public void EveryInScopeFile_HasTheHeader_WithItsComponent()
    {
        var files = SourceHeader.InScopeFiles();
        files.Count.ShouldBeGreaterThan(900);
        files.Where(f => !SourceHeader.HasCorrectHeader(f)).Select(f => $"{f} (COMPONENT {SourceHeader.ComponentOf(f)})").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("src/Modules/Life/Insys.Life.Domain/Policies/LifePolicy.cs", "Life")]
    [InlineData("src/BuildingBlocks/Insys.BuildingBlocks.Infrastructure/StaticData/StaticRecord.cs", "BuildingBlocks.Infrastructure")]
    [InlineData("tools/Insys.StaticData.Generator/Program.cs", "StaticData.Generator")]
    [InlineData("db/migrations/V0001__core_schema.sql", "Migrations")]
    [InlineData(".github/workflows/ci.yml", "CI")]
    [InlineData("tests/Insys.Life.Tests/LifeTests.cs", "Life.Tests")]
    [InlineData("Directory.Build.props", "Build")]
    public void Component_FollowsTheFilesLocation(string path, string component) => SourceHeader.ComponentOf(path).ShouldBe(component);

    [Fact]
    public void JsonMarkdownAndOtherDocuments_AreOutOfScope()
    {
        foreach (var path in new[] { "docs/api/openapi-core.json", "README.md", "global.json", "db/roles/module_roles.csv", "docs/specs/html/presentation.html" })
        {
            SourceHeader.StyleOf(path).ShouldBeNull(path);
        }
    }
}
