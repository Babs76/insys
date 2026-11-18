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

/// <summary>
/// tenant.tenant, the platform's directory of tenants (Ade, 18 November 2026): only the owner and the Tenancy module's role may read it, the API's reads of
/// tenants are the platform's only, and row-level security (V0142) lets a tenant's session see only its own row, and the platform operator's every row.
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

    /// <summary>V0142: the Tenancy module's role sees its own tenant's row in a tenant's session, every row in the operator's, and none with no tenant set.</summary>
    [Fact]
    public async Task ATenantsSession_SeesOnlyItsOwnRow_ThePlatformOperatorsSeesEvery_AndNoneSetSeesNone_V0142()
    {
        async Task<string> Visible(Guid? session)
        {
            await using var connection = new NpgsqlConnection(fixture.Database.ConnectionFor("insys_tenant"));
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            if (session is { } tenant)
            {
                await using var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", connection, transaction);
                set.Parameters.AddWithValue("t", tenant.ToString());
                await set.ExecuteNonQueryAsync();
            }

            await using var read = new NpgsqlCommand("SELECT coalesce(string_agg(tenant_code, ',' ORDER BY tenant_code), '') FROM tenant.tenant", connection, transaction);
            return (string)(await read.ExecuteScalarAsync())!;
        }

        (await Visible(DatabaseFixture.TenantA)).ShouldBe("TENA", "a tenant's session sees its own row only");
        (await Visible(DatabaseFixture.TenantB)).ShouldBe("TENB");
        (await Visible(null)).ShouldBe("", "no tenant set is never the platform");
        (await Visible(DatabaseFixture.Operator)).Split(',').ShouldBe(["OPERATOR", "TENA", "TENB"], ignoreOrder: true, "the platform operator's session sees every row");
    }

    /// <summary>V0142: only the owner marks the platform operator, so a tenant's session can't raise itself to see the others.</summary>
    [Fact]
    public async Task ATenantsSession_CantMarkItselfThePlatformOperator_V0142()
    {
        await using var connection = new NpgsqlConnection(fixture.Database.ConnectionFor("insys_tenant"));
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("t", DatabaseFixture.TenantA.ToString());
            await set.ExecuteNonQueryAsync();
        }

        await using var raise = new NpgsqlCommand("UPDATE tenant.tenant SET platform_operator = true WHERE id = @t", connection, transaction);
        raise.Parameters.AddWithValue("t", DatabaseFixture.TenantA);
        (await Should.ThrowAsync<PostgresException>(() => raise.ExecuteNonQueryAsync())).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
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
