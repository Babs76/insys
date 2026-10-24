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
using Insys.ClaimsFramework.Contracts;
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// F103 (addenda/access-scopes.md §1): a registered claim's Claims Framework records are scoped as its domain claim, by
/// the product and organisation node recorded at registration.
/// </summary>
public sealed class ClaimsScopeTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync() => _factory = await fixture.Host();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task RegisteredClaim_IsReadOnlyWithinTheProductAndNodeItWasRegisteredWith_F103()
    {
        var db = fixture.Database;
        var motor = await Registered("GEN-MOTOR", "BR-A");
        var home = await Registered("GEN-HOME", "BR-A");
        var user = await db.UserWithPermissions(DatabaseFixture.TenantA, "claims-motor-only", ClaimsPermissions.ClaimRead, ClaimsPermissions.FinancialsRead,
            ClaimsPermissions.InstructionRead);
        await db.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_products = ARRAY['GEN-MOTOR'] WHERE user_id = @u", ("u", user));
        var client = _factory.ClientFor(DatabaseFixture.TenantA, "claims-motor-only");

        (await client.GetAsync($"/api/v1/claims/claims/{motor}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        foreach (var path in new[] { "", "/financials", "/payment-instructions" })
        {
            (await client.GetAsync($"/api/v1/claims/claims/{home}{path}")).StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{path}: outside the scope, not revealed");
        }
    }

    private async Task<string> Registered(string product, string node)
    {
        var number = $"SCP-{Guid.CreateVersion7():N}"[..20];
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO claims.claim_register (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, claim_number, domain, domain_claim_id,
                                               policy_ref, registered_at, product_code, org_node_code)
            VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @n, 'GENERAL', gen_random_uuid(), gen_random_uuid(), now(), @p, @o)
            """, ("t", DatabaseFixture.TenantA), ("n", number), ("p", product), ("o", node));
        return number;
    }
}
