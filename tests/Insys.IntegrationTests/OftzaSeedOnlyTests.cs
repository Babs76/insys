/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Workstream F seed
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
using Insys.Seed;
using Insys.Testing;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>
/// The seed's --only flag (batch 6, Ade 20 November 2026): the deployment smoke test runs --only administrators, and --only personas is the next stop.
/// Its own database, so the whole seed's test doesn't see a part-seeded tenant.
/// </summary>
public sealed class OftzaSeedOnlyTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task OnlyAdministrators_StopsOnceBothHaveSignedInAndRead_ThenOnlyPersonas_StopsBeforeProducts()
    {
        var (seed, admin) = await OftzaSeedTests.Start(fixture, only: "administrators", accounts: false);
        async Task<long> Count(string sql) => await fixture.Database.ScalarAsOwner<long>(sql, ("t", seed.TenantId));

        (await Count("SELECT count(*) FROM iam.user_account WHERE tenant_id = @t")).ShouldBe(2L, "the two first administrators and nobody else");
        (await Count("SELECT count(*) FROM product.cfg_product_definitions_and_versions WHERE tenant_id = @t")).ShouldBe(0L);
        (await admin.Send(HttpMethod.Get, "/api/v1/identity/me")).GetProperty("tenant").GetProperty("tenantCode").GetString().ShouldBe(OftzaSeed.TenantCode);

        // Run again with --only personas: it carries on from there (the seed is idempotent) and stops before products and business.
        var (again, _) = await OftzaSeedTests.Start(fixture, only: "personas", accounts: false);
        again.TenantId.ShouldBe(seed.TenantId);
        (await Count("SELECT count(*) FROM iam.user_account WHERE tenant_id = @t")).ShouldBeGreaterThan(2L, "the personas");
        (await Count("SELECT count(*) FROM product.cfg_product_definitions_and_versions WHERE tenant_id = @t")).ShouldBe(0L);
        (await Count("SELECT count(*) FROM life.policy WHERE tenant_id = @t")).ShouldBe(0L);
    }
}
