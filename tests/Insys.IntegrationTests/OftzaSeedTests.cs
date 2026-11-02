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
using Insys.Seed;
using Insys.Testing;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>Workstream F: the sample tenant, through the governed API alone.</summary>
public sealed class OftzaSeedTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private async Task<(OftzaSeed Seed, Persona Admin)> Run()
    {
        var db = fixture.Database;
        var host = await fixture.Host();
        string[] platform = ["tenant.read", "tenant.create", "tenant.update", "user.read", "user.create", "approval.read", "approval.approve", "static_data.read", "static_data.create", "static_data.update", "static_data.approve"];
        await db.UserWithPermissions(DatabaseFixture.Operator, "seed-platform-1", platform);
        var second = await db.UserWithPermissions(DatabaseFixture.Operator, "seed-platform-2", platform);
        var role = await db.RoleWithPermissions(DatabaseFixture.Operator);
        await db.AssignRole(DatabaseFixture.Operator, second, role);
        await db.SeedStatic(DatabaseFixture.Operator, "workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", "AUTH-SEED-ADM", new DateOnly(2026, 1, 1), ("role_code", role),
            ("transaction_type", "TENANT_ADMINISTRATOR"), ("limit_value", 1m), ("delegation_allowed", false));
        var seed = new OftzaSeed(new SeedContext(new Persona(host.ClientFor(DatabaseFixture.Operator, "seed-platform-1")), new Persona(host.ClientFor(DatabaseFixture.Operator, "seed-platform-2")),
            (tenant, subject) => new Persona(host.ClientFor(tenant, subject)), () => host.DrainOutbox()));
        await seed.Run();
        return (seed, new Persona(host.ClientFor(seed.TenantId, OftzaSeed.FirstAdministrator)));
    }

    [Fact]
    public async Task TheSeed_MakesASampleTenant_WithTwoAdministrators_AndARunAgainChangesNothing()
    {
        var (seed, _) = await Run();
        (await fixture.Database.ScalarAsOwner<bool>("SELECT sample FROM tenant.tenant WHERE id = @t", ("t", seed.TenantId))).ShouldBeTrue();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM tenant.cfg_locale_defaults WHERE tenant_id = @t", ("t", seed.TenantId))).ShouldBe(1L);

        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_account WHERE tenant_id = @t AND username LIKE 'oftza-%'", ("t", seed.TenantId))).ShouldBeGreaterThan(15L, "a persona per role template");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_role_assignment WHERE tenant_id = @t AND role_code = 'TENANT_ADMIN'", ("t", seed.TenantId))).ShouldBe(2L);
        var users = await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_account WHERE tenant_id = @t", ("t", seed.TenantId));
        var again = await Run();
        again.Seed.TenantId.ShouldBe(seed.TenantId);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_account WHERE tenant_id = @t", ("t", seed.TenantId))).ShouldBe(users);
    }
}
