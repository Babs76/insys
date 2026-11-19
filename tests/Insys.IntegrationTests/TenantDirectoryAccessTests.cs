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
        readers.Split(',').Where(r => !r.EndsWith("_login", StringComparison.Ordinal)).ShouldBe(["insys_tenant", "insys_tenant_directory"],
            "only the Tenancy module's role (and its login), and the platform's directory role it may take (V0142), read the directory");
    }

    /// <summary>V0142: the Tenancy module's role sees its own tenant's row in a tenant's session, every row in the operator's, and none with no tenant set.</summary>
    [Fact]
    public async Task ATenantsSession_SeesOnlyItsOwnRow_ThePlatformOperatorsSeesEvery_AndNoneSetSeesNone_V0142()
    {
        async Task<string> Visible(Guid? session, bool directory = false)
        {
            await using var connection = new NpgsqlConnection(fixture.Database.ConnectionFor("insys_tenant"));
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            if (directory)
            {
                await using var role = new NpgsqlCommand("SET LOCAL ROLE insys_tenant_directory", connection, transaction);
                await role.ExecuteNonQueryAsync();
            }

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
        (await Visible(null, directory: true)).Split(',').ShouldBe(["OPERATOR", "TENA", "TENB"], ignoreOrder: true,
            "the platform's directory role sees every row, with no operator row needed");
    }

    /// <summary>
    /// V0142: the security-definer check has a fixed search path and qualified names (Ade), so a look-alike current_setting that a session puts first in
    /// its path, answering with the operator's id, changes neither the check's answer nor the rows the session sees.
    /// </summary>
    [Fact]
    public async Task ALookAlikeInAnotherSchema_CantMakeATenantsSessionThePlatformOperators_V0142()
    {
        await fixture.Database.ExecuteAsOwner($"""
            CREATE SCHEMA IF NOT EXISTS lookalike;
            CREATE OR REPLACE FUNCTION lookalike.current_setting(text, boolean) RETURNS text LANGUAGE sql AS $$ SELECT '{DatabaseFixture.Operator}'::text $$;
            GRANT USAGE ON SCHEMA lookalike TO PUBLIC;
            GRANT EXECUTE ON FUNCTION lookalike.current_setting(text, boolean) TO PUBLIC;
            """);
        await using var connection = new NpgsqlConnection(fixture.Database.ConnectionFor("insys_tenant"));
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true); SET LOCAL search_path = lookalike, pg_catalog, tenant", connection, transaction))
        {
            set.Parameters.AddWithValue("t", DatabaseFixture.TenantA.ToString());
            await set.ExecuteNonQueryAsync();
        }

        await using (var probe = new NpgsqlCommand("SELECT current_setting('app.tenant_id', true)", connection, transaction))
        {
            ((string)(await probe.ExecuteScalarAsync())!).ShouldBe(DatabaseFixture.Operator.ToString(), "the look-alike is first in the session's own path");
        }

        await using (var check = new NpgsqlCommand("SELECT tenant.session_is_platform_operator()", connection, transaction))
        {
            ((bool)(await check.ExecuteScalarAsync())!).ShouldBeFalse("the check reads pg_catalog's current_setting, whatever the caller's path");
        }

        await using var read = new NpgsqlCommand("SELECT string_agg(tenant_code, ',') FROM tenant.tenant", connection, transaction);
        ((string)(await read.ExecuteScalarAsync())!).ShouldBe("TENA");
    }

    /// <summary>The one route that reads tenants' parked deliveries across tenants (an operator-session directory read) needs a platform permission.</summary>
    [Fact]
    public async Task ABusinessTenantsUser_CantReachTheCrossTenantParkedDeliveryCounts_V0142()
    {
        var factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "dl-reader-a", Insys.Integration.Contracts.IntegrationPermissions.DeadLetterRead);
        (await factory.ClientFor(DatabaseFixture.TenantA, "dl-reader-a").GetAsync("/api/v1/integration/parked-deliveries/by-tenant")).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>V0142: the directory role is the Tenancy module's alone: another module's role can't take it.</summary>
    [Fact]
    public async Task AnotherModulesRole_CantTakeTheDirectoryRole_V0142()
    {
        await using var connection = new NpgsqlConnection(fixture.Database.ConnectionFor("insys_party"));
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var role = new NpgsqlCommand("SET LOCAL ROLE insys_tenant_directory", connection, transaction);
        (await Should.ThrowAsync<PostgresException>(() => role.ExecuteNonQueryAsync())).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
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
