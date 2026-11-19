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
using Insys.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>
/// OQ-185 (Ade, 19 November 2026), V0144: an approved static data row's status moves only forward, on every static data table, as one trigger guards them
/// all. Every move from every locked status is tried directly in the database, on one table of each lifecycle, and the guard's answer compared with the
/// lifecycle written out here. The moves INSYS makes through its own paths (activation, deprecation, retirement, imports and Product Factory's lifecycle)
/// are the same list, and their own tests run against V0144 too.
/// </summary>
public sealed class StaticStatusGuardTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    // The specification's two lifecycles (§4), forward moves from each locked status. A status not listed for a from-status is refused.
    private static readonly Dictionary<string, string[]> Reference = new()
    {
        ["APPROVED"] = ["ACTIVE"],
        ["ACTIVE"] = ["DEPRECATED", "RETIRED"],
        ["DEPRECATED"] = ["RETIRED"],
        ["RETIRED"] = [],
    };

    private static readonly Dictionary<string, string[]> Configuration = new()
    {
        ["APPROVED"] = ["PUBLISHED"],
        ["PUBLISHED"] = ["EFFECTIVE", "WITHDRAWN", "RETIRED"],
        ["EFFECTIVE"] = ["WITHDRAWN", "RETIRED"],
        ["WITHDRAWN"] = ["RETIRED"],
        ["RETIRED"] = [],
    };

    private static readonly string[] ReferenceStatuses = ["DRAFT", "PENDING_APPROVAL", "APPROVED", "ACTIVE", "DEPRECATED", "RETIRED"];
    private static readonly string[] ConfigurationStatuses = ["DRAFT", "VALIDATED", "SUBMITTED", "APPROVED", "PUBLISHED", "EFFECTIVE", "WITHDRAWN", "RETIRED"];

    [Fact]
    public async Task AnApprovedReferenceRow_MovesOnlyForward_AndABackwardMoveIsRefused_OQ185() =>
        (await Mismatches("workflow.ref_appeal_routes", ReferenceStatuses, Reference, (code, status) =>
            $"INSERT INTO workflow.ref_appeal_routes (tenant_id, code, version, effective_from, record_status, created_by, route_code, name, preferred_lines) "
            + $"VALUES (NULL, '{code}', 1, DATE '2020-01-01', '{status}', 'test', 'GT', 'Guard test', ARRAY['LIFE'])"))
        .ShouldBeEmpty();

    [Fact]
    public async Task AnApprovedConfigurationRow_MovesOnlyForward_AndABackwardMoveIsRefused_OQ185() =>
        (await Mismatches("tenant.cfg_feature_entitlements", ConfigurationStatuses, Configuration, (code, status) =>
            $"INSERT INTO tenant.cfg_feature_entitlements (tenant_id, code, version, effective_from, record_status, created_by, tenant, feature, enabled) "
            + $"VALUES (NULL, '{code}', 1, DATE '2020-01-01', '{status}', 'test', 'GUARD', 'LIFE', true)"))
        .ShouldBeEmpty();

    [Fact]
    public async Task APublishedProductVersion_CantBeSetBackToDraft_TheGapOQ185Found()
    {
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO tenant.cfg_feature_entitlements (tenant_id, code, version, effective_from, record_status, created_by, tenant, feature, enabled)
            VALUES (NULL, 'GUARD-BACK', 1, DATE '2020-01-01', 'EFFECTIVE', 'test', 'GUARD', 'LIFE', true)
            """);
        var refused = await Should.ThrowAsync<PostgresException>(() =>
            fixture.Database.ExecuteAsOwner("UPDATE tenant.cfg_feature_entitlements SET record_status = 'DRAFT' WHERE tenant_id IS NULL AND code = 'GUARD-BACK'"));
        refused.MessageText.ShouldContain("moves only forward, not from EFFECTIVE to DRAFT");

        // Unchanged status: effective_to still closes on a locked row, as a new version supersedes it.
        await fixture.Database.ExecuteAsOwner("UPDATE tenant.cfg_feature_entitlements SET effective_to = DATE '2030-01-01' WHERE tenant_id IS NULL AND code = 'GUARD-BACK'");

        // The deliberate correction, with the trigger off for its connection, still works.
        await fixture.Database.CorrectAsOwnerPastTheGuard("UPDATE tenant.cfg_feature_entitlements SET record_status = 'DRAFT' WHERE tenant_id IS NULL AND code = 'GUARD-BACK'");
        (await fixture.Database.ScalarAsOwner<string>("SELECT record_status FROM tenant.cfg_feature_entitlements WHERE tenant_id IS NULL AND code = 'GUARD-BACK'"))
            .ShouldBe("DRAFT");
    }

    /// <summary>Each locked from-status to each other status the table allows, on a fresh row: where the guard's answer differs from the lifecycle.</summary>
    private async Task<List<string>> Mismatches(string table, string[] statuses, Dictionary<string, string[]> forward, Func<string, string, string> insert)
    {
        var mismatches = new List<string>();
        var n = 0;
        foreach (var (from, allowed) in forward)
        {
            foreach (var to in statuses.Where(s => s != from))
            {
                var code = $"GUARD-{table.Replace('.', '-')}-{++n}".ToUpperInvariant();
                await fixture.Database.ExecuteAsOwner(insert(code, from));
                string outcome;
                try
                {
                    await fixture.Database.ExecuteAsOwner($"UPDATE {table} SET record_status = @to WHERE tenant_id IS NULL AND code = @code", ("to", to), ("code", code));
                    outcome = "allowed";
                }
                catch (PostgresException e) when (e.MessageText.Contains("moves only forward", StringComparison.Ordinal))
                {
                    outcome = "refused";
                }

                var expected = allowed.Contains(to) ? "allowed" : "refused";
                if (outcome != expected)
                {
                    mismatches.Add($"{from} -> {to}: {outcome}, expected {expected}");
                }
            }
        }

        return mismatches;
    }
}
