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
using Insys.Identity.Contracts;
using Insys.Party.Contracts;
using Insys.Testing;
using Insys.Workflow.Contracts;

namespace Insys.IntegrationTests;

/// <summary>
/// External-user access §1: an external user's link to the party they are takes effect only once a second person approves
/// it, and stops at the next request once ended. Linked, the user is external: only their portal's endpoints, through their
/// portal's channel.
/// </summary>
public sealed class ExternalUserLinkTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid Tenant = DatabaseFixture.TenantA;
    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync() => _factory = await fixture.Host();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task ALink_TakesEffectOnASecondPersonsApproval_AndEndsAtOnce_ExternalUserAccess()
    {
        var db = fixture.Database;
        string[] administration = [IdentityPermissions.UserRead, IdentityPermissions.UserUpdate, PartyPermissions.PartyCreate, WorkflowPermissions.ApprovalRead,
            WorkflowPermissions.ApprovalApprove];
        await db.UserWithPermissions(Tenant, "link-admin", administration);
        var checker = await db.UserWithPermissions(Tenant, "link-checker", administration);
        var authority = await db.RoleWithPermissions(Tenant);
        await db.AssignRole(Tenant, checker, authority);
        await db.ExecuteAsOwner("""
            INSERT INTO workflow.cfg_delegation_and_approval_authority (tenant_id, code, version, effective_from, record_status, created_by, change_reason,
                                                                         role_code, transaction_type, limit_value, delegation_allowed)
            VALUES (@t, 'AUTH-LINK', 1, current_date - 1, 'EFFECTIVE', 'seed', 'test seed', @r, @type, 0, false)
            """, ("t", Tenant), ("r", authority), ("type", IdentityApprovals.SubjectLinkApprovalType));
        var admin = _factory.ClientFor(Tenant, "link-admin");
        var party = await admin.RegisterPerson("Linked");
        var user = await db.UserWithPermissions(Tenant, "portal-person");
        var person = _factory.ClientFor(Tenant, "portal-person", "member-portal");

        async Task<HttpResponseMessage> Link(object body) =>
            await admin.Send(HttpMethod.Post, $"/api/v1/identity/users/{user}/subject-link", body, ifMatch: (await admin.GetAsync($"/api/v1/identity/users/{user}")).ETag());

        (await (await Link(new { kind = "Party", partyId = Guid.CreateVersion7() })).Code()).ShouldBe("LINK_SUBJECT_NOT_FOUND");
        (await Link(new { kind = "Party", partyId = party })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await Link(new { kind = "Party", partyId = party })).Code()).ShouldBe("SUBJECT_LINK_EXISTS");
        var link = await (await admin.GetAsync($"/api/v1/identity/users/{user}/subject-link")).Json();
        link.GetProperty("status").GetString().ShouldBe("Pending");

        // Pending, it gives nothing: the user is not yet external.
        (await (await person.GetAsync("/api/v1/identity/me")).Json()).GetProperty("external").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);

        // A second person approves it; the one who asked cannot.
        await _factory.DrainOutbox();
        var approval = (await (await admin.GetAsync($"/api/v1/workflow/approvals?status=Requested&businessRefType={IdentityApprovals.SubjectLinkBusinessRefType}"
            + $"&businessRefId={link.GetProperty("id").GetGuid()}")).Json()).GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        (await (await admin.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        (await _factory.ClientFor(Tenant, "link-checker").Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();

        // Linked, the user is external: only through the member portal's channel.
        (await (await person.GetAsync("/api/v1/identity/me")).Code()).ShouldBe("CHANNEL_NOT_ENABLED");
        await db.SeedStatic(Tenant, "channel.cfg_channel_definitions", "EFFECTIVE", "CH-MEM", TestClock.Today.AddDays(-1), ("channel_code", "MEM"),
            ("audience", "CUSTOMER"), ("name", "Member portal"), ("client_ids", new[] { "member-portal" }));
        var me = await (await person.GetAsync("/api/v1/identity/me")).Json();
        (me.GetProperty("external").GetProperty("kind").GetString(), me.GetProperty("external").GetProperty("partyId").GetGuid()).ShouldBe(("Party", party));
        (await person.GetAsync($"/api/v1/identity/users/{user}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Ended, it stops at the next request.
        (await admin.Send(HttpMethod.Post, $"/api/v1/identity/users/{user}/subject-link/end", ifMatch: (await admin.GetAsync($"/api/v1/identity/users/{user}")).ETag()))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        (await (await person.GetAsync("/api/v1/identity/me")).Json()).GetProperty("external").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
    }
}
