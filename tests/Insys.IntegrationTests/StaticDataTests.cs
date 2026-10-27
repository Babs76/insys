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
        // less the payment release limits V0059 retired (OQ-112), V0060's six case tables, and V0064's record retention policies (D-34).
        var descriptors = _factory.Services.GetServices<ModuleStaticDataTables>().SelectMany(m => m.Descriptors).ToList();
        descriptors.Count.ShouldBe(252 + 2 + 4 + 5 + 1 - 1 + 6 + 1);
        descriptors.Select(d => $"{d.Schema}.{d.Table}").Distinct().Count().ShouldBe(252 + 2 + 4 + 5 + 1 - 1 + 6 + 1);

        var reader = _factory.ClientFor(DatabaseFixture.TenantA, "maker-a");
        var failures = new List<string>();
        foreach (var descriptor in descriptors)
        {
            var list = await reader.GetAsync($"/api/v1/{descriptor.ModuleRoute}/static/{descriptor.EntityRoute}");
            var versions = await reader.GetAsync($"/api/v1/{descriptor.ModuleRoute}/static/{descriptor.EntityRoute}/any/versions");
            if (list.StatusCode != HttpStatusCode.OK || versions.StatusCode != HttpStatusCode.OK)
            {
                failures.Add($"{descriptor.Schema}.{descriptor.Table}: {(int)list.StatusCode}/{(int)versions.StatusCode}");
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
