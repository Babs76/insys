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
using Insys.Testing;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>
/// tenant.tenant, the platform's directory of tenants, has no row-level security by design (Ade's question, 18 November 2026): only the owner and the
/// Tenancy module's role may read it, and the API's reads of tenants are the platform's only. These tests hold both.
/// </summary>
public sealed class TenantDirectoryAccessTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task OnlyTheTenancyModulesRole_CanReadTheTenantDirectory_TEN004()
    {
        var readers = await fixture.Database.ScalarAsOwner<string>("""
            SELECT string_agg(rolname, ',' ORDER BY rolname) FROM pg_roles
            WHERE rolname LIKE 'insys\_%' AND has_table_privilege(rolname, 'tenant.tenant', 'SELECT') AND NOT rolsuper
              AND rolname NOT IN (SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'tenant.tenant'::regclass)
            """);
        readers.ShouldNotBeNull();
        readers.Split(',').Where(r => !r.EndsWith("_login", StringComparison.Ordinal)).ShouldBe(["insys_tenant"],
            "only the Tenancy module's role (and its login) reads the directory");
    }

    [Fact]
    public async Task ABusinessTenantsUser_HoldingTenantRead_ReadsNoTenant_NotEvenAnother_TEN004()
    {
        var factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "tenant-reader-a", Insys.Tenancy.Contracts.TenancyPermissions.Read);
        var client = factory.ClientFor(DatabaseFixture.TenantA, "tenant-reader-a");
        (await client.GetAsync("/api/v1/tenancy/tenants")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.GetAsync($"/api/v1/tenancy/tenants/{DatabaseFixture.TenantB}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
