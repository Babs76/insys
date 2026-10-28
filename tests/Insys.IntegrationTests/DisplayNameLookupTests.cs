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

namespace Insys.IntegrationTests;

/// <summary>
/// BR-021 (wave2-3-proposals.md §3, decided by Ade on 28 October 2026): any signed-in staff user, with no function or
/// permission, turns up to 100 user IDs of their own tenant into display names, a disabled user's included; a platform
/// operator's ID reads "Platform operator"; another tenant's user and an unknown ID are left out; a portal user cannot call it.
/// </summary>
public sealed class DisplayNameLookupTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string Lookup = "/api/v1/identity/users/display-names";
    private static readonly Guid A = DatabaseFixture.TenantA;
    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync() => _factory = await fixture.Host();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task StaffResolveTheirTenantsUsers_AndAnOperatorAsALabel_AndNothingElse_BR021()
    {
        await fixture.Database.UserWithPermissions(A, "names-caller");
        var colleague = await fixture.Database.UserWithPermissions(A, "Thandi Nkosi");
        var disabled = await fixture.Database.UserWithPermissions(A, "Former Clerk");
        await fixture.Database.ExecuteAsOwner("UPDATE iam.user_account SET status = 'DISABLED' WHERE id = @u", ("u", disabled));
        var elsewhere = await fixture.Database.UserWithPermissions(DatabaseFixture.TenantB, "Other Tenant User");
        var operatorUser = await fixture.Database.UserWithPermissions(DatabaseFixture.Operator, "Ops Person");
        var unknown = Guid.CreateVersion7();

        var caller = _factory.ClientFor(A, "names-caller");
        var answered = await caller.Send(HttpMethod.Post, Lookup, new { ids = new[] { colleague, disabled, elsewhere, operatorUser, unknown, colleague } });
        answered.StatusCode.ShouldBe(HttpStatusCode.OK, await answered.Content.ReadAsStringAsync());
        var names = (await answered.Json()).EnumerateArray().ToDictionary(n => n.GetProperty("id").GetGuid(), n => n.GetProperty("displayName").GetString());
        names.ShouldBe(new Dictionary<Guid, string?>
        {
            [colleague] = "Thandi Nkosi", [disabled] = "Former Clerk", [operatorUser] = "Platform operator",
        }, ignoreOrder: true);
        (await answered.Content.ReadAsStringAsync()).ShouldNotContain("Ops Person", customMessage: "never an operator's personal name");

        (await caller.Send(HttpMethod.Post, Lookup, new { ids = Enumerable.Range(0, 101).Select(_ => Guid.CreateVersion7()) })).StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity, "at most 100 IDs");
        (await caller.Send(HttpMethod.Post, Lookup, new { ids = Array.Empty<Guid>() })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task APortalUser_CannotResolveStaffNames_BR021()
    {
        await fixture.Database.SeedStatic(A, "channel.cfg_channel_definitions", "EFFECTIVE", "CH-MEM21", new DateOnly(2026, 1, 1), ("channel_code", "MEM21"),
            ("audience", "CUSTOMER"), ("name", "Member portal"), ("client_ids", new[] { "member-portal" }));
        var member = await fixture.Database.UserWithPermissions(A, "names-member");
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO iam.user_subject_link (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, user_id, kind, party_id, intermediary_code,
                                               provider_id, status, requested_by, decided_by, decided_at)
            VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @u, 'PARTY', gen_random_uuid(), NULL, NULL, 'ACTIVE', gen_random_uuid(), gen_random_uuid(), now())
            """, ("t", A), ("u", member));
        var staff = await fixture.Database.UserWithPermissions(A, "Named Staff");

        var refused = await _factory.ClientFor(A, "names-member", "member-portal").Send(HttpMethod.Post, Lookup, new { ids = new[] { staff } });
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Code()).ShouldBe("EXTERNAL_USER_NOT_ALLOWED");
    }
}
