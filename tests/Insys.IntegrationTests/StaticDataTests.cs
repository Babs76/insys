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
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "maker-a", All);
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
        var byOther = await checker.Send(HttpMethod.Post, url, ifMatch: created.ETag());
        byOther.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await byOther.Code()).ShouldBe("STATIC_MAKER_ONLY");
        (await maker.Send(HttpMethod.Post, url)).StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
        (await maker.Send(HttpMethod.Post, url, ifMatch: "\"99\"")).StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);

        (await maker.Send(HttpMethod.Post, url, ifMatch: created.ETag())).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await maker.GetAsync($"{Policies}/DISCARD_POLICY/versions")).Json()).GetArrayLength().ShouldBe(0);
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE operation = 'DiscardDraft' AND target_id = @id", ("id", id.ToString())))
            .ShouldBe(1L);

        var again = await maker.Send(HttpMethod.Post, Policies, Policy("DISCARD_POLICY"), idempotent: true);
        again.StatusCode.ShouldBe(HttpStatusCode.Created, "the code is free for a new draft");
        (await again.Json()).GetProperty("version").GetInt32().ShouldBe(1);
        var submitted = await maker.Send(HttpMethod.Post, $"{Policies}/DISCARD_POLICY/versions/1/submit", ifMatch: again.ETag());
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK, await submitted.Content.ReadAsStringAsync());
        var notDraft = await maker.Send(HttpMethod.Post, url, ifMatch: submitted.ETag());
        notDraft.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await notDraft.Code()).ShouldBe("INVALID_STATE_TRANSITION");
    }

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
