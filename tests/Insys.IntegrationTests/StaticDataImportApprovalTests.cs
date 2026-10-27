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
using Insys.BuildingBlocks.Api.StaticData;
using Insys.StaticData.Contracts;
using Insys.Testing;
using Insys.Workflow.Contracts;

namespace Insys.IntegrationTests;

/// <summary>
/// BR-016 (signed off 27 October 2026): a static data import is approved as one change. It is approved or rejected as a
/// whole, never partly; the checker sees every line; the approval is bound to a hash of the complete import, so any change
/// to a line needs a new approval. Each test has a tenant of its own with a maker and a checker holding static data authority.
/// </summary>
public sealed class StaticDataImportApprovalTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private const string Roles = "/api/v1/identity/static/roles-and-permission-sets";
    private const string Schedules = "/api/v1/identity/static/access-review-schedules";

    private static readonly string[] Permissions =
    [
        StaticDataPermissions.Read, StaticDataPermissions.Create, StaticDataPermissions.Update, StaticDataPermissions.Approve,
        StaticDataGovernancePermissions.ChangeRequestRead, WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove,
    ];

    [Fact]
    public async Task AHundredLineImport_IsApprovedWithOneDecision_TheCheckerSeeingEveryLine_AndActivatedTogether_BR016()
    {
        var w = await World();
        var batch = await Import(w, Roles, Enumerable.Range(1, 100).Select(i => Role($"IMP_{i:D3}")));

        // A line is not submitted alone.
        var line = (await Lines(w.Maker, Roles, batch)).EnumerateArray().First();
        (await (await w.Maker.Send(HttpMethod.Post, $"{Roles}/{line.GetProperty("code").GetString()}/versions/1/submit",
            ifMatch: $"\"{line.GetProperty("rowVersion").GetInt64()}\"")).Code()).ShouldBe("INVALID_STATE_TRANSITION");

        var submitted = await w.Maker.Send(HttpMethod.Post, $"{Roles}/imports/{batch}/submit");
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK, await submitted.Content.ReadAsStringAsync());
        var change = (await submitted.Json()).GetProperty("changeRequestId").GetGuid();
        await w.Factory.DrainOutbox();

        // One change request and one approval for the hundred lines; the checker reads every line.
        (await w.Db.ScalarAsOwner<string>("SELECT action || ' ' || status FROM sdm.change_request WHERE id = @c", ("c", change))).ShouldBe("IMPORT PENDING_APPROVAL");
        (await w.Db.ScalarAsOwner<long>("SELECT count(*) FROM workflow.approval WHERE tenant_id = @t", ("t", w.Tenant))).ShouldBe(1L);
        var lines = await Lines(w.Checker, Roles, batch);
        lines.GetArrayLength().ShouldBe(100);
        lines.EnumerateArray().ShouldAllBe(l => l.GetProperty("recordStatus").GetString() == "SUBMITTED" && l.GetProperty("changeRequestId").GetGuid() == change);

        var approval = await Waiting(w, change);
        (await w.Maker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode.ShouldNotBe(HttpStatusCode.NoContent);
        (await w.Checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await w.Factory.DrainOutbox();
        (await Statuses(w, Roles, batch)).ShouldBe(["APPROVED"]);

        var activated = await w.Maker.Send(HttpMethod.Post, $"{Roles}/imports/{batch}/activate");
        activated.StatusCode.ShouldBe(HttpStatusCode.OK, await activated.Content.ReadAsStringAsync());
        (await activated.Json()).GetProperty("imported").GetInt32().ShouldBe(100);
        (await Statuses(w, Roles, batch)).ShouldBe(["PUBLISHED"]);
        await w.Factory.DrainOutbox();
        (await w.Db.ScalarAsOwner<string>("SELECT status FROM sdm.change_request WHERE id = @c", ("c", change))).ShouldBe("ACTIVE");
    }

    [Fact]
    public async Task ARejectedImport_ReturnsEveryLineToDraft_AndOnceCorrected_IsApprovedAsANewChange_BR016()
    {
        var w = await World();
        var batch = await Import(w, Roles, [Role("REJ_A"), Role("REJ_B"), Role("REJ_C")]);
        var first = (await (await w.Maker.Send(HttpMethod.Post, $"{Roles}/imports/{batch}/submit")).Json()).GetProperty("changeRequestId").GetGuid();
        await w.Factory.DrainOutbox();
        (await w.Checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{await Waiting(w, first)}/reject", new { reason = "REJ_B is too broad" }, ifMatch: "\"1\""))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await w.Factory.DrainOutbox();
        (await Statuses(w, Roles, batch)).ShouldBe(["DRAFT"], "rejected as a whole, never partly");

        var line = (await Lines(w.Maker, Roles, batch)).EnumerateArray().Single(l => l.GetProperty("code").GetString() == "REJ_B");
        (await w.Maker.Send(HttpMethod.Put, $"{Roles}/REJ_B/versions/1", new { code = "REJ_B", roleCode = "REJ_B", scopeProducts = new[] { "GEN-HOME" }, changeReason = "Narrowed" },
            ifMatch: $"\"{line.GetProperty("rowVersion").GetInt64()}\"")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var second = await w.Maker.Send(HttpMethod.Post, $"{Roles}/imports/{batch}/submit");
        second.StatusCode.ShouldBe(HttpStatusCode.OK, await second.Content.ReadAsStringAsync());
        var change = (await second.Json()).GetProperty("changeRequestId").GetGuid();
        change.ShouldNotBe(first, "a corrected import needs a new approval");
        await w.Factory.DrainOutbox();
        (await w.Checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{await Waiting(w, change)}/approve", ifMatch: "\"1\"")).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        await w.Factory.DrainOutbox();
        (await Statuses(w, Roles, batch)).ShouldBe(["APPROVED"]);
    }

    /// <summary>The approval is bound to the hash of the complete import: a line changed after submission is not approved, nor is any other (D-5).</summary>
    [Fact]
    public async Task AnImportWhoseLineChangedAfterSubmission_IsNotApplied_AndNoLineIsApproved_BR016()
    {
        var w = await World();
        var batch = await Import(w, Roles, [Role("HASH_A"), Role("HASH_B")]);
        var change = (await (await w.Maker.Send(HttpMethod.Post, $"{Roles}/imports/{batch}/submit")).Json()).GetProperty("changeRequestId").GetGuid();
        await w.Factory.DrainOutbox();

        // No API changes a submitted line; an owner update stands for a change made underneath the approval.
        await w.Db.ExecuteAsOwner("UPDATE iam.cfg_roles_and_permission_sets SET scope_products = ARRAY['GEN-MOTOR'] WHERE tenant_id = @t AND code = 'HASH_B'",
            ("t", w.Tenant));
        var approval = await Waiting(w, change);
        (await w.Checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await w.Factory.DrainOutbox();

        (await Statuses(w, Roles, batch)).ShouldBe(["SUBMITTED"], "nothing is applied");
        (await w.Db.ScalarAsOwner<long>("""
            SELECT count(*) FROM integration.parked_delivery
            WHERE tenant_id = @t AND prerequisite_code = 'APPROVAL_NOT_APPLIED' AND prerequisite_item = @item
            """, ("t", w.Tenant), ("item", $"STATIC_DATA_IMPORT:{change}"))).ShouldBeGreaterThan(0L);
    }

    /// <summary>Tier 2 with the tenant's maker-checker switched off: the import is approved on submission, as a single draft would be (SDM-003).</summary>
    [Fact]
    public async Task ATier2Import_WithMakerCheckerOff_IsApprovedOnSubmission_BR016()
    {
        var w = await World();
        await w.Db.ExecuteAsOwner("""
            INSERT INTO sdm.tenant_governance_setting (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, tier2_maker_checker)
            VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', false)
            """, ("t", w.Tenant));
        var batch = await Import(w, Schedules, new[] { "S1", "S2" }.Select(code => (object)new { code, scope = "PRIVILEGED", frequency = "P3M", reviewerRole = "AUDITOR",
            changeReason = "Schedules" }));

        (await w.Maker.Send(HttpMethod.Post, $"{Schedules}/imports/{batch}/submit")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await w.Factory.DrainOutbox();
        await w.Factory.DrainOutbox();

        (await Statuses(w, Schedules, batch)).ShouldBe(["APPROVED"]);
        (await w.Db.ScalarAsOwner<long>("SELECT count(*) FROM workflow.approval WHERE tenant_id = @t", ("t", w.Tenant))).ShouldBe(0L);
    }

    private sealed record ImportWorld(InsysApiFactory Factory, TestDatabase Db, Guid Tenant, HttpClient Maker, HttpClient Checker);

    private async Task<ImportWorld> World()
    {
        var factory = await fixture.Host();
        var db = fixture.Database;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenant = Guid.CreateVersion7();
        await db.SeedTenant(tenant, "IM" + suffix.ToUpperInvariant());
        var maker = await db.UserWithPermissions(tenant, $"maker-{suffix}", Permissions);
        var checker = await db.UserWithPermissions(tenant, $"checker-{suffix}", Permissions);
        await db.AssignRole(tenant, maker, await db.RoleWithPermissions(tenant));
        var approver = await db.RoleWithPermissions(tenant);
        await db.AssignRole(tenant, checker, approver);
        foreach (var type in new[] { "STATIC_DATA_T1", "STATIC_DATA_T2" })
        {
            await db.SeedStatic(tenant, "workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", $"AUTH-{type}-{suffix}", TestClock.Today.AddYears(-1),
                ("role_code", approver), ("transaction_type", type), ("limit_value", 100_000_000m), ("delegation_allowed", false));
        }

        return new(factory, db, tenant, factory.ClientFor(tenant, $"maker-{suffix}"), factory.ClientFor(tenant, $"checker-{suffix}"));
    }

    private static async Task<Guid> Import(ImportWorld w, string url, IEnumerable<object> lines)
    {
        var imported = await w.Maker.Send(HttpMethod.Post, $"{url}/imports", new { fileRef = "import.csv", lines = lines.ToArray() }, idempotent: true);
        imported.StatusCode.ShouldBe(HttpStatusCode.OK, await imported.Content.ReadAsStringAsync());
        await w.Factory.DrainOutbox();
        return (await imported.Json()).GetProperty("batchId").GetGuid();
    }

    private static async Task<string[]> Statuses(ImportWorld w, string url, Guid batch) =>
        [.. (await Lines(w.Maker, url, batch)).EnumerateArray().Select(l => l.GetProperty("recordStatus").GetString()!).Distinct()];

    private static async Task<System.Text.Json.JsonElement> Lines(HttpClient client, string url, Guid batch)
    {
        var read = await client.GetAsync($"{url}/imports/{batch}");
        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync());
        return await read.Json();
    }

    private static Task<Guid> Waiting(ImportWorld w, Guid change) =>
        w.Db.ScalarAsOwner<Guid>("SELECT id FROM workflow.approval WHERE tenant_id = @t AND business_ref_type = @r AND business_ref_id = @c AND status = 'REQUESTED'",
            ("t", w.Tenant), ("r", StaticDataApprovals.BusinessRefType), ("c", change));

    private static object Role(string code) => new { code, roleCode = code, scopeProducts = new[] { "LIFE-TERM" }, changeReason = "Imported role" };
}
