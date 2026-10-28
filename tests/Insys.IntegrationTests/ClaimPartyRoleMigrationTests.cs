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
using Insys.DbMigrator;

namespace Insys.IntegrationTests;


/// <summary>
/// OQ-122, migration V0075: a General claim party's role comes from the tenant's claim party roles; each tenant that
/// exists when the table arrives gets the starter list as drafts, for its claims team to confirm.
/// </summary>
public sealed class ClaimPartyRoleMigrationTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task ExistingTenants_GetTheStarterRolesAsDrafts_OQ122()
    {
        var connection = await fixture.Server.CreateEmptyDatabase();
        var directory = Path.Combine(Path.GetTempPath(), $"insys-v0075-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(RepositoryPaths.Root, "db", "migrations");
        foreach (var file in Directory.GetFiles(source, "V*.sql").Where(f => string.CompareOrdinal(Path.GetFileName(f), "V0075") < 0))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        }

        var database = new TestDatabase("v0075", connection, connection, await new MigrationRunner(connection).Migrate(directory));
        var tenant = Guid.NewGuid();
        await database.ExecuteAsOwner("""
            INSERT INTO tenant.tenant (id, tenant_code, legal_name, isolation_profile, status, created_at)
            VALUES (@t, 'V0075', 'V0075 tenant', 'S', 'ACTIVE', now())
            """, ("t", tenant));
        File.Copy(Directory.GetFiles(source, "V0075__*.sql").Single(), Path.Combine(directory, "V0075__claim_party_roles.sql"));
        await new MigrationRunner(connection).Migrate(directory);

        (await database.ScalarAsOwner<string>("""
            SELECT string_agg(code || ':' || record_status, ',' ORDER BY sort_order) FROM general.cfg_claim_party_roles WHERE tenant_id = @t
            """, ("t", tenant))).ShouldBe("CLAIMANT:DRAFT,INSURED:DRAFT,THIRD_PARTY:DRAFT,DRIVER:DRAFT,WITNESS:DRAFT,ASSESSOR:DRAFT,REPAIRER:DRAFT,LEGAL_REP:DRAFT,PAYEE:DRAFT");
    }
}
