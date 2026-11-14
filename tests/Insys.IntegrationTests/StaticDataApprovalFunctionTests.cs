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
using Insys.BuildingBlocks.Api.Functions;
using Insys.BuildingBlocks.Api.StaticData;
using Insys.Identity.Api;
using Insys.StaticData.Contracts;
using Insys.Testing;
using Insys.Workflow.Contracts;

namespace Insys.IntegrationTests;

/// <summary>
/// ADR-026, SRS-002, WFL-004 (Ade, 14 November 2026): a static data change approval is decided only by a holder of the
/// table's own approve function (SD_{catalogue module}_APPROVE), on the generic route POST /workflow/approvals/{id}/approve
/// (the console's Inbox) as on the table's own route. Workflow approval and authority for the type are not enough. The
/// change here is a role (Identity and access management, Tier 1): the tenant administrator holds its approve function,
/// a line configuration manager, with the same Tier 1 authority, does not. Each test has a tenant of its own.
/// </summary>
public sealed class StaticDataApprovalFunctionTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private const string Roles = "/api/v1/identity/static/roles-and-permission-sets";
    private const string Approvals = "/api/v1/workflow/approvals";

    private static readonly string[] MakerPermissions =
    [
        StaticDataPermissions.Read, StaticDataPermissions.Create, StaticDataPermissions.Update, StaticDataPermissions.Approve,
        StaticDataGovernancePermissions.ChangeRequestRead, WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove,
    ];

    [Fact]
    public async Task ACheckerWithoutTheTablesApproveFunction_IsRefusedOnTheGenericRoute_DespiteWorkflowApproveAndAuthority_ADR026_SRS002_WFL004()
    {
        var w = await World();
        var change = await Submit(w, "NO_FUNCTION");
        var approval = await Waiting(w, change);

        var approved = await w.LineManager.Send(HttpMethod.Post, $"{Approvals}/{approval}/approve", ifMatch: "\"1\"");
        approved.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await approved.Content.ReadAsStringAsync());
        (await approved.Code()).ShouldBe("FUNCTION_NOT_GRANTED");
        var rejected = await w.LineManager.Send(HttpMethod.Post, $"{Approvals}/{approval}/reject", new { reason = "Not mine to decide" }, ifMatch: "\"1\"");
        (await rejected.Code()).ShouldBe("FUNCTION_NOT_GRANTED");

        // Nor is it offered to them to decide (BR-004), while the tenant administrator is offered it.
        (await Decidable(w.LineManager)).ShouldNotContain(approval);
        (await Decidable(w.TenantAdmin)).ShouldContain(approval);

        await w.Factory.DrainOutbox();
        (await w.Db.ScalarAsOwner<string>("SELECT status FROM workflow.approval WHERE id = @a", ("a", approval))).ShouldBe("REQUESTED");
        (await Status(w, "NO_FUNCTION")).ShouldBe("SUBMITTED");
    }

    [Fact]
    public async Task TheTenantAdministrator_ApprovesATableChangeOfTheirArea_FromTheInboxRoute_ADR026_SRS002()
    {
        var w = await World();
        var change = await Submit(w, "INBOX_APPROVED");
        var approval = await Waiting(w, change);

        var approved = await w.TenantAdmin.Send(HttpMethod.Post, $"{Approvals}/{approval}/approve", ifMatch: "\"1\"");
        approved.StatusCode.ShouldBe(HttpStatusCode.NoContent, await approved.Content.ReadAsStringAsync());
        await w.Factory.DrainOutbox();
        (await Status(w, "INBOX_APPROVED")).ShouldBe("APPROVED");
    }

    [Fact]
    public async Task TheMaker_IsRefusedOnBothRoutes_SRS002_SDM003()
    {
        var w = await World();
        var change = await Submit(w, "OWN_CHANGE");
        var approval = await Waiting(w, change);

        var generic = await w.Maker.Send(HttpMethod.Post, $"{Approvals}/{approval}/approve", ifMatch: "\"1\"");
        generic.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await generic.Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        var table = await w.Maker.Send(HttpMethod.Post, $"{Roles}/OWN_CHANGE/versions/1/approve");
        table.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await table.Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");

        await w.Factory.DrainOutbox();
        (await Status(w, "OWN_CHANGE")).ShouldBe("SUBMITTED");
    }

    [Fact]
    public async Task TheTablesOwnRoute_StillNeedsTheTablesApproveFunction_AndApprovesWithIt_ADR026_SDM003()
    {
        var w = await World();
        await Submit(w, "TABLE_ROUTE");

        var refused = await w.LineManager.Send(HttpMethod.Post, $"{Roles}/TABLE_ROUTE/versions/1/approve");
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Code()).ShouldBe("FUNCTION_NOT_GRANTED");

        var approved = await w.TenantAdmin.Send(HttpMethod.Post, $"{Roles}/TABLE_ROUTE/versions/1/approve");
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());
        await w.Factory.DrainOutbox();
        (await Status(w, "TABLE_ROUTE")).ShouldBe("APPROVED");
    }

    private sealed record FunctionWorld(InsysApiFactory Factory, TestDatabase Db, Guid Tenant, HttpClient Maker, HttpClient TenantAdmin, HttpClient LineManager);

    /// <summary>A maker, a tenant administrator and a line configuration manager, each with Tier 1 and Tier 2 authority (WFL-004).</summary>
    private async Task<FunctionWorld> World()
    {
        var factory = await fixture.Host();
        var db = fixture.Database;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenant = Guid.CreateVersion7();
        await db.SeedTenant(tenant, "SF" + suffix.ToUpperInvariant());
        var catalogue = FunctionRegistry.All();
        string[] Template(string code) => [.. RoleTemplates.Expand(RoleTemplates.All.Single(t => t.Code == code), catalogue).Select(f => f.Code)];

        var adminFunctions = Template("TENANT_ADMIN");
        var lineFunctions = Template("LINE_CONFIGURATION_MANAGER");
        adminFunctions.ShouldContain("SD_IDENTITY_AND_ACCESS_MANAGEMENT_APPROVE");
        adminFunctions.ShouldContain("WORKFLOW_APPROVE");
        lineFunctions.ShouldContain("WORKFLOW_APPROVE");
        lineFunctions.ShouldNotContain("SD_IDENTITY_AND_ACCESS_MANAGEMENT_APPROVE");

        var maker = await db.UserWithPermissions(tenant, $"maker-{suffix}", MakerPermissions);
        var admin = await db.UserWithFunctions(tenant, $"admin-{suffix}", adminFunctions);
        var line = await db.UserWithFunctions(tenant, $"line-{suffix}", lineFunctions);
        var approver = await db.RoleWithPermissions(tenant);
        foreach (var user in new[] { maker, admin, line })
        {
            await db.AssignRole(tenant, user, approver);
        }

        foreach (var type in new[] { "STATIC_DATA_T1", "STATIC_DATA_T2" })
        {
            await db.SeedStatic(tenant, "workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", $"AUTH-{type}-{suffix}", TestClock.Today.AddYears(-1),
                ("role_code", approver), ("transaction_type", type), ("limit_value", 100_000_000m), ("delegation_allowed", false));
        }

        return new(factory, db, tenant, factory.ClientFor(tenant, $"maker-{suffix}"), factory.ClientFor(tenant, $"admin-{suffix}"),
            factory.ClientFor(tenant, $"line-{suffix}"));
    }

    private static async Task<Guid> Submit(FunctionWorld w, string code)
    {
        var created = await w.Maker.Send(HttpMethod.Post, Roles, new { code, roleCode = code, scopeProducts = new[] { "LIFE-TERM" }, changeReason = "Test role" },
            idempotent: true);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var submitted = await w.Maker.Send(HttpMethod.Post, $"{Roles}/{code}/versions/1/submit", ifMatch: created.ETag());
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK, await submitted.Content.ReadAsStringAsync());
        await w.Factory.DrainOutbox();
        return await w.Db.ScalarAsOwner<Guid>("SELECT id FROM sdm.change_request WHERE tenant_id = @t AND record_key = @k AND status = 'PENDING_APPROVAL'",
            ("t", w.Tenant), ("k", $"{code}@1"));
    }

    private static Task<Guid> Waiting(FunctionWorld w, Guid change) =>
        w.Db.ScalarAsOwner<Guid>("SELECT id FROM workflow.approval WHERE tenant_id = @t AND business_ref_type = @r AND business_ref_id = @c AND status = 'REQUESTED'",
            ("t", w.Tenant), ("r", StaticDataApprovals.BusinessRefType), ("c", change));

    private static async Task<string?> Status(FunctionWorld w, string code) =>
        (await (await w.Maker.GetAsync($"{Roles}/{code}/versions")).Json())[0].GetProperty("recordStatus").GetString();

    private static async Task<List<Guid>> Decidable(HttpClient client)
    {
        var page = await client.GetAsync($"{Approvals}?decidable=true&businessRefType={StaticDataApprovals.BusinessRefType}");
        page.StatusCode.ShouldBe(HttpStatusCode.OK, await page.Content.ReadAsStringAsync());
        return [.. (await page.Json()).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];
    }
}
