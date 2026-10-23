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
using Insys.BuildingBlocks.Api;
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// GAP-029: every target type an audit entry names in the source is one the documents enumerate, so a front end can
/// rely on the list. Literal targets (nameof(X) or "X") are checked; computed ones are static data entity codes.
/// </summary>
public sealed partial class AuditTargetTypeTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task EveryLiteralAuditTarget_IsAnEnumeratedTargetType_GAP029()
    {
        await fixture.Host(); // Maps every module, which registers the static data entity codes and other target types.
        var known = AuditTargetTypes.All().ToHashSet(StringComparer.Ordinal);

        var used = Directory.EnumerateFiles(Path.Combine(RepositoryPaths.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => Target().Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Name: (m.Groups["nameof"].Success ? m.Groups["nameof"].Value.Split('.')[^1] : m.Groups["literal"].Value))))
            .ToList();

        used.ShouldNotBeEmpty();
        used.Where(u => !known.Contains(u.Name)).Select(u => $"{u.File}: {u.Name}").Distinct().ShouldBeEmpty();
    }

    [GeneratedRegex("""new AuditEntry\(\s*"[A-Z_]+",\s*[^,]+?,\s*(?:nameof\((?<nameof>[A-Za-z_.]+)\)|"(?<literal>[A-Za-z_]+)")""")]
    private static partial Regex Target();
}
