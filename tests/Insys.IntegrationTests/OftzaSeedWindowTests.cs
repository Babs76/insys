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

/// <summary>Past the API's idempotency window the keys return nothing, and the search finds the parties an earlier run made.</summary>
public sealed class OftzaSeedWindowTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private sealed class Interrupted() : Exception("Stopped part-way, for the test.");

    [Fact]
    public async Task PastTheIdempotencyWindow_ARerunFindsPartiesByIdentifierThenName_WithNoDuplicates()
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

        // Stopped first with a party holding its identifier, then with one that has none yet.
        var stops = new Queue<string>(["/identifiers", "party/parties"]);
        void Stop(string call)
        {
            if (stops.Count > 0 && call.StartsWith("POST ", StringComparison.Ordinal) && call.EndsWith(stops.Peek(), StringComparison.Ordinal))
            {
                stops.Dequeue();
                throw new Interrupted();
            }
        }

        OftzaSeed Seed() => new(new SeedContext(new Persona(host.ClientFor(DatabaseFixture.Operator, "seed-platform-1"), Stop), new Persona(host.ClientFor(DatabaseFixture.Operator, "seed-platform-2"), Stop),
            (tenant, subject) => new Persona(host.ClientFor(tenant, subject, "insys-web"), Stop), () => host.DrainOutbox()));

        var seed = Seed();
        await Should.ThrowAsync<Interrupted>(seed.Run);
        await db.ExecuteAsOwner("DELETE FROM integration.idempotency_key");
        await Should.ThrowAsync<Interrupted>(Seed().Run);
        await db.ExecuteAsOwner("DELETE FROM integration.idempotency_key");
        await Seed().Run();

        var t = ("t", (object)seed.TenantId);
        (await db.ScalarAsOwner<string>("SELECT status FROM tenant.tenant WHERE id = @t", t)).ShouldBe("ACTIVE");
        (await db.ScalarAsOwner<long>("SELECT count(*) FROM party.party WHERE tenant_id = @t", t)).ShouldBe(25L, "no party twice");
    }
}
