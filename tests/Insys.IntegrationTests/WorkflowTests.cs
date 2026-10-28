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
using System.Text.Json;
using Insys.Testing;
using Insys.Workflow.Contracts;
using Insys.Workflow.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>Approval (SRS-002, WFL-004) and ProcessInstance (WFL-001 to WFL-003), through the API.</summary>
public sealed class WorkflowTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string Approvals = "/api/v1/workflow/approvals";
    private const string Processes = "/api/v1/workflow/processes";
    private static readonly string[] All =
    [
        WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove, WorkflowPermissions.ProcessRead, WorkflowPermissions.ProcessCreate,
        WorkflowPermissions.ProcessUpdate,
    ];

    private InsysApiFactory _factory = null!;
    private Guid _maker;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        var db = fixture.Database;
        var t = DatabaseFixture.TenantA;
        _maker = await db.UserWithPermissions(t, "wf-maker", All);
        var junior = await db.UserWithPermissions(t, "wf-junior", All);
        var senior = await db.UserWithPermissions(t, "wf-senior", All);
        await db.UserWithPermissions(t, "wf-none", All);
        var juniorRole = await db.RoleWithPermissions(t);
        var seniorRole = await db.RoleWithPermissions(t);
        await db.AssignRole(t, junior, juniorRole);
        await db.AssignRole(t, senior, seniorRole);
        await Authority(juniorRole, 1_000m);
        await Authority(seniorRole, 50_000m);

        await Static("cfg_workflow_definitions", "CLAIM", "process_code, states, transitions, domain_validation",
            "'CLAIM', ARRAY['Registered','Assessment','Approved','Declined'], " +
            "'[{\"from\":\"Registered\",\"to\":\"Assessment\"},{\"from\":\"Assessment\",\"to\":\"Approved\",\"when\":\"amount <= 10000\"},{\"from\":\"Assessment\",\"to\":\"Declined\"}]', ''");
        await Static("cfg_workflow_definitions", "CHECKED", "process_code, states, transitions, domain_validation",
            "'CHECKED', ARRAY['Open','Closed'], '[\"Open → Closed\"]', 'NO_SUCH_SERVICE'");
        await Static("cfg_task_types_and_work_queues", "CLAIMS_Q", "queue_code, task_types, assignment_rule", "'CLAIMS_Q', ARRAY['REGISTERED','ASSESSMENT'], 'MANUAL_PULL'");
        await Static("cfg_sla_definitions", "SLA_REG", "sla_code, task_type, duration, calendar, breach_action", "'SLA_REG', 'REGISTERED', interval 'PT0S', 'STANDARD', 'ESCALATE'");
        await Static("cfg_sla_definitions", "SLA_ASS", "sla_code, task_type, duration, calendar, breach_action", "'SLA_ASS', 'ASSESSMENT', interval 'P2D', 'STANDARD', 'ESCALATE'");
        await Static("cfg_escalation_rules", "ESC1", "rule_code, trigger, level, target_role", "'ESC1', 'SLA_BREACH', 1, 'SUPERVISORS'");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Approval_IsDecidedBySomeoneElse_WithinTheirAuthority_WFL004()
    {
        var id = await SeedApproval(amount: 5_000m);
        var url = $"{Approvals}/{id}";

        await Refused(_factory.ClientFor(DatabaseFixture.TenantA, "wf-maker"), url, "MAKER_CHECKER_SAME_PERSON");
        await Refused(_factory.ClientFor(DatabaseFixture.TenantA, "wf-none"), url, "APPROVAL_AUTHORITY_MISSING");
        await Refused(_factory.ClientFor(DatabaseFixture.TenantA, "wf-junior"), url, "APPROVAL_AUTHORITY_EXCEEDED");

        var senior = _factory.ClientFor(DatabaseFixture.TenantA, "wf-senior");
        var approved = await senior.Send(HttpMethod.Post, $"{url}/approve", ifMatch: "\"1\"");
        approved.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var view = await (await senior.GetAsync(url)).Json();
        view.GetProperty("status").GetString().ShouldBe("Approved");
        approved.Headers.ETag!.Tag.ShouldBe("\"2\"");

        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM workflow.outbox_event WHERE event_type = 'workflow.ApprovalGranted' AND aggregate_id = @id",
            ("id", id))).ShouldBe(1);
    }

    [Fact]
    public async Task Approval_IsNotVisibleToAnotherTenant()
    {
        var id = await SeedApproval(amount: null);
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantB, "wf-other", All);
        (await _factory.ClientFor(DatabaseFixture.TenantB, "wf-other").GetAsync($"{Approvals}/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Process_MovesAlongItsDefinition_AndRefusesAFailedGuard_WFL001()
    {
        var client = _factory.ClientFor(DatabaseFixture.TenantA, "wf-maker");
        var (url, taskId) = await Start(client, "CLAIM");
        (await Transitions(client, url)).ShouldBe([("Assessment", false)], "BR-028: the transitions from the current state");

        var notOffered = await client.Send(HttpMethod.Post, $"{url}/tasks/{taskId}/complete", new { toState = "Approved", variables = new { amount = 1 } }, ifMatch: "\"1\"");
        notOffered.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await notOffered.Code()).ShouldBe("TRANSITION_NOT_DEFINED", "a transition not offered is refused");
        (await client.Send(HttpMethod.Post, $"{url}/tasks/{taskId}/complete", new { toState = "Assessment" }, ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var process = await (await client.GetAsync(url)).Json();
        var assessment = process.GetProperty("tasks").EnumerateArray().Single(t => t.GetProperty("status").GetString() == "Open");
        assessment.GetProperty("taskType").GetString().ShouldBe("ASSESSMENT");
        assessment.GetProperty("slaCode").GetString().ShouldBe("SLA_ASS");
        var next = assessment.GetProperty("id").GetGuid();
        (await Transitions(client, url)).ShouldBe([("Approved", true), ("Declined", false)], "Approved has a guard");

        var guardFailed = await client.Send(HttpMethod.Post, $"{url}/tasks/{next}/complete", new { toState = "Approved", variables = new { amount = 25_000 } },
            ifMatch: "\"2\"");
        (await guardFailed.Code()).ShouldBe("TRANSITION_GUARD_FAILED");
        (await client.Send(HttpMethod.Post, $"{url}/tasks/{next}/complete", new { toState = "Approved", variables = new { amount = 500 } }, ifMatch: "\"2\""))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        process = await (await client.GetAsync(url)).Json();
        process.GetProperty("status").GetString().ShouldBe("Completed");
        process.GetProperty("state").GetString().ShouldBe("Approved");
        process.GetProperty("processVersion").GetInt32().ShouldBe(1);
        (await Transitions(client, url)).ShouldBeEmpty("a completed process offers none");
    }

    /// <summary>BR-028: open tasks page with a cursor, earliest due first, every task once.</summary>
    [Fact]
    public async Task OpenTasks_PageEarliestDueFirst_EveryTaskOnce_BR028()
    {
        var client = _factory.ClientFor(DatabaseFixture.TenantA, "wf-maker");
        var started = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            started.Add((await Start(client, "CLAIM")).TaskId);
        }

        var seen = new List<(DateTimeOffset DueAt, Guid Id)>();
        string? cursor = null;
        for (var page = 0; page < 50; page++)
        {
            var response = await client.GetAsync($"/api/v1/workflow/tasks?queueCode=CLAIMS_Q&limit=2{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}");
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var body = await response.Json();
            seen.AddRange(body.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("task"))
                .Select(t => (t.GetProperty("dueAt").GetDateTimeOffset(), t.GetProperty("id").GetGuid())));
            if (body.GetProperty("nextCursor").ValueKind == System.Text.Json.JsonValueKind.Null)
            {
                break;
            }

            cursor = body.GetProperty("nextCursor").GetString();
        }

        seen.Select(t => t.Id).ShouldBeUnique();
        started.ShouldAllBe(id => seen.Any(t => t.Id == id));
        seen.ShouldBe([.. seen.OrderBy(t => t.DueAt).ThenBy(t => t.Id)], "earliest due first");
        (await client.GetAsync("/api/v1/workflow/tasks?cursor=not-a-cursor")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    private static async Task<List<(string, bool)>> Transitions(HttpClient client, string url) =>
        [.. (await (await client.GetAsync(url)).Json()).GetProperty("allowedTransitions").EnumerateArray()
            .Select(t => (t.GetProperty("toState").GetString()!, t.GetProperty("conditional").GetBoolean()))];

    [Fact]
    public async Task Transition_WithoutTheOwningDomainsValidation_IsRefused_WFL002()
    {
        var client = _factory.ClientFor(DatabaseFixture.TenantA, "wf-maker");
        var (url, taskId) = await Start(client, "CHECKED");
        var refused = await client.Send(HttpMethod.Post, $"{url}/tasks/{taskId}/complete", new { toState = "Closed" }, ifMatch: "\"1\"");
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.Code()).ShouldBe("DOMAIN_VALIDATION_FAILED");
    }

    [Fact]
    public async Task BreachedSla_EscalatesTheTask_AndSurvivesInTheDatabase_WFL003_WFL005()
    {
        var client = _factory.ClientFor(DatabaseFixture.TenantA, "wf-maker");
        var (url, taskId) = await Start(client, "CLAIM");

        // The timer job runs as a service identity per tenant; the breach is handled through the outbox.
        var (_, fired) = await _factory.Services.GetRequiredService<WorkflowJobs>().Run(CancellationToken.None);
        fired.ShouldBeGreaterThanOrEqualTo(1);
        await _factory.DrainOutbox();

        var task = (await (await client.GetAsync(url)).Json()).GetProperty("tasks").EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == taskId);
        task.GetProperty("queueCode").GetString().ShouldBe("SUPERVISORS");
        task.GetProperty("outcome").GetString().ShouldBe("ESCALATED_L1");
        var notes = (await (await client.GetAsync(url)).Json()).GetProperty("notes");
        notes.GetArrayLength().ShouldBe(1);

        var queue = await (await client.GetAsync("/api/v1/workflow/tasks?queueCode=SUPERVISORS")).Json();
        queue.GetProperty("items").EnumerateArray().ShouldContain(t => t.GetProperty("task").GetProperty("id").GetGuid() == taskId);
    }

    [Fact]
    public async Task Task_IsAssigned_AndTheProcessSuspendsAndResumes_WFL003()
    {
        var client = _factory.ClientFor(DatabaseFixture.TenantA, "wf-maker");
        var (url, taskId) = await Start(client, "CLAIM");
        (await client.Send(HttpMethod.Post, $"{url}/tasks/{taskId}/assign", new { assigneeId = (Guid?)null }, ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.Send(HttpMethod.Post, $"{url}/suspend", ifMatch: "\"2\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.Send(HttpMethod.Post, $"{url}/tasks/{taskId}/complete", new { toState = "Assessment" }, ifMatch: "\"3\"")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.Send(HttpMethod.Post, $"{url}/resume", ifMatch: "\"3\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var process = await (await client.GetAsync(url)).Json();
        process.GetProperty("status").GetString().ShouldBe("Running");
        process.GetProperty("tasks")[0].GetProperty("assigneeId").GetGuid().ShouldBe(_maker);
    }

    /// <summary>
    /// F103 (addenda/access-scopes.md §4): an approval about a scoped record is seen and decided only by a checker whose
    /// scope covers that record; approvals about unscoped things (static data, say) are unaffected.
    /// </summary>
    [Fact]
    public async Task ScopedApproval_IsSeenAndDecidedOnlyWithinTheCheckersScope_F103()
    {
        var db = fixture.Database;
        var checker = await db.UserWithPermissions(DatabaseFixture.TenantA, "wf-home-only", All);
        var authorityRole = await db.RoleWithPermissions(DatabaseFixture.TenantA);
        await db.AssignRole(DatabaseFixture.TenantA, checker, authorityRole);
        await Authority(authorityRole, 50_000m);
        await db.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_products = ARRAY['GEN-HOME'] WHERE user_id = @u", ("u", checker));
        var outside = await SeedApproval(100m);
        var inside = await SeedApproval(100m);
        var unscoped = await SeedApproval(100m);
        foreach (var (approval, product) in new[] { (outside, "GEN-MOTOR"), (inside, "GEN-HOME") })
        {
            await db.ExecuteAsOwner("UPDATE workflow.approval SET is_scoped = true, product_code = @p, org_node_code = 'BR-A' WHERE id = @a", ("p", product), ("a", approval));
        }

        var client = _factory.ClientFor(DatabaseFixture.TenantA, "wf-home-only");
        (await client.GetAsync($"{Approvals}/{outside}")).StatusCode.ShouldBe(HttpStatusCode.NotFound, "an approval about a record outside the scope is not revealed");
        (await client.GetAsync($"{Approvals}/{inside}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync($"{Approvals}/{unscoped}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        foreach (var (approval, shown) in new[] { (outside, 0), (inside, 1), (unscoped, 1) })
        {
            var businessRef = await db.ScalarAsOwner<Guid>("SELECT business_ref_id FROM workflow.approval WHERE id = @a", ("a", approval));
            (await (await client.GetAsync($"{Approvals}?businessRefId={businessRef}")).Json()).GetProperty("items").GetArrayLength().ShouldBe(shown, "lists show only approvals in scope");
        }

        await Refused(client, $"{Approvals}/{outside}", "OUT_OF_SCOPE");
        (await client.Send(HttpMethod.Post, $"{Approvals}/{inside}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private async Task<(string Url, Guid TaskId)> Start(HttpClient client, string processCode)
    {
        var started = await client.Send(HttpMethod.Post, Processes, new { processCode, businessRefType = "CLAIM", businessRefId = Guid.CreateVersion7() }, idempotent: true);
        started.StatusCode.ShouldBe(HttpStatusCode.Created);
        var url = $"{Processes}/{(await started.Json()).GetProperty("id").GetGuid()}";
        var process = await (await client.GetAsync(url)).Json();
        return (url, process.GetProperty("tasks")[0].GetProperty("id").GetGuid());
    }

    private static async Task Refused(HttpClient client, string url, string code)
    {
        var response = await client.Send(HttpMethod.Post, $"{url}/approve", ifMatch: "\"1\"");
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Code()).ShouldBe(code);
    }

    /// <summary>BR-001: an approval can carry the checker's reason, kept as its decision reason like a rejection's.</summary>
    [Fact]
    public async Task Approving_WithAReason_KeepsItOnTheApproval_BR001()
    {
        var id = await SeedApproval(amount: 100m);
        var senior = _factory.ClientFor(DatabaseFixture.TenantA, "wf-senior");
        (await senior.Send(HttpMethod.Post, $"{Approvals}/{id}/approve", new { reason = "Within the claim's documented loss" }, ifMatch: "\"1\""))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await senior.GetAsync($"{Approvals}/{id}")).Json()).GetProperty("decisionReason").GetString().ShouldBe("Within the claim's documented loss");

        // The body stays optional.
        var plain = await SeedApproval(amount: 100m);
        (await senior.Send(HttpMethod.Post, $"{Approvals}/{plain}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>BR-002: the signed-in user's own authority, per approval type, with its limit and currency.</summary>
    [Fact]
    public async Task MyAuthority_IsTheSignedInUsersOwn_BR002()
    {
        var response = await _factory.ClientFor(DatabaseFixture.TenantA, "wf-junior").GetAsync($"{Approvals}/my-authority");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var rows = (await response.Json()).EnumerateArray().ToList();
        // Each test's set-up gives the junior another role with the same authority, so there is a row per role.
        var limits = rows.Where(r => r.GetProperty("approvalType").GetString() == "CLAIM_PAYMENT").Select(r => r.GetProperty("limit").GetDecimal()).ToList();
        limits.ShouldNotBeEmpty();
        limits.ShouldAllBe(l => l == 1_000m);
        (await (await _factory.ClientFor(DatabaseFixture.TenantA, "wf-none").GetAsync($"{Approvals}/my-authority")).Json()).EnumerateArray().ShouldBeEmpty();
    }

    /// <summary>
    /// BR-004: decidable=true lists only what the caller can decide now (not their own, within their authority and limit),
    /// with the API's own cursor paging.
    /// </summary>
    [Fact]
    public async Task DecidableApprovals_AreOnlyThoseTheCallerCanDecideNow_WithPaging_BR004()
    {
        const string refType = "BR004_CLAIM";
        var small = await SeedApproval(500m, refType);
        var large = await SeedApproval(5_000m, refType);
        var juniorsOwn = await SeedApproval(500m, refType, await fixture.Database.ScalarAsOwner<Guid>(
            "SELECT id FROM iam.user_account WHERE tenant_id = @t AND username = 'wf-junior'", ("t", DatabaseFixture.TenantA)));

        async Task<List<Guid>> Decidable(string user, int limit)
        {
            var client = _factory.ClientFor(DatabaseFixture.TenantA, user);
            var ids = new List<Guid>();
            string? cursor = null;
            do
            {
                var page = await (await client.GetAsync($"{Approvals}?decidable=true&businessRefType={refType}&limit={limit}{(cursor is null ? "" : $"&cursor={cursor}")}")).Json();
                ids.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
                cursor = page.GetProperty("nextCursor").GetString();
            }
            while (cursor is not null);
            return ids;
        }

        (await Decidable("wf-junior", 10)).ShouldBe([small]);
        (await Decidable("wf-senior", 1)).ShouldBe([small, large, juniorsOwn]);
        (await Decidable("wf-none", 10)).ShouldBeEmpty();
        (await Decidable("wf-maker", 10)).ShouldBeEmpty();
    }

    private async Task<Guid> SeedApproval(decimal? amount, string businessRefType = "CLAIM", Guid? maker = null)
    {
        var id = Guid.CreateVersion7();
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO workflow.approval (tenant_id, id, created_at, created_by, business_ref_type, business_ref_id, approval_type, maker_id,
                                           authority_limit, currency, reason, status, requested_at, expires_at, row_version, updated_at, updated_by)
            VALUES (@t, @id, now(), 'seed', @ref, gen_random_uuid(), 'CLAIM_PAYMENT', @maker, @amount, 'ZAR', 'Pay', 'REQUESTED', now(), now() + interval '1 day', 1, now(), 'seed')
            """, ("t", DatabaseFixture.TenantA), ("id", id), ("ref", businessRefType), ("maker", maker ?? _maker), ("amount", (object?)amount ?? DBNull.Value));
        return id;
    }

    private Task Authority(string role, decimal limit) =>
        Static("cfg_delegation_and_approval_authority", role, "role_code, transaction_type, limit_value, delegation_allowed", $"'{role}', 'CLAIM_PAYMENT', {limit}, false");

    private Task Static(string table, string code, string columns, string values) =>
        fixture.Database.ExecuteAsOwner($"""
            INSERT INTO workflow.{table} (tenant_id, code, version, effective_from, record_status, created_by, change_reason, {columns})
            VALUES (@t, @code, 1, current_date - 1, 'EFFECTIVE', 'seed', 'test seed', {values})
            ON CONFLICT DO NOTHING
            """, ("t", DatabaseFixture.TenantA), ("code", code));
}
