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
using System.Net;
using Npgsql;
using Insys.Testing;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>Front-end request (18 November 2026): the General claims list filters by a party on the claim, as the policies list does by policyholder.</summary>
public sealed class GeneralClaimsListTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static readonly Guid T = DatabaseFixture.TenantA;

    [Fact]
    public async Task TheClaimsList_FiltersByAPartyOnTheClaim_InAnyRoleOrTheOneGiven()
    {
        var factory = await fixture.Host();
        var rows = new RowFiller(fixture.Database, DateTimeOffset.UtcNow);
        var suffix = Random.Shared.Next(100_000, 999_999);
        var policy = await rows.Insert(T, "general.policy", ("policy_number", $"GCL{suffix}"), ("status", "IN_FORCE"));
        var party = Guid.CreateVersion7();
        var asClaimant = await rows.Insert(T, "general.claim", ("policy_id", policy), ("claim_number", $"GC-A{suffix}"), ("status", "REGISTERED"), ("coverage_code", "OWN_DAMAGE"));
        var asWitness = await rows.Insert(T, "general.claim", ("policy_id", policy), ("claim_number", $"GC-B{suffix}"), ("status", "REGISTERED"), ("coverage_code", "OWN_DAMAGE"));
        var notTheirs = await rows.Insert(T, "general.claim", ("policy_id", policy), ("claim_number", $"GC-C{suffix}"), ("status", "REGISTERED"), ("coverage_code", "OWN_DAMAGE"));
        await rows.Insert(T, "general.claim_party", ("claim_id", asClaimant), ("party_id", party), ("role", "CLAIMANT"));
        await rows.Insert(T, "general.claim_party", ("claim_id", asWitness), ("party_id", party), ("role", "WITNESS"));
        await rows.Insert(T, "general.claim_party", ("claim_id", notTheirs), ("party_id", Guid.CreateVersion7()), ("role", "CLAIMANT"));
        await fixture.Database.UserWithPermissions(T, "gcl-reader", Insys.General.Contracts.GeneralPermissions.ClaimRead);
        var client = factory.ClientFor(T, "gcl-reader");

        async Task<List<Guid>> Ids(string query)
        {
            var response = await client.GetAsync($"/api/v1/general/claims?{query}");
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var page = await response.Json();
            DocumentedSchema.ResponseProblems(page, "core", "GeneralListClaims").ShouldBeEmpty();
            return page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        }

        (await Ids($"partyId={party}")).ShouldBe([asClaimant, asWitness], ignoreOrder: true, "the party in any role");
        (await Ids($"partyId={party}&partyRole=CLAIMANT")).ShouldBe([asClaimant], "or in the role given");
        (await Ids($"partyId={party}&policyId={policy}&limit=1")).Count.ShouldBe(1, "with the other filters and paging");

        var orphan = await client.GetAsync("/api/v1/general/claims?partyRole=CLAIMANT");
        orphan.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await orphan.Code()).ShouldBe("VALIDATION_FAILED");
    }

    [Fact]
    public async Task TheFilterByParty_IsServedByAnIndexOnTheClaimsParties_V0143()
    {
        // With sequential scans off, the planner takes the cheapest index; only (tenant_id, party_id) avoids reading every claim party of the tenant.
        await using var connection = new NpgsqlConnection(fixture.Database.OwnerConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var off = new NpgsqlCommand("SET LOCAL enable_seqscan = off", connection, transaction))
        {
            await off.ExecuteNonQueryAsync();
        }

        await using var explain = new NpgsqlCommand(
            "EXPLAIN SELECT claim_id FROM general.claim_party WHERE tenant_id = @t AND party_id = @p", connection, transaction);
        explain.Parameters.AddWithValue("t", T);
        explain.Parameters.AddWithValue("p", Guid.NewGuid());
        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.Add(reader.GetString(0));
            }
        }

        string.Join("\n", plan).ShouldContain("ix_claim_party_by_party");
    }
}
