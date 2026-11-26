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
using Insys.Workflow.Contracts;
using System.Net;
using System.Text.Json;
using Insys.BuildingBlocks.Api.StaticData;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.StaticData.Contracts;
using Insys.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.IntegrationTests;

/// <summary>
/// Static data through the generated endpoints: maker-checker through sdm.change_request (item 7, SDM-003), the
/// lifecycle, imports (SDM-012), tenant overlays (SDM-008), and global rows written only by platform administrators
/// on their own connection (item 6).
/// </summary>
public sealed class StaticDataTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string Roles = "/api/v1/identity/static/roles-and-permission-sets";
    private const string Policies = "/api/v1/identity/static/authentication-and-session-policies";
    private const string Permissions = "/api/v1/identity/static/permission-catalogue";
    private static readonly string[] All =
        [StaticDataPermissions.Read, StaticDataPermissions.Create, StaticDataPermissions.Update, StaticDataPermissions.Approve, StaticDataGovernancePermissions.ChangeRequestRead];

    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "maker-a", [.. All, WorkflowPermissions.OwnTaskRead]); // OQ-204: told in my tasks
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "checker-a", All);
        await fixture.Database.UserWithPermissions(DatabaseFixture.Operator, "op-maker", All);
        await fixture.Database.UserWithPermissions(DatabaseFixture.Operator, "op-checker", All);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Tier1Change_NeedsADifferentChecker_ThenActivates_SDM003()
    {
        var maker = _factory.ClientFor(DatabaseFixture.TenantA, "maker-a");
        var checker = _factory.ClientFor(DatabaseFixture.TenantA, "checker-a");
        var created = await maker.Send(HttpMethod.Post, Roles, Role("CLAIMS_CLERK"), idempotent: true);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var draft = await created.Json();
        draft.GetProperty("recordStatus").GetString().ShouldBe("DRAFT");
        draft.GetProperty("tenantId").GetGuid().ShouldBe(DatabaseFixture.TenantA);

        var submitted = await maker.Send(HttpMethod.Post, $"{Roles}/CLAIMS_CLERK/versions/1/submit", ifMatch: created.ETag());
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await submitted.Json()).GetProperty("recordStatus").GetString().ShouldBe("SUBMITTED");
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM sdm.change_request WHERE record_key = 'CLAIMS_CLERK@1' AND tenant_id = @t",
            ("t", DatabaseFixture.TenantA))).ShouldBe("PENDING_APPROVAL");

        var selfApproval = await maker.Send(HttpMethod.Post, $"{Roles}/CLAIMS_CLERK/versions/1/approve");
        selfApproval.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await selfApproval.Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");

        (await checker.Send(HttpMethod.Post, $"{Roles}/CLAIMS_CLERK/versions/1/approve")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var versions = await (await maker.GetAsync($"{Roles}/CLAIMS_CLERK/versions")).Json();
        var approved = versions[0];
        approved.GetProperty("recordStatus").GetString().ShouldBe("APPROVED");
        approved.GetProperty("approvedBy").GetString().ShouldNotBeNull();

        var activated = await maker.Send(HttpMethod.Post, $"{Roles}/CLAIMS_CLERK/versions/1/activate",
            ifMatch: $"\"{approved.GetProperty("rowVersion").GetInt64()}\"");
        activated.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await activated.Json()).GetProperty("recordStatus").GetString().ShouldBe("PUBLISHED");

        var inForce = await maker.GetAsync($"{Roles}/CLAIMS_CLERK");
        inForce.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await inForce.Json()).GetProperty("scopeProducts")[0].GetString().ShouldBe("LIFE-TERM");

        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM sdm.change_request WHERE record_key = 'CLAIMS_CLERK@1' AND tenant_id = @t",
            ("t", DatabaseFixture.TenantA))).ShouldBe("ACTIVE");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM sdm.change_request WHERE record_key = 'CLAIMS_CLERK@1' AND checker_id IS NOT NULL AND checker_id <> maker_id"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task RejectedChange_ReturnsToDraft_AndOnlyTheMakerEditsIt_SDM003()
    {
        var maker = _factory.ClientFor(DatabaseFixture.TenantA, "maker-a");
        var checker = _factory.ClientFor(DatabaseFixture.TenantA, "checker-a");
        var created = await maker.Send(HttpMethod.Post, Roles, Role("REJECTED_ROLE"), idempotent: true);

        var edit = await checker.Send(HttpMethod.Put, $"{Roles}/REJECTED_ROLE/versions/1", Role("REJECTED_ROLE"), ifMatch: created.ETag());
        edit.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await edit.Code()).ShouldBe("STATIC_MAKER_ONLY");

        var submitted = await maker.Send(HttpMethod.Post, $"{Roles}/REJECTED_ROLE/versions/1/submit", ifMatch: created.ETag());
        await _factory.DrainOutbox();
        (await checker.Send(HttpMethod.Post, $"{Roles}/REJECTED_ROLE/versions/1/reject", new { reason = "Too broad" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();

        var version = (await (await maker.GetAsync($"{Roles}/REJECTED_ROLE/versions")).Json())[0];
        version.GetProperty("recordStatus").GetString().ShouldBe("DRAFT");
        (await maker.Send(HttpMethod.Put, $"{Roles}/REJECTED_ROLE/versions/1", Role("REJECTED_ROLE"), ifMatch: $"\"{version.GetProperty("rowVersion").GetInt64()}\""))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        _ = submitted;
    }

    [Fact]
    public async Task Tier3Change_IsApprovedOnSubmission_WithNoChecker()
    {
        var maker = _factory.ClientFor(DatabaseFixture.TenantA, "maker-a");
        const string url = "/api/v1/general/static/salvage-disposal-methods";
        var created = await maker.Send(HttpMethod.Post, url, new { code = "AUCTION", label = "Auction", changeReason = "New method" }, idempotent: true);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        await maker.Send(HttpMethod.Post, $"{url}/AUCTION/versions/1/submit", ifMatch: created.ETag());
        await _factory.DrainOutbox();
        await _factory.DrainOutbox();

        (await (await maker.GetAsync($"{url}/AUCTION/versions")).Json())[0].GetProperty("recordStatus").GetString().ShouldBe("APPROVED");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM sdm.change_request WHERE record_key = 'AUCTION@1' AND status = 'APPROVED' AND checker_id IS NULL"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task TenantRequest_CannotWriteGlobalRows_ThroughTheApi_Item6()
    {
        var tenantUser = _factory.ClientFor(DatabaseFixture.TenantA, "maker-a");

        var globalEndpoint = await tenantUser.Send(HttpMethod.Post, $"{Policies}/global", Policy("GLOBAL_TRY"), idempotent: true);
        globalEndpoint.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // A platform-global (scope G) entity has no tenant write endpoints at all, only /global.
        var globalOnlyEntity = await tenantUser.Send(HttpMethod.Post, Permissions,
            new { code = "claim.approve", permissionCode = "claim.approve", resource = "Claim", operation = "APPROVE", changeReason = "try" }, idempotent: true);
        globalOnlyEntity.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        (await tenantUser.Send(HttpMethod.Post, $"{Permissions}/global",
            new { code = "claim.approve", permissionCode = "claim.approve", resource = "Claim", operation = "APPROVE", changeReason = "try" }, idempotent: true))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // A tenant_id in the body is not a field: tenant rows always take the caller's tenant.
        var spoofed = await tenantUser.Send(HttpMethod.Post, Policies, new Dictionary<string, object?>
        {
            ["code"] = "SPOOF", ["policyCode"] = "SPOOF", ["mfaRequiredRoles"] = Array.Empty<string>(), ["sessionTimeout"] = "PT30M", ["lockoutAttempts"] = 5,
            ["changeReason"] = "try", ["tenantId"] = null,
        }, idempotent: true);
        spoofed.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await spoofed.Json()).GetProperty("tenantId").GetGuid().ShouldBe(DatabaseFixture.TenantA);
    }

    [Theory]
    [InlineData("insys_iam", "iam.cfg_authentication_and_session_policies")]
    [InlineData("insys_sdm", "refdata.ref_currencies")]
    public async Task ModuleRole_CannotWriteGlobalRows_InTheDatabase_Item6(string role, string table)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.ConnectionFor(role));
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var tenant = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{DatabaseFixture.TenantA}', true)", connection, transaction))
        {
            await tenant.ExecuteScalarAsync();
        }

        await using var command = new NpgsqlCommand($"UPDATE {table} SET description = 'changed' WHERE tenant_id IS NULL; INSERT INTO {table} (tenant_id, code, created_by) VALUES (NULL, 'X', 'x')",
            connection, transaction);
        var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    /// <summary>
    /// A feature entitlement's activation names its tenant and feature as fields of its own (Ade, 18 November 2026), so a consumer (the parking ledger)
    /// acts for that tenant without reading the global row, which only the platform may.
    /// </summary>
    [Fact]
    public async Task AnEntitlementsActivation_NamesItsTenantAndFeature_OnTheEvent()
    {
        const string Entitlements = "/api/v1/tenancy/static/feature-entitlements/global";
        var opMaker = _factory.ClientFor(DatabaseFixture.Operator, "op-maker");
        var opChecker = _factory.ClientFor(DatabaseFixture.Operator, "op-checker");
        var tenantCode = await fixture.Database.ScalarAsOwner<string>("SELECT tenant_code FROM tenant.tenant WHERE id = @t", ("t", DatabaseFixture.TenantB));
        var code = $"{tenantCode}:AI_ASSIST";
        var created = await opMaker.Send(HttpMethod.Post, Entitlements, new { code, tenant = tenantCode, feature = "AI_ASSIST", enabled = true, changeReason = "test" }, idempotent: true);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        await opMaker.Send(HttpMethod.Post, $"{Entitlements}/{Uri.EscapeDataString(code)}/versions/1/submit", ifMatch: created.ETag());
        await _factory.DrainOutbox();
        (await opChecker.Send(HttpMethod.Post, $"{Entitlements}/{Uri.EscapeDataString(code)}/versions/1/approve")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var approved = (await (await opMaker.GetAsync($"{Entitlements}/{Uri.EscapeDataString(code)}/versions")).Json())[0];
        if (approved.GetProperty("recordStatus").GetString() == "APPROVED")
        {
            (await opMaker.Send(HttpMethod.Post, $"{Entitlements}/{Uri.EscapeDataString(code)}/versions/1/activate", ifMatch: $"\"{approved.GetProperty("rowVersion").GetInt64()}\""))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var named = await fixture.Database.ScalarAsOwner<string>("""
            SELECT payload->>'entitlementTenant' || ' ' || (payload->>'entitlementFeature') FROM tenant.outbox_event
            WHERE event_type = 'sdm.StaticDataVersionActivated' AND payload->'record'->>'code' = @code
            """, ("code", code));
        named.ShouldBe($"{tenantCode} AI_ASSIST");
        (await fixture.Database.ScalarAsOwner<string>("""
            SELECT coalesce(payload->>'entitlementTenant', 'none') FROM tenant.outbox_event WHERE event_type = 'sdm.StaticDataVersionActivated'
              AND payload->'record'->>'table' <> 'cfg_feature_entitlements' LIMIT 1
            """) ?? "none").ShouldBe("none", "any other row's activation names none");
    }

    [Fact]
    public async Task PlatformAdministrators_MaintainGlobalRows_WithMakerChecker_AndTenantsOverlayThem_SDM008()
    {
        var opMaker = _factory.ClientFor(DatabaseFixture.Operator, "op-maker");
        var opChecker = _factory.ClientFor(DatabaseFixture.Operator, "op-checker");
        var created = await opMaker.Send(HttpMethod.Post, $"{Policies}/global", Policy("BASELINE"), idempotent: true);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var globalDraft = await created.Json();
        globalDraft.GetProperty("tenantId").ValueKind.ShouldBe(JsonValueKind.Null);
        globalDraft.GetProperty("sessionTimeout").GetString().ShouldBe("PT30M");

        await opMaker.Send(HttpMethod.Post, $"{Policies}/global/BASELINE/versions/1/submit", ifMatch: created.ETag());
        await _factory.DrainOutbox();
        (await opMaker.Send(HttpMethod.Post, $"{Policies}/global/BASELINE/versions/1/approve")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await opChecker.Send(HttpMethod.Post, $"{Policies}/global/BASELINE/versions/1/approve")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var approved = (await (await opMaker.GetAsync($"{Policies}/global/BASELINE/versions")).Json())[0];
        approved.GetProperty("recordStatus").GetString().ShouldBe("APPROVED");
        (await opMaker.Send(HttpMethod.Post, $"{Policies}/global/BASELINE/versions/1/activate", ifMatch: $"\"{approved.GetProperty("rowVersion").GetInt64()}\""))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // Tenants read the global baseline...
        var tenant = _factory.ClientFor(DatabaseFixture.TenantA, "maker-a");
        var baseline = await (await tenant.GetAsync($"{Policies}/BASELINE")).Json();
        baseline.GetProperty("tenantId").ValueKind.ShouldBe(JsonValueKind.Null);
        baseline.GetProperty("lockoutAttempts").GetInt32().ShouldBe(5);

        // ...and their own row with the same code overrides it, for them only.
        var checker = _factory.ClientFor(DatabaseFixture.TenantA, "checker-a");
        var own = await tenant.Send(HttpMethod.Post, Policies, Policy("BASELINE", lockoutAttempts: 3), idempotent: true);
        own.StatusCode.ShouldBe(HttpStatusCode.Created);
        await tenant.Send(HttpMethod.Post, $"{Policies}/BASELINE/versions/1/submit", ifMatch: own.ETag());
        await _factory.DrainOutbox();
        await checker.Send(HttpMethod.Post, $"{Policies}/BASELINE/versions/1/approve");
        await _factory.DrainOutbox();
        var ownApproved = (await (await tenant.GetAsync($"{Policies}/BASELINE/versions")).Json())[0];
        await tenant.Send(HttpMethod.Post, $"{Policies}/BASELINE/versions/1/activate", ifMatch: $"\"{ownApproved.GetProperty("rowVersion").GetInt64()}\"");

        (await (await tenant.GetAsync($"{Policies}/BASELINE")).Json()).GetProperty("lockoutAttempts").GetInt32().ShouldBe(3);
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantB, "reader-b", StaticDataPermissions.Read);
        (await (await _factory.ClientFor(DatabaseFixture.TenantB, "reader-b").GetAsync($"{Policies}/BASELINE")).Json()).GetProperty("lockoutAttempts").GetInt32()
            .ShouldBe(5);
    }

    /// <summary>
    /// BR-019: a role's function grant naming a function the catalogue does not have (SD_BILLING_VIEW), or a JOB function, is
    /// refused with a field error on create and on import; it used to be approved and grant nothing.
    /// </summary>
    [Fact]
    public async Task ARoleFunctionGrant_NamesAFunctionInTheCatalogue_OnCreateAndImport_BR019()
    {
        const string grants = "/api/v1/identity/static/role-function-assignments";
        Insys.BuildingBlocks.Api.Functions.FunctionRegistry.All().Any(f => f.Function.Code == "SD_BILLING_VIEW").ShouldBeFalse("the example is not a catalogue function");
        var maker = _factory.ClientFor(DatabaseFixture.TenantA, "maker-a");
        static object Grant(string code, string function) => new { code, roleCode = "BR019_ROLE", functionCode = function, changeReason = "BR-019" };

        var unknown = await maker.Send(HttpMethod.Post, grants, Grant("BR019-UNKNOWN", "SD_BILLING_VIEW"), idempotent: true);
        unknown.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await unknown.Content.ReadAsStringAsync());
        (await unknown.Json()).GetProperty("errors").GetProperty("functionCode")[0].GetString().ShouldBe("SD_BILLING_VIEW is not a function in the catalogue.");

        var job = await maker.Send(HttpMethod.Post, grants, Grant("BR019-JOB", "WEBHOOK_DELIVERY"), idempotent: true);
        (await job.Json()).GetProperty("errors").GetProperty("functionCode")[0].GetString()!.ShouldContain("JOB function");

        var imported = await maker.Send(HttpMethod.Post, $"{grants}/imports",
            new { fileRef = "grants-bad.csv", lines = new[] { Grant("BR019-OK", "IDENTITY_USERS"), Grant("BR019-BAD", "SD_BILLING_VIEW") } }, idempotent: true);
        imported.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await imported.Json()).GetProperty("errors").TryGetProperty("line 2", out _).ShouldBeTrue();

        (await maker.Send(HttpMethod.Post, grants, Grant("BR019-GOOD", "IDENTITY_USERS"), idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>
    /// OQ-190 (Ade, 19 November 2026): a role granted ASSIST_PRODUCT_DRAFT_RUN must also hold ASSIST_PRODUCT_DRAFT_REVIEW. RUN alone is refused at submission;
    /// with REVIEW in force it is accepted; REVIEW can't be deprecated or retired while RUN stays, and goes once RUN has gone.
    /// </summary>
    [Fact]
    public async Task ARoleGrantedTheDraftRun_MustHoldItsReview_AndTheReviewCantGoWhileTheRunStays_OQ190()
    {
        const string grants = "/api/v1/identity/static/role-function-assignments";
        var maker = _factory.ClientFor(DatabaseFixture.TenantA, "maker-a");
        var checker = _factory.ClientFor(DatabaseFixture.TenantA, "checker-a");
        static object Grant(string code, string function) => new { code, roleCode = "OQ190_ROLE", functionCode = function, changeReason = "OQ-190" };

        async Task<HttpResponseMessage> Submit(string code, string function)
        {
            var created = await maker.Send(HttpMethod.Post, grants, Grant(code, function), idempotent: true);
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            return await maker.Send(HttpMethod.Post, $"{grants}/{code}/versions/1/submit", ifMatch: created.ETag());
        }

        async Task<long> InForce(string code)
        {
            await _factory.DrainOutbox();
            (await checker.Send(HttpMethod.Post, $"{grants}/{code}/versions/1/approve")).StatusCode.ShouldBe(HttpStatusCode.OK);
            await _factory.DrainOutbox();
            var approved = (await (await maker.GetAsync($"{grants}/{code}/versions")).Json())[0];
            var activated = await maker.Send(HttpMethod.Post, $"{grants}/{code}/versions/1/activate", ifMatch: $"\"{approved.GetProperty("rowVersion").GetInt64()}\"");
            activated.StatusCode.ShouldBe(HttpStatusCode.OK, await activated.Content.ReadAsStringAsync());
            return (await activated.Json()).GetProperty("rowVersion").GetInt64();
        }

        Task<HttpResponseMessage> Leave(string code, string action, long rowVersion) =>
            maker.Send(HttpMethod.Post, $"{grants}/{code}/versions/1/{action}", ifMatch: $"\"{rowVersion}\"");

        var alone = await Submit("OQ190_ROLE:ASSIST_PRODUCT_DRAFT_RUN", "ASSIST_PRODUCT_DRAFT_RUN");
        alone.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await alone.Content.ReadAsStringAsync());
        (await alone.Code()).ShouldBe("ASSIST_RUN_NEEDS_REVIEW");

        (await Submit("OQ190_ROLE:ASSIST_PRODUCT_DRAFT_REVIEW", "ASSIST_PRODUCT_DRAFT_REVIEW")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var review = await InForce("OQ190_ROLE:ASSIST_PRODUCT_DRAFT_REVIEW");
        var withReview = await Submit("OQ190_ROLE:ASSIST_PRODUCT_DRAFT_RUN2", "ASSIST_PRODUCT_DRAFT_RUN");
        withReview.StatusCode.ShouldBe(HttpStatusCode.OK, await withReview.Content.ReadAsStringAsync());
        var run = await InForce("OQ190_ROLE:ASSIST_PRODUCT_DRAFT_RUN2");

        foreach (var action in new[] { "deprecate", "retire" })
        {
            var removed = await Leave("OQ190_ROLE:ASSIST_PRODUCT_DRAFT_REVIEW", action, review);
            removed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, $"{action}: {await removed.Content.ReadAsStringAsync()}");
            (await removed.Code()).ShouldBe("ASSIST_RUN_NEEDS_REVIEW");
        }

        (await Leave("OQ190_ROLE:ASSIST_PRODUCT_DRAFT_RUN2", "deprecate", run)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Leave("OQ190_ROLE:ASSIST_PRODUCT_DRAFT_REVIEW", "deprecate", review)).StatusCode.ShouldBe(HttpStatusCode.OK, "the run has gone, so the review may go");
    }

    /// <summary>
    /// DE-016: the maker discards their own draft that was never submitted (If-Match, audited), and the code is free for a new
    /// draft; nobody else discards it, and nothing past a draft is discarded (SDM-007). The refusal of a second draft names the step.
    /// </summary>
    [Fact]
    public async Task ADraftNeverSubmitted_IsDiscardedByItsMakerOnly_AndFreesItsCode_DE016_SDM007()
    {
        var maker = _factory.ClientFor(DatabaseFixture.TenantA, "maker-a");
        var checker = _factory.ClientFor(DatabaseFixture.TenantA, "checker-a");
        var created = await maker.Send(HttpMethod.Post, Policies, Policy("DISCARD_POLICY"), idempotent: true);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await created.Json()).GetProperty("id").GetGuid();

        var second = await maker.Send(HttpMethod.Post, Policies, Policy("DISCARD_POLICY"), idempotent: true);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.Code()).ShouldBe("STATIC_DRAFT_EXISTS");
        (await second.Content.ReadAsStringAsync()).ShouldContain("DISCARD_POLICY/versions/1/discard");

        var url = $"{Policies}/DISCARD_POLICY/versions/1/discard";
        // OQ-204: someone else holding the maintain function waits for the tenant's days (see the stale draft tests below).
        var byOther = await checker.Send(HttpMethod.Post, url, ifMatch: created.ETag());
        byOther.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await byOther.Code()).ShouldBe("STATIC_DRAFT_NOT_STALE");
        (await maker.Send(HttpMethod.Post, url)).StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
        (await maker.Send(HttpMethod.Post, url, ifMatch: "\"99\"")).StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);

        (await maker.Send(HttpMethod.Post, url, ifMatch: created.ETag())).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await maker.GetAsync($"{Policies}/DISCARD_POLICY/versions")).Json()).GetArrayLength().ShouldBe(0);
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE operation = 'DiscardDraft' AND target_id = @id", ("id", id.ToString())))
            .ShouldBe(1L);
        // OQ-204: the maker's own discard tells nobody: no event, no task.
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.outbox_event WHERE event_type = 'sdm.StaticDataDraftDiscarded' AND aggregate_id = @id",
            ("id", id))).ShouldBe(0L);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM workflow.process_instance WHERE process_code = 'STATIC_DRAFT_DISCARDED' AND business_ref_id = @id",
            ("id", id))).ShouldBe(0L);

        var again = await maker.Send(HttpMethod.Post, Policies, Policy("DISCARD_POLICY"), idempotent: true);
        again.StatusCode.ShouldBe(HttpStatusCode.Created, "the code is free for a new draft");
        (await again.Json()).GetProperty("version").GetInt32().ShouldBe(1);
        var submitted = await maker.Send(HttpMethod.Post, $"{Policies}/DISCARD_POLICY/versions/1/submit", ifMatch: again.ETag());
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK, await submitted.Content.ReadAsStringAsync());
        var notDraft = await maker.Send(HttpMethod.Post, url, ifMatch: submitted.ETag());
        notDraft.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await notDraft.Code()).ShouldBe("INVALID_STATE_TRANSITION");
    }

    /// <summary>
    /// DE-016, OQ-204 (Ade, 25 November 2026): another maintainer discards someone else's draft only once it has been untouched (created
    /// or last edited) for the tenant's days, 7 by default, in 24-hour periods by the clock; sooner, 409 STATIC_DRAFT_NOT_STALE names when.
    /// The pending list and the versions say when too. The reason is required, at most 500 characters.
    /// </summary>
    [Fact]
    public async Task AnotherMaintainer_DiscardsADraft_OnlyOnceUntouchedForTheTenantsDays_WithAReason_DE016_OQ204()
    {
        var maker = _factory.ClientFor(DatabaseFixture.TenantA, "maker-a");
        var other = _factory.ClientFor(DatabaseFixture.TenantA, "checker-a");
        var created = await maker.Send(HttpMethod.Post, Policies, Policy("STALE_POLICY"), idempotent: true);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var draft = await created.Json();
        var id = draft.GetProperty("id").GetGuid();
        var createdAt = draft.GetProperty("createdAt").GetDateTimeOffset();
        var url = $"{Policies}/STALE_POLICY/versions/1/discard";

        async Task<DateTimeOffset> Listed() =>
            (await (await maker.GetAsync($"{Policies}/pending?status=DRAFT&limit=200")).Json()).GetProperty("items").EnumerateArray()
            .Single(v => v.GetProperty("code").GetString() == "STALE_POLICY").GetProperty("discardableByOthersFrom").GetDateTimeOffset();
        async Task<DateTimeOffset> Versioned() =>
            (await (await maker.GetAsync($"{Policies}/STALE_POLICY/versions")).Json())[0].GetProperty("discardableByOthersFrom").GetDateTimeOffset();
        (await Listed()).ShouldBe(createdAt.AddDays(7), Precision);
        (await Versioned()).ShouldBe(createdAt.AddDays(7), Precision);

        var fresh = await other.Send(HttpMethod.Post, url, new { reason = "Maker has left" }, ifMatch: created.ETag());
        fresh.StatusCode.ShouldBe(HttpStatusCode.Conflict, await fresh.Content.ReadAsStringAsync());
        var refusal = await fresh.Json();
        refusal.GetProperty("code").GetString().ShouldBe("STATIC_DRAFT_NOT_STALE");
        refusal.GetProperty("discardableFrom").GetDateTimeOffset().ShouldBe(createdAt.AddDays(7), Precision);

        // An edit touches it: the days count from the edit, not from its creation.
        var edited = await maker.Send(HttpMethod.Put, $"{Policies}/STALE_POLICY/versions/1", Policy("STALE_POLICY", lockoutAttempts: 4), ifMatch: created.ETag());
        edited.StatusCode.ShouldBe(HttpStatusCode.OK, await edited.Content.ReadAsStringAsync());
        var editedAt = (await edited.Json()).GetProperty("editedAt").GetDateTimeOffset();
        await Age("STALE_POLICY", created: TimeSpan.FromDays(10), edited: TimeSpan.FromDays(7) - TimeSpan.FromMinutes(5));
        (await Listed()).ShouldBe(editedAt.AddMinutes(5), Precision, "7 days after the edit, now 7 days less 5 minutes ago");
        var recent = await other.Send(HttpMethod.Post, url, new { reason = "Maker has left" }, ifMatch: edited.ETag());
        (await recent.Code()).ShouldBe("STATIC_DRAFT_NOT_STALE", "edited less than 7 × 24 hours ago, though created 10 days ago");

        await Age("STALE_POLICY", created: TimeSpan.Zero, edited: TimeSpan.FromMinutes(10)); // now edited 7 days and 5 minutes ago
        var noReason = await other.Send(HttpMethod.Post, url, ifMatch: edited.ETag());
        noReason.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await noReason.Content.ReadAsStringAsync());
        var invalid = await noReason.Json();
        invalid.GetProperty("code").GetString().ShouldBe("VALIDATION_FAILED");
        invalid.GetProperty("errors").TryGetProperty("reason", out _).ShouldBeTrue();
        (await other.Send(HttpMethod.Post, url, new { reason = "  " }, ifMatch: edited.ETag())).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await other.Send(HttpMethod.Post, url, new { reason = new string('x', 501) }, ifMatch: edited.ETag())).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var discarded = await other.Send(HttpMethod.Post, url, new { reason = "Maker has left the team" }, ifMatch: edited.ETag());
        discarded.StatusCode.ShouldBe(HttpStatusCode.NoContent, await discarded.Content.ReadAsStringAsync());
        (await (await maker.GetAsync($"{Policies}/STALE_POLICY/versions")).Json()).GetArrayLength().ShouldBe(0);
        await _factory.DrainOutbox();
        var makerId = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM iam.user_account WHERE tenant_id = @t AND external_subject = 'maker-a'",
            ("t", DatabaseFixture.TenantA));
        var otherId = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM iam.user_account WHERE tenant_id = @t AND external_subject = 'checker-a'",
            ("t", DatabaseFixture.TenantA));
        (await fixture.Database.ScalarAsOwner<long>("""
            SELECT count(*) FROM audit.audit_event
            WHERE tenant_id = @t AND operation = 'DiscardStaleDraft' AND target_id = @id AND actor_id = @actor
              AND details->>'reason' = 'Maker has left the team' AND details->>'makerId' = @maker
              AND details->>'daysUntouched' = '7' AND details->>'staleDraftDiscardDays' = '7'
            """, ("t", DatabaseFixture.TenantA), ("id", id.ToString()), ("actor", otherId.ToString()), ("maker", makerId.ToString()))).ShouldBe(1L);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE operation = 'DiscardDraft' AND target_id = @id", ("id", id.ToString())))
            .ShouldBe(0L, "the maker's own discard is audited as DiscardDraft; this one is DiscardStaleDraft");

        // Ade, 25 November 2026 (OQ-204): the maker is told, by an informational task assigned to them: the table, code, version, who, when and why.
        await _factory.DrainOutbox();
        var mine = await maker.GetAsync("/api/v1/workflow/tasks/mine?limit=100");
        mine.StatusCode.ShouldBe(HttpStatusCode.OK, await mine.Content.ReadAsStringAsync());
        var told = (await mine.Json()).GetProperty("items").EnumerateArray()
            .Single(t => t.GetProperty("processCode").GetString() == "STATIC_DRAFT_DISCARDED" && t.GetProperty("businessRefId").GetGuid() == id);
        told.GetProperty("businessRefType").GetString().ShouldBe("STATIC_DATA_VERSION");
        told.GetProperty("assignedToMe").GetBoolean().ShouldBeTrue();
        told.GetProperty("informational").GetBoolean().ShouldBeTrue();
        var notice = told.GetProperty("draftDiscarded");
        notice.GetProperty("entityRoute").GetString().ShouldBe("identity/static/authentication-and-session-policies");
        (notice.GetProperty("code").GetString(), notice.GetProperty("versionNumber").GetInt32(), notice.GetProperty("global").GetBoolean())
            .ShouldBe(("STALE_POLICY", 1, false));
        notice.GetProperty("discardedBy").GetGuid().ShouldBe(otherId);
        notice.GetProperty("reason").GetString().ShouldBe("Maker has left the team");
        notice.GetProperty("discardedAt").GetDateTimeOffset().ShouldBeGreaterThan(editedAt);
        // OQ-203 (Ade, 26 November 2026): the task names the draft (its table and record key) and links to the table's page of versions not in force.
        (notice.GetProperty("table").GetString(), notice.GetProperty("recordKey").GetString()).ShouldBe(("iam.cfg_authentication_and_session_policies", "STALE_POLICY@1"));
        await TheTaskLinksToTheTablesPage(maker, told, "/api/v1/identity/static/authentication-and-session-policies/pending");
    }

    /// <summary>
    /// OQ-203 (Ade, 26 November 2026): a discarded draft's or import's task links to its table's page of versions not in force: subjectOperation is that
    /// GET operation of the host (List{Table}Pending), subjectPath its path, and the maker reads it.
    /// </summary>
    private async Task TheTaskLinksToTheTablesPage(HttpClient maker, JsonElement task, string path)
    {
        var operation = task.GetProperty("subjectOperation").GetString();
        task.GetProperty("subjectPath").GetString().ShouldBe(path);
        task.GetProperty("draftDiscarded").GetProperty("tableOperation").GetString().ShouldBe(operation);
        var gets = _factory.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>().SelectMany(e => e.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()?.HttpMethods.Contains("GET") == true)
            .Select(e => (Name: e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IEndpointNameMetadata>()?.EndpointName, Route: e.RoutePattern.RawText))
            .Where(e => e.Name == operation).ToList();
        gets.Count.ShouldBe(1, $"{operation} is one GET operation of the host");
        System.Text.RegularExpressions.Regex.Replace(gets[0].Route!, "^/api/v{[^}]*}/", "/api/v1/").TrimEnd('/').ShouldBe(path);
        var page = await maker.GetAsync(path);
        page.StatusCode.ShouldBe(HttpStatusCode.OK, await page.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// DE-016, OQ-204: only someone holding the table's maintain function discards another's draft, however long untouched: the same 403 as any
    /// other change to the table without it. The static_data.update permission alone, through another table's maintain function, is not enough.
    /// </summary>
    [Fact]
    public async Task WithoutTheTablesMaintainFunction_NobodyDiscardsAnothersDraft_EvenUntouched_DE016_OQ204()
    {
        var maker = _factory.ClientFor(DatabaseFixture.TenantA, "maker-a");
        var created = await maker.Send(HttpMethod.Post, Policies, Policy("STALE_NOFUNC"), idempotent: true);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        await Age("STALE_NOFUNC", created: TimeSpan.FromDays(30));
        await fixture.Database.UserWithFunctions(DatabaseFixture.TenantA, "viewer-a", "SD_IDENTITY_AND_ACCESS_MANAGEMENT_VIEW", "SD_PARTY_MANAGEMENT_VIEW",
            "SD_PARTY_MANAGEMENT_MAINTAIN");
        var refused = await _factory.ClientFor(DatabaseFixture.TenantA, "viewer-a")
            .Send(HttpMethod.Post, $"{Policies}/STALE_NOFUNC/versions/1/discard", new { reason = "Maker has left" }, ifMatch: created.ETag());
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync());
        (await refused.Code()).ShouldBe("FUNCTION_NOT_GRANTED");
        (await (await maker.GetAsync($"{Policies}/STALE_NOFUNC/versions")).Json()).GetArrayLength().ShouldBe(1);
    }

    /// <summary>
    /// DE-016, OQ-204: the days are a tenant governance setting, 7 by default, at least 1, changed through the settings (If-Match, audited, at once)
    /// and used for the tenant's drafts; another tenant keeps its own. The platform operator's setting applies to the global tables.
    /// </summary>
    [Fact]
    public async Task TheStaleDraftDays_DefaultTo7_ChangeThroughTheGovernanceSettings_AndApplyToTheTenantsDrafts_DE016_OQ204()
    {
        var tenant = Guid.CreateVersion7();
        await fixture.Database.SeedTenant(tenant, "TSTL");
        await fixture.Database.UserWithPermissions(tenant, "stale-admin",
            [.. All, StaticDataGovernancePermissions.SettingRead, StaticDataGovernancePermissions.SettingUpdate]);
        await fixture.Database.UserWithPermissions(tenant, "stale-maker", All);
        var admin = _factory.ClientFor(tenant, "stale-admin");
        const string settings = "/api/v1/static-data/settings";

        var defaults = await admin.GetAsync(settings);
        defaults.StatusCode.ShouldBe(HttpStatusCode.OK, await defaults.Content.ReadAsStringAsync());
        (await defaults.Json()).GetProperty("staleDraftDiscardDays").GetInt32().ShouldBe(7);
        (await fixture.Database.ScalarAsOwner<int>("SELECT column_default::int FROM information_schema.columns WHERE table_schema = 'sdm' AND table_name = 'tenant_governance_setting' AND column_name = 'stale_draft_discard_days'"))
            .ShouldBe(7);

        var zero = await admin.Send(HttpMethod.Put, $"{settings}/stale-draft-discard-days", new { staleDraftDiscardDays = 0 }, ifMatch: "\"0\"");
        zero.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await zero.Code()).ShouldBe("VALIDATION_FAILED");
        (await admin.Send(HttpMethod.Put, $"{settings}/stale-draft-discard-days", new { staleDraftDiscardDays = 2 }, ifMatch: "\"5\""))
            .StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        var changed = await admin.Send(HttpMethod.Put, $"{settings}/stale-draft-discard-days", new { staleDraftDiscardDays = 2 }, ifMatch: "\"0\"");
        changed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await changed.Content.ReadAsStringAsync());
        var now = await (await admin.GetAsync(settings)).Json();
        now.GetProperty("staleDraftDiscardDays").GetInt32().ShouldBe(2);
        now.GetProperty("tier2MakerChecker").GetBoolean().ShouldBeTrue("the other settings are as they were");
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("""
            SELECT count(*) FROM audit.audit_event
            WHERE tenant_id = @t AND operation = 'ChangeStaleDraftDiscardDays' AND details->>'before' = '7' AND details->>'after' = '2'
            """, ("t", tenant))).ShouldBe(1L);
        await Should.ThrowAsync<PostgresException>(() => fixture.Database.ExecuteAsOwner(
            "UPDATE sdm.tenant_governance_setting SET stale_draft_discard_days = 0 WHERE tenant_id = @t", ("t", tenant)));

        // The tenant's 2 days: a draft untouched for 2 × 24 hours is discardable by another maintainer.
        var maker = _factory.ClientFor(tenant, "stale-maker");
        var created = await maker.Send(HttpMethod.Post, Policies, Policy("STALE_TWO"), idempotent: true);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var createdAt = (await created.Json()).GetProperty("createdAt").GetDateTimeOffset();
        (await (await maker.GetAsync($"{Policies}/STALE_TWO/versions")).Json())[0].GetProperty("discardableByOthersFrom").GetDateTimeOffset()
            .ShouldBe(createdAt.AddDays(2), Precision);
        await Age("STALE_TWO", created: TimeSpan.FromDays(2), tenant: tenant);
        var discarded = await admin.Send(HttpMethod.Post, $"{Policies}/STALE_TWO/versions/1/discard", new { reason = "Abandoned draft" }, ifMatch: created.ETag());
        discarded.StatusCode.ShouldBe(HttpStatusCode.NoContent, await discarded.Content.ReadAsStringAsync());

        // Another tenant keeps the default.
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "stale-reader-a", StaticDataGovernancePermissions.SettingRead);
        (await (await _factory.ClientFor(DatabaseFixture.TenantA, "stale-reader-a").GetAsync(settings)).Json()).GetProperty("staleDraftDiscardDays").GetInt32()
            .ShouldBe(7);
    }

    /// <summary>DE-016, OQ-204: a global draft (the platform operator's) is discarded by another platform maintainer under the operator tenant's setting.</summary>
    [Fact]
    public async Task AGlobalDraft_IsDiscardedByAnotherPlatformMaintainer_UnderTheOperatorsSetting_DE016_OQ204()
    {
        var opMaker = _factory.ClientFor(DatabaseFixture.Operator, "op-maker");
        var opOther = _factory.ClientFor(DatabaseFixture.Operator, "op-checker");
        var created = await opMaker.Send(HttpMethod.Post, $"{Policies}/global", Policy("STALE_GLOBAL"), idempotent: true);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var draft = await created.Json();
        var createdAt = draft.GetProperty("createdAt").GetDateTimeOffset();
        (await (await opMaker.GetAsync($"{Policies}/global/pending?status=DRAFT&limit=200")).Json()).GetProperty("items").EnumerateArray()
            .Single(v => v.GetProperty("code").GetString() == "STALE_GLOBAL").GetProperty("discardableByOthersFrom").GetDateTimeOffset()
            .ShouldBe(createdAt.AddDays(7), Precision, "the operator tenant's setting, 7 by default");
        var url = $"{Policies}/global/STALE_GLOBAL/versions/1/discard";
        (await (await opOther.Send(HttpMethod.Post, url, new { reason = "Left behind" }, ifMatch: created.ETag())).Code()).ShouldBe("STATIC_DRAFT_NOT_STALE");

        await Age("STALE_GLOBAL", created: TimeSpan.FromDays(7), global: true);
        (await opOther.Send(HttpMethod.Post, url, new { reason = "Left behind" }, ifMatch: created.ETag())).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE operation = 'DiscardStaleDraft' AND target_id = @id AND details->>'global' = 'True'",
            ("id", draft.GetProperty("id").GetGuid().ToString()))).ShouldBe(1L);
    }

    /// <summary>Timestamps round to microseconds in the database.</summary>
    private static readonly TimeSpan Precision = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Moves a policy draft's created_at (and edited_at, when given) back by the given time, as if it had been left untouched that much longer
    /// (DE-016, OQ-204). Tenant A's row unless another tenant is named; the global row when <paramref name="global"/>.
    /// </summary>
    private Task Age(string code, TimeSpan created, TimeSpan? edited = null, Guid? tenant = null, bool global = false) =>
        fixture.Database.ExecuteAsOwner($"""
            UPDATE iam.cfg_authentication_and_session_policies SET created_at = created_at - @created{(edited is null ? "" : ", edited_at = edited_at - @edited")}
            WHERE code = @code AND record_status = 'DRAFT' AND {(global ? "tenant_id IS NULL AND @t::uuid IS NOT NULL" : "tenant_id = @t")}
            """, ("code", code), ("created", created), ("edited", edited ?? TimeSpan.Zero), ("t", tenant ?? DatabaseFixture.TenantA));

    /// <summary>DE-015: a table's versions not in force (draft, waiting for approval, approved), by status, with their maker and change request.</summary>
    [Fact]
    public async Task ATablesVersionsNotInForce_AreListedByStatus_WithTheirMakerAndChangeRequest_DE015()
    {
        var maker = _factory.ClientFor(DatabaseFixture.TenantA, "maker-a");
        var makerId = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM iam.user_account WHERE tenant_id = @t AND external_subject = 'maker-a'",
            ("t", DatabaseFixture.TenantA));
        var draft = await maker.Send(HttpMethod.Post, Policies, Policy("PEND_DRAFT"), idempotent: true);
        draft.StatusCode.ShouldBe(HttpStatusCode.Created);
        var waiting = await maker.Send(HttpMethod.Post, Policies, Policy("PEND_SUBMIT"), idempotent: true);
        waiting.StatusCode.ShouldBe(HttpStatusCode.Created, await waiting.Content.ReadAsStringAsync());
        (await maker.Send(HttpMethod.Post, $"{Policies}/PEND_SUBMIT/versions/1/submit", ifMatch: waiting.ETag())).StatusCode.ShouldBe(HttpStatusCode.OK);

        async Task<List<JsonElement>> Listed(string? status)
        {
            var items = new List<JsonElement>();
            string? cursor = null;
            do
            {
                var page = await maker.GetAsync($"{Policies}/pending?limit=2{(status is null ? "" : $"&status={status}")}{(cursor is null ? "" : $"&cursor={cursor}")}");
                page.StatusCode.ShouldBe(HttpStatusCode.OK, await page.Content.ReadAsStringAsync());
                var body = await page.Json();
                items.AddRange(body.GetProperty("items").EnumerateArray());
                cursor = body.GetProperty("nextCursor").ValueKind == JsonValueKind.String ? body.GetProperty("nextCursor").GetString() : null;
            }
            while (cursor is not null);
            return items;
        }

        var drafts = await Listed("DRAFT");
        var mine = drafts.Single(v => v.GetProperty("code").GetString() == "PEND_DRAFT");
        (mine.GetProperty("status").GetString(), mine.GetProperty("versionNumber").GetInt32(), mine.GetProperty("makerId").GetGuid()).ShouldBe(("DRAFT", 1, makerId));
        mine.GetProperty("changeRequestId").ValueKind.ShouldBe(JsonValueKind.Null);
        drafts.ShouldNotContain(v => v.GetProperty("code").GetString() == "PEND_SUBMIT");

        var submitted = (await Listed("SUBMITTED")).Single(v => v.GetProperty("code").GetString() == "PEND_SUBMIT");
        submitted.GetProperty("status").GetString().ShouldBe("SUBMITTED");
        submitted.GetProperty("changeRequestId").GetGuid().ShouldNotBe(Guid.Empty);
        (await Listed("APPROVED")).ShouldNotContain(v => v.GetProperty("code").GetString()!.StartsWith("PEND_", StringComparison.Ordinal));
        (await Listed(null)).Select(v => v.GetProperty("code").GetString()).ShouldContain("PEND_DRAFT");
        (await maker.GetAsync($"{Policies}/pending?status=ACTIVE")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await (await maker.GetAsync(Policies)).Content.ReadAsStringAsync()).ShouldNotContain("PEND_DRAFT", Case.Sensitive, "the list in force leaves them out");
    }

    /// <summary>DE-017: the import batches list filters by status, and each batch's id is its import's batchId.</summary>
    [Fact]
    public async Task ImportBatches_AreFilteredByStatus_AndEachIdIsItsImportsBatchId_DE017()
    {
        var maker = _factory.ClientFor(DatabaseFixture.TenantA, "maker-a");
        var committed = await maker.Send(HttpMethod.Post, $"{Policies}/imports", new { fileRef = "de017.csv", lines = new[] { Policy("DE017_ONE") } }, idempotent: true);
        committed.StatusCode.ShouldBe(HttpStatusCode.OK, await committed.Content.ReadAsStringAsync());
        var batch = (await committed.Json()).GetProperty("batchId").GetGuid();
        await _factory.DrainOutbox();

        async Task<List<JsonElement>> Listed(string status)
        {
            var page = await maker.GetAsync($"/api/v1/static-data/imports?status={status}&limit=200");
            page.StatusCode.ShouldBe(HttpStatusCode.OK, await page.Content.ReadAsStringAsync());
            return [.. (await page.Json()).GetProperty("items").EnumerateArray()];
        }

        var found = (await Listed("COMMITTED")).Single(b => b.GetProperty("id").GetGuid() == batch);
        found.GetProperty("batchId").GetGuid().ShouldBe(batch);
        found.GetProperty("status").GetString().ShouldBe("Committed", StringCompareShould.IgnoreCase);
        (await Listed("REJECTED")).ShouldNotContain(b => b.GetProperty("id").GetGuid() == batch);
        (await maker.GetAsync("/api/v1/static-data/imports?status=SUBMITTED_LATER")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await (await maker.GetAsync($"{Policies}/imports/{batch}")).Json()).GetArrayLength().ShouldBe(1, "the id opens the import's lines");
    }

    /// <summary>
    /// OQ-203 item 1 (Ade, 26 November 2026): an import never submitted (every line a draft with no change request) is discarded as a whole by its maker at
    /// any time, with no body: every line goes in one transaction, audited (DiscardImport, with what each held), the codes are free, the import reads
    /// DISCARDED, and nobody is told. A second discard finds nothing. Its lines are never discarded one by one.
    /// </summary>
    [Fact]
    public async Task AnImportNeverSubmitted_IsDiscardedAsAWholeByItsMaker_AndFreesItsCodes_OQ203_SDM007()
    {
        var maker = _factory.ClientFor(DatabaseFixture.TenantA, "maker-a");
        var batch = await Imported(maker, "OQ203_OWN_A", "OQ203_OWN_B");
        var line = await maker.GetAsync($"{Policies}/OQ203_OWN_A/versions");
        var lineDiscard = await maker.Send(HttpMethod.Post, $"{Policies}/OQ203_OWN_A/versions/1/discard",
            ifMatch: $"\"{(await line.Json())[0].GetProperty("rowVersion").GetInt64()}\"");
        (await lineDiscard.Code()).ShouldBe("INVALID_STATE_TRANSITION");
        (await lineDiscard.Content.ReadAsStringAsync()).ShouldContain($"imports/{batch}/discard");

        var discarded = await maker.Send(HttpMethod.Post, $"{Policies}/imports/{batch}/discard");
        discarded.StatusCode.ShouldBe(HttpStatusCode.NoContent, await discarded.Content.ReadAsStringAsync());
        (await maker.GetAsync($"{Policies}/imports/{batch}")).StatusCode.ShouldBe(HttpStatusCode.NotFound, "its lines are gone");
        (await maker.Send(HttpMethod.Post, $"{Policies}/imports/{batch}/discard")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("""
            SELECT count(*) FROM audit.audit_event WHERE tenant_id = @t AND operation = 'DiscardImport' AND details->>'importBatchId' = @b AND details->>'lines' = '2'
              AND details->>'before' IS NOT NULL
            """, ("t", DatabaseFixture.TenantA), ("b", batch.ToString()))).ShouldBe(2L, "one entry per line, with what it held");
        (await ImportStatus(maker, batch)).ShouldBe("DISCARDED");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM workflow.process_instance WHERE process_code = 'STATIC_DRAFT_DISCARDED' AND business_ref_id = @b",
            ("b", batch))).ShouldBe(0L, "its maker's own discard tells nobody");
        (await maker.Send(HttpMethod.Post, Policies, Policy("OQ203_OWN_A"), idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.Created, "the code is free");
    }

    /// <summary>
    /// OQ-203 item 1 (Ade, 26 November 2026): another holder of the table's maintain function discards someone else's import never submitted only once no
    /// line has been touched for the tenant's days (OQ-204's setting and clock: an edit to any line counts), with a reason, audited (DiscardStaleImport);
    /// sooner 409 STATIC_DRAFT_NOT_STALE with discardableFrom. The maker is told by an informational task about the import, which names the table, the
    /// import (its batchNumber), who discarded it and why, and links to the table's page.
    /// </summary>
    [Fact]
    public async Task AnotherMaintainer_DiscardsAnImportNeverSubmitted_OnlyOnceUntouchedForTheTenantsDays_AndTheMakerIsTold_OQ203_OQ204()
    {
        var maker = _factory.ClientFor(DatabaseFixture.TenantA, "maker-a");
        var other = _factory.ClientFor(DatabaseFixture.TenantA, "checker-a");
        var batch = await Imported(maker, "OQ203_STALE_A", "OQ203_STALE_B");
        var url = $"{Policies}/imports/{batch}/discard";

        var fresh = await other.Send(HttpMethod.Post, url, new { reason = "Abandoned import" });
        fresh.StatusCode.ShouldBe(HttpStatusCode.Conflict, await fresh.Content.ReadAsStringAsync());
        var refusal = await fresh.Json();
        refusal.GetProperty("code").GetString().ShouldBe("STATIC_DRAFT_NOT_STALE");
        refusal.GetProperty("staleDraftDiscardDays").GetInt32().ShouldBe(7);

        // An edit to one line touches the import: the days count from it.
        var line = await maker.GetAsync($"{Policies}/OQ203_STALE_B/versions");
        var edited = await maker.Send(HttpMethod.Put, $"{Policies}/OQ203_STALE_B/versions/1", Policy("OQ203_STALE_B", lockoutAttempts: 4),
            ifMatch: $"\"{(await line.Json())[0].GetProperty("rowVersion").GetInt64()}\"");
        edited.StatusCode.ShouldBe(HttpStatusCode.OK, await edited.Content.ReadAsStringAsync());
        await Age("OQ203_STALE_A", created: TimeSpan.FromDays(30));
        await Age("OQ203_STALE_B", created: TimeSpan.FromDays(30), edited: TimeSpan.FromDays(7) - TimeSpan.FromMinutes(5));
        (await (await other.Send(HttpMethod.Post, url, new { reason = "Abandoned import" })).Code()).ShouldBe("STATIC_DRAFT_NOT_STALE", "a line was edited less than 7 days ago");

        await Age("OQ203_STALE_B", created: TimeSpan.Zero, edited: TimeSpan.FromMinutes(10));
        var noReason = await other.Send(HttpMethod.Post, url);
        noReason.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await noReason.Content.ReadAsStringAsync());
        (await noReason.Json()).GetProperty("errors").TryGetProperty("reason", out _).ShouldBeTrue();

        var discarded = await other.Send(HttpMethod.Post, url, new { reason = "Abandoned import" });
        discarded.StatusCode.ShouldBe(HttpStatusCode.NoContent, await discarded.Content.ReadAsStringAsync());
        (await maker.GetAsync($"{Policies}/imports/{batch}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await _factory.DrainOutbox();
        var makerId = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM iam.user_account WHERE tenant_id = @t AND external_subject = 'maker-a'",
            ("t", DatabaseFixture.TenantA));
        var otherId = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM iam.user_account WHERE tenant_id = @t AND external_subject = 'checker-a'",
            ("t", DatabaseFixture.TenantA));
        (await fixture.Database.ScalarAsOwner<long>("""
            SELECT count(*) FROM audit.audit_event
            WHERE tenant_id = @t AND operation = 'DiscardStaleImport' AND actor_id = @actor AND details->>'importBatchId' = @b AND details->>'reason' = 'Abandoned import'
              AND details->>'makerId' = @maker AND details->>'daysUntouched' = '7' AND details->>'staleDraftDiscardDays' = '7'
            """, ("t", DatabaseFixture.TenantA), ("actor", otherId.ToString()), ("b", batch.ToString()), ("maker", makerId.ToString()))).ShouldBe(2L);
        (await ImportStatus(maker, batch)).ShouldBe("DISCARDED");

        var told = (await (await maker.GetAsync("/api/v1/workflow/tasks/mine?limit=100")).Json()).GetProperty("items").EnumerateArray()
            .Single(t => t.GetProperty("processCode").GetString() == "STATIC_DRAFT_DISCARDED" && t.GetProperty("businessRefId").GetGuid() == batch);
        (told.GetProperty("businessRefType").GetString(), told.GetProperty("informational").GetBoolean(), told.GetProperty("assignedToMe").GetBoolean())
            .ShouldBe(("STATIC_DATA_IMPORT", true, true));
        var notice = told.GetProperty("draftDiscarded");
        var batchNumber = (await ImportOf(maker, batch)).GetProperty("batchNumber").GetString();
        (notice.GetProperty("table").GetString(), notice.GetProperty("recordKey").GetString(), notice.GetProperty("importBatchId").GetGuid(), notice.GetProperty("lines").GetInt32())
            .ShouldBe(("iam.cfg_authentication_and_session_policies", batchNumber, batch, 2));
        (notice.GetProperty("discardedBy").GetGuid(), notice.GetProperty("reason").GetString()).ShouldBe((otherId, "Abandoned import"));
        await TheTaskLinksToTheTablesPage(maker, told, "/api/v1/identity/static/authentication-and-session-policies/pending");
    }

    /// <summary>OQ-203 item 1: once submitted, an import is not discarded (409 INVALID_STATE_TRANSITION); its approval decides it.</summary>
    [Fact]
    public async Task AnImportSubmitted_IsNotDiscarded_OQ203()
    {
        var maker = _factory.ClientFor(DatabaseFixture.TenantA, "maker-a");
        var batch = await Imported(maker, "OQ203_SUB_A");
        (await maker.Send(HttpMethod.Post, $"{Policies}/imports/{batch}/submit")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var refused = await maker.Send(HttpMethod.Post, $"{Policies}/imports/{batch}/discard");
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, await refused.Content.ReadAsStringAsync());
        (await refused.Code()).ShouldBe("INVALID_STATE_TRANSITION");
        (await (await maker.GetAsync($"{Policies}/imports/{batch}")).Json()).GetArrayLength().ShouldBe(1);
    }

    private static async Task<Guid> Imported(HttpClient maker, params string[] codes)
    {
        var imported = await maker.Send(HttpMethod.Post, $"{Policies}/imports", new { fileRef = "oq203.csv", lines = codes.Select(c => Policy(c)).ToArray() }, idempotent: true);
        imported.StatusCode.ShouldBe(HttpStatusCode.OK, await imported.Content.ReadAsStringAsync());
        return (await imported.Json()).GetProperty("batchId").GetGuid();
    }

    private static async Task<JsonElement> ImportOf(HttpClient reader, Guid batch)
    {
        string? cursor = null;
        do
        {
            var page = await (await reader.GetAsync($"/api/v1/static-data/imports?limit=200{(cursor is null ? "" : $"&cursor={cursor}")}")).Json();
            foreach (var item in page.GetProperty("items").EnumerateArray().Where(b => b.GetProperty("id").GetGuid() == batch))
            {
                return item;
            }

            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.String ? page.GetProperty("nextCursor").GetString() : null;
        }
        while (cursor is not null);
        throw new InvalidOperationException($"import {batch} is not listed");
    }

    private static async Task<string> ImportStatus(HttpClient reader, Guid batch) => (await ImportOf(reader, batch)).GetProperty("status").GetString()!.ToUpperInvariant();

    [Fact]
    public async Task Import_IsAllOrNothing_AndReportsEveryRejectedLine_SDM012()
    {
        var maker = _factory.ClientFor(DatabaseFixture.TenantA, "maker-a");
        var rejected = await maker.Send(HttpMethod.Post, $"{Roles}/imports", new
        {
            fileRef = "roles-bad.csv",
            lines = new object[] { Role("IMPORT_OK"), new { code = "IMPORT_BAD", changeReason = "import" }, new { code = "", roleCode = "X", permissions = Array.Empty<string>() } },
        }, idempotent: true);

        rejected.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var errors = (await rejected.Json()).GetProperty("errors");
        errors.TryGetProperty("line 2", out _).ShouldBeTrue();
        errors.TryGetProperty("line 3", out _).ShouldBeTrue();
        (await (await maker.GetAsync($"{Roles}/IMPORT_OK/versions")).Json()).GetArrayLength().ShouldBe(0);

        var committed = await maker.Send(HttpMethod.Post, $"{Roles}/imports", new { fileRef = "roles-good.csv", lines = new[] { Role("IMPORT_ONE"), Role("IMPORT_TWO") } },
            idempotent: true);
        committed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await committed.Json()).GetProperty("imported").GetInt32().ShouldBe(2);

        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM sdm.import_batch WHERE file_ref = 'roles-bad.csv'")).ShouldBe("REJECTED");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM sdm.import_error e JOIN sdm.import_batch b ON b.id = e.batch_id WHERE b.file_ref = 'roles-bad.csv'"))
            .ShouldBeGreaterThan(1);
        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM sdm.import_batch WHERE file_ref = 'roles-good.csv'")).ShouldBe("COMMITTED");
    }

    [Fact]
    public async Task AllStaticDataTables_AreReachableThroughTheGeneratedEndpoints()
    {
        // V0002's 252 tables, V0019's two Health tables (ADR-012), V0051's four Communications tables, V0052's five function catalogue tables and V0056's approval expiry,
        // less the payment release limits V0059 retired (OQ-112), V0060's six case tables, V0064's record retention policies (D-34), V0075's claim party roles (OQ-122), V0078's product billing frequencies V0092's five due diligence tables, V0094's Health service types and V0095's grace rules and short-period scales, and V0096's renewal review rules, claims experience factors and renewal settings, V0099's non-renewal reasons and V0100's retention offers.
        var descriptors = _factory.Services.GetServices<ModuleStaticDataTables>().SelectMany(m => m.Descriptors).ToList();
        // V0126: the appeal routes.
        descriptors.Count.ShouldBe(252 + 2 + 4 + 5 + 1 - 1 + 6 + 1 + 1 + 1 + 5 + 1 + 3 + 3 + 1 + 1 + 1 + 1 + 1 + 1 + 2 + 1 + 1 + 1);
        descriptors.Select(d => $"{d.Schema}.{d.Table}").Distinct().Count().ShouldBe(252 + 2 + 4 + 5 + 1 - 1 + 6 + 1 + 1 + 1 + 5 + 1 + 3 + 3 + 1 + 1 + 1 + 1 + 1 + 1 + 2 + 1 + 1 + 1);

        var reader = _factory.ClientFor(DatabaseFixture.TenantA, "maker-a");
        var failures = new List<string>();
        foreach (var descriptor in descriptors)
        {
            var list = await reader.GetAsync($"/api/v1/{descriptor.ModuleRoute}/static/{descriptor.EntityRoute}");
            var versions = await reader.GetAsync($"/api/v1/{descriptor.ModuleRoute}/static/{descriptor.EntityRoute}/any/versions");
            var pending = await reader.GetAsync($"/api/v1/{descriptor.ModuleRoute}/static/{descriptor.EntityRoute}/pending"); // DE-015
            if (list.StatusCode != HttpStatusCode.OK || versions.StatusCode != HttpStatusCode.OK || pending.StatusCode != HttpStatusCode.OK)
            {
                failures.Add($"{descriptor.Schema}.{descriptor.Table}: {(int)list.StatusCode}/{(int)versions.StatusCode}/{(int)pending.StatusCode}");
            }
        }

        failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task Tier2MakerChecker_CanBeSwitchedOffPerTenant_IsAudited_AndTier1StaysOn_SDM003()
    {
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantB, "admin-b",
            [.. All, StaticDataGovernancePermissions.SettingRead, StaticDataGovernancePermissions.SettingUpdate]);
        var admin = _factory.ClientFor(DatabaseFixture.TenantB, "admin-b");
        const string schedules = "/api/v1/identity/static/access-review-schedules";

        var defaults = await admin.GetAsync("/api/v1/static-data/settings");
        (await defaults.Json()).GetProperty("tier2MakerChecker").GetBoolean().ShouldBeTrue();
        defaults.ETag().ShouldBe("\"0\"");

        // On (the default): a Tier 2 change waits for a checker.
        var pending = await admin.Send(HttpMethod.Post, schedules, Schedule("QUARTERLY"), idempotent: true);
        await admin.Send(HttpMethod.Post, $"{schedules}/QUARTERLY/versions/1/submit", ifMatch: pending.ETag());
        await _factory.DrainOutbox();
        (await (await admin.GetAsync($"{schedules}/QUARTERLY/versions")).Json())[0].GetProperty("recordStatus").GetString().ShouldBe("SUBMITTED");

        // Switching it off is itself a Tier 1 change, approved by a second person in Workflow (OQ-046; see MakerCheckerTests).
        var switchedOff = await admin.Send(HttpMethod.Put, "/api/v1/static-data/settings/tier2-maker-checker", new { tier2MakerChecker = false }, ifMatch: "\"0\"");
        switchedOff.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("""
            SELECT count(*) FROM audit.audit_event
            WHERE tenant_id = @t AND operation = 'RequestTier2MakerCheckerOff' AND details->>'before' = 'True' AND details->>'after' = 'False'
            """, ("t", DatabaseFixture.TenantB))).ShouldBe(1);
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantB, "admin-b2", [WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove]);
        var approvalId = await fixture.Database.ScalarAsOwner<Guid>(
            "SELECT id FROM workflow.approval WHERE tenant_id = @t AND business_ref_type = 'GOVERNANCE_SETTING' AND status = 'REQUESTED'", ("t", DatabaseFixture.TenantB));
        (await _factory.ClientFor(DatabaseFixture.TenantB, "admin-b2").Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approvalId}/approve", ifMatch: "\"1\""))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();

        // Off: the next Tier 2 change is approved on submission, with no checker.
        var direct = await admin.Send(HttpMethod.Post, schedules, Schedule("MONTHLY"), idempotent: true);
        await admin.Send(HttpMethod.Post, $"{schedules}/MONTHLY/versions/1/submit", ifMatch: direct.ETag());
        await _factory.DrainOutbox();
        await _factory.DrainOutbox();
        (await (await admin.GetAsync($"{schedules}/MONTHLY/versions")).Json())[0].GetProperty("recordStatus").GetString().ShouldBe("APPROVED");

        // Tier 1 is always maker-checker.
        var role = await admin.Send(HttpMethod.Post, Roles, Role("TIER1_STILL"), idempotent: true);
        await admin.Send(HttpMethod.Post, $"{Roles}/TIER1_STILL/versions/1/submit", ifMatch: role.ETag());
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM sdm.change_request WHERE record_key = 'TIER1_STILL@1' AND tenant_id = @t",
            ("t", DatabaseFixture.TenantB))).ShouldBe("PENDING_APPROVAL");

        // Another tenant keeps the default.
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "settings-reader-a", StaticDataGovernancePermissions.SettingRead);
        (await (await _factory.ClientFor(DatabaseFixture.TenantA, "settings-reader-a").GetAsync("/api/v1/static-data/settings")).Json())
            .GetProperty("tier2MakerChecker").GetBoolean().ShouldBeTrue();
    }

    private static object Schedule(string code) =>
        new { code, scope = "PRIVILEGED", frequency = "P3M", reviewerRole = "AUDITOR", changeReason = "Test schedule" };

    // ADR-026: a role holds functions, so its definition carries no permissions; its default product scope stands in as a field to check.
    private static object Role(string code) => new { code, roleCode = code, scopeProducts = new[] { "LIFE-TERM" }, changeReason = "Test role" };

    private static object Policy(string code, int lockoutAttempts = 5) =>
        new { code, policyCode = code, mfaRequiredRoles = Array.Empty<string>(), sessionTimeout = "PT30M", lockoutAttempts, changeReason = "Test policy" };
}
