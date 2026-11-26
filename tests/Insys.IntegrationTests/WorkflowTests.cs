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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts.Approvals;
using Insys.Life.Contracts;
using Insys.StaticData.Contracts;
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

    /// <summary>BR-170: a process is about a record type the contract lists (ProcessRecordTypes); any other is refused before it starts.</summary>
    [Fact]
    public async Task StartingAProcess_AboutAnUndeclaredRecordType_IsRefused_BR170()
    {
        var client = _factory.ClientFor(DatabaseFixture.TenantA, "wf-maker");
        var refused = await client.Send(HttpMethod.Post, Processes, new { processCode = "CLAIM", businessRefType = "CLAIM", businessRefId = Guid.CreateVersion7() },
            idempotent: true);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await refused.Json();
        problem.GetProperty("code").GetString().ShouldBe("VALIDATION_FAILED");
        problem.GetProperty("errors").TryGetProperty("businessRefType", out _).ShouldBeTrue(problem.ToString());
    }

    /// <summary>
    /// Ade, 26 November 2026 (BR-170): a process stored with a record type that is no longer declared (started through the API before the narrowing)
    /// shows UNDECLARED, with no screen, and the stored value in storedBusinessRefType, in the process view, the process list and the my-task view; the
    /// stored row is unchanged, and the list filter finds it by its stored value and by UNDECLARED.
    /// </summary>
    [Fact]
    public async Task AProcessStoredWithAnUndeclaredType_IsShownAsUndeclared_WithItsStoredType_BR170()
    {
        var db = fixture.Database;
        var worker = await db.UserWithPermissions(DatabaseFixture.TenantA, "wf-refs", [.. All, WorkflowPermissions.OwnTaskRead]);
        var client = _factory.ClientFor(DatabaseFixture.TenantA, "wf-refs");
        var (url, taskId) = await Start(client, "CLAIM");
        var processId = Guid.Parse(url[(url.LastIndexOf('/') + 1)..]);
        await db.ExecuteAsOwner("UPDATE workflow.task SET assignee_id = @u WHERE id = @t", ("u", worker), ("t", taskId));

        var declared = await (await client.GetAsync(url)).Json();
        declared.GetProperty("businessRefType").GetString().ShouldBe("LIFE_CLAIM");
        declared.GetProperty("storedBusinessRefType").ValueKind.ShouldBe(JsonValueKind.Null, "a declared type is shown as stored");
        var declaredTask = await (await client.GetAsync($"/api/v1/workflow/tasks/mine/{taskId}")).Json();
        declaredTask.GetProperty("storedBusinessRefType").ValueKind.ShouldBe(JsonValueKind.Null);
        declaredTask.GetProperty("screen").GetString().ShouldNotBeNull();

        var stored = $"OLD_{Guid.NewGuid():N}"[..20];
        await db.ExecuteAsOwner("UPDATE workflow.process_instance SET business_ref_type = @s WHERE id = @p", ("s", stored), ("p", processId));

        var process = await (await client.GetAsync(url)).Json();
        process.GetProperty("businessRefType").GetString().ShouldBe("UNDECLARED");
        process.GetProperty("storedBusinessRefType").GetString().ShouldBe(stored);

        var byStored = (await (await client.GetAsync($"{Processes}?businessRefType={stored}")).Json()).GetProperty("items").EnumerateArray().ToList();
        byStored.Select(p => p.GetProperty("id").GetGuid()).ShouldBe([processId], "a filter by the stored value still finds it");
        byStored[0].GetProperty("businessRefType").GetString().ShouldBe("UNDECLARED");
        byStored[0].GetProperty("storedBusinessRefType").GetString().ShouldBe(stored);
        var byShown = (await (await client.GetAsync($"{Processes}?businessRefType=UNDECLARED&limit=200")).Json()).GetProperty("items").EnumerateArray().ToList();
        byShown.Select(p => p.GetProperty("id").GetGuid()).ShouldContain(processId, "a filter by the type its view shows finds it");
        byShown.ShouldAllBe(p => p.GetProperty("businessRefType").GetString() == "UNDECLARED");
        (await (await client.GetAsync($"{Processes}?businessRefType=LIFE_CLAIM&businessRefId={process.GetProperty("businessRefId").GetGuid()}")).Json())
            .GetProperty("items").GetArrayLength().ShouldBe(0, "it is no longer of the declared type");

        var task = await (await client.GetAsync($"/api/v1/workflow/tasks/mine/{taskId}")).Json();
        task.GetProperty("businessRefType").GetString().ShouldBe("UNDECLARED");
        task.GetProperty("storedBusinessRefType").GetString().ShouldBe(stored);
        task.GetProperty("screen").ValueKind.ShouldBe(JsonValueKind.Null, "UNDECLARED opens no screen");
        var mine = (await (await client.GetAsync("/api/v1/workflow/tasks/mine?limit=200")).Json()).GetProperty("items").EnumerateArray()
            .Single(t => t.GetProperty("id").GetGuid() == taskId);
        mine.GetProperty("businessRefType").GetString().ShouldBe("UNDECLARED");
        mine.GetProperty("storedBusinessRefType").GetString().ShouldBe(stored);
        mine.GetProperty("screen").ValueKind.ShouldBe(JsonValueKind.Null);

        (await db.ScalarAsOwner<string>("SELECT business_ref_type FROM workflow.process_instance WHERE id = @p", ("p", processId))).ShouldBe(stored,
            "the stored value is unchanged");
    }

    /// <summary>
    /// Ade, 26 November 2026 (BR-170 decision 2): a process stored with a value matching a declared type once trimmed and ignoring letter case
    /// (" LIFE_claim ") shows that declared type, with its screen, and the stored value in storedBusinessRefType, in the process view, the process list
    /// and the my-task view; the list filter by the declared type finds it, a filter by the stored value matches exactly, and the stored row is unchanged.
    /// </summary>
    [Fact]
    public async Task AProcessStoredWithAFoldedDeclaredType_IsShownAsThatType_WithItsScreen_AndFoundByIt_BR170()
    {
        var db = fixture.Database;
        var worker = await db.UserWithPermissions(DatabaseFixture.TenantA, "wf-fold", [.. All, WorkflowPermissions.OwnTaskRead]);
        var client = _factory.ClientFor(DatabaseFixture.TenantA, "wf-fold");
        var (url, taskId) = await Start(client, "CLAIM");
        var processId = Guid.Parse(url[(url.LastIndexOf('/') + 1)..]);
        await db.ExecuteAsOwner("UPDATE workflow.task SET assignee_id = @u WHERE id = @t", ("u", worker), ("t", taskId));
        var screen = (await (await client.GetAsync($"/api/v1/workflow/tasks/mine/{taskId}")).Json()).GetProperty("screen").GetString();
        screen.ShouldNotBeNull();

        const string stored = " LIFE_claim ";
        await db.ExecuteAsOwner("UPDATE workflow.process_instance SET business_ref_type = @s WHERE id = @p", ("s", stored), ("p", processId));

        var process = await (await client.GetAsync(url)).Json();
        process.GetProperty("businessRefType").GetString().ShouldBe("LIFE_CLAIM");
        process.GetProperty("storedBusinessRefType").GetString().ShouldBe(stored);
        var businessRefId = process.GetProperty("businessRefId").GetGuid();

        var byDeclared = (await (await client.GetAsync($"{Processes}?businessRefType=LIFE_CLAIM&businessRefId={businessRefId}")).Json())
            .GetProperty("items").EnumerateArray().ToList();
        byDeclared.Select(p => p.GetProperty("id").GetGuid()).ShouldBe([processId], "a filter by the declared type finds the values that fold to it");
        byDeclared[0].GetProperty("businessRefType").GetString().ShouldBe("LIFE_CLAIM");
        byDeclared[0].GetProperty("storedBusinessRefType").GetString().ShouldBe(stored);
        var byStored = (await (await client.GetAsync($"{Processes}?businessRefType={Uri.EscapeDataString(stored)}&businessRefId={businessRefId}")).Json())
            .GetProperty("items").EnumerateArray().ToList();
        byStored.Select(p => p.GetProperty("id").GetGuid()).ShouldBe([processId], "a filter by the stored value finds it exactly");
        (await (await client.GetAsync($"{Processes}?businessRefType=life_claim&businessRefId={businessRefId}")).Json())
            .GetProperty("items").GetArrayLength().ShouldBe(0, "a filter by an undeclared value matches exactly: life_claim is not the stored value");
        (await (await client.GetAsync($"{Processes}?businessRefType=UNDECLARED&businessRefId={businessRefId}")).Json())
            .GetProperty("items").GetArrayLength().ShouldBe(0, "a folded value is not shown as UNDECLARED");
        (await (await client.GetAsync($"{Processes}?businessRefType=GENERAL_CLAIM&businessRefId={businessRefId}")).Json())
            .GetProperty("items").GetArrayLength().ShouldBe(0, "it folds only to its own type");

        var task = await (await client.GetAsync($"/api/v1/workflow/tasks/mine/{taskId}")).Json();
        task.GetProperty("businessRefType").GetString().ShouldBe("LIFE_CLAIM");
        task.GetProperty("storedBusinessRefType").GetString().ShouldBe(stored);
        task.GetProperty("screen").GetString().ShouldBe(screen, "the declared type's screen");
        var mine = (await (await client.GetAsync("/api/v1/workflow/tasks/mine?limit=200")).Json()).GetProperty("items").EnumerateArray()
            .Single(t => t.GetProperty("id").GetGuid() == taskId);
        mine.GetProperty("businessRefType").GetString().ShouldBe("LIFE_CLAIM");
        mine.GetProperty("storedBusinessRefType").GetString().ShouldBe(stored);
        mine.GetProperty("screen").GetString().ShouldBe(screen);

        (await db.ScalarAsOwner<string>("SELECT business_ref_type FROM workflow.process_instance WHERE id = @p", ("p", processId))).ShouldBe(stored,
            "the stored value is unchanged");
    }

    /// <summary>
    /// Ade, 26 November 2026 (BR-170 decision 2): a stored value that folds to UNDECLARED itself (" undeclared ") stays UNDECLARED, with no screen, and
    /// the list filter by UNDECLARED finds it.
    /// </summary>
    [Fact]
    public async Task AProcessStoredWithAValueFoldingToUndeclared_StaysUndeclared_BR170()
    {
        var db = fixture.Database;
        var worker = await db.UserWithPermissions(DatabaseFixture.TenantA, "wf-fold-undeclared", [.. All, WorkflowPermissions.OwnTaskRead]);
        var client = _factory.ClientFor(DatabaseFixture.TenantA, "wf-fold-undeclared");
        var (url, taskId) = await Start(client, "CLAIM");
        var processId = Guid.Parse(url[(url.LastIndexOf('/') + 1)..]);
        await db.ExecuteAsOwner("UPDATE workflow.task SET assignee_id = @u WHERE id = @t", ("u", worker), ("t", taskId));
        const string stored = " undeclared ";
        await db.ExecuteAsOwner("UPDATE workflow.process_instance SET business_ref_type = @s WHERE id = @p", ("s", stored), ("p", processId));

        var process = await (await client.GetAsync(url)).Json();
        process.GetProperty("businessRefType").GetString().ShouldBe("UNDECLARED");
        process.GetProperty("storedBusinessRefType").GetString().ShouldBe(stored);
        var businessRefId = process.GetProperty("businessRefId").GetGuid();
        (await (await client.GetAsync($"{Processes}?businessRefType=UNDECLARED&businessRefId={businessRefId}")).Json())
            .GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ShouldBe([processId]);

        var task = await (await client.GetAsync($"/api/v1/workflow/tasks/mine/{taskId}")).Json();
        task.GetProperty("businessRefType").GetString().ShouldBe("UNDECLARED");
        task.GetProperty("storedBusinessRefType").GetString().ShouldBe(stored);
        task.GetProperty("screen").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>Ade, 26 November 2026 (BR-170): UNDECLARED is only shown; starting a process about it is refused like any undeclared type.</summary>
    [Fact]
    public async Task StartingAProcess_AboutUndeclared_IsRefused_BR170()
    {
        var client = _factory.ClientFor(DatabaseFixture.TenantA, "wf-maker");
        var refused = await client.Send(HttpMethod.Post, Processes, new { processCode = "CLAIM", businessRefType = "UNDECLARED", businessRefId = Guid.CreateVersion7() },
            idempotent: true);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await refused.Json();
        problem.GetProperty("code").GetString().ShouldBe("VALIDATION_FAILED");
        problem.GetProperty("errors").TryGetProperty("businessRefType", out _).ShouldBeTrue(problem.ToString());
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

    /// <summary>
    /// F103, OQ-176: every business reference type a module declares has a scope source in its module, or is declared unscoped with its reason (a party,
    /// a user, static data). A new type with neither fails here before it can reach a branch-limited checker unscoped.
    /// </summary>
    [Fact]
    public void EveryApprovalReferenceType_HasAScopeSource_OrIsDeclaredUnscoped_F103_OQ176()
    {
        var sources = _factory.Services.GetServices<IApprovalScopeSource>().ToList();
        ApprovalKinds.BusinessRefTypes.Where(t => !sources.Any(s => s.Covers(t)) && !ApprovalKinds.UnscopedTypes.ContainsKey(t))
            .ShouldBeEmpty("each needs a scope source, or ApprovalKinds.Unscoped with its reason");
        ApprovalKinds.UnscopedTypes.Keys.Where(t => sources.Any(s => s.Covers(t))).ShouldBeEmpty("a type is either scoped by a source or declared unscoped, not both");
    }

    /// <summary>
    /// F103, OQ-176 (fail closed): an approval about a record whose scope can't be settled (a refund across accounts, a provider's recovery, a record
    /// not found) is scoped to no product or node, so only a checker with unrestricted scope sees and decides it. OQ-173: a product version's approval
    /// carries its product, judged by product only. A type declared unscoped (static data) stays unscoped.
    /// </summary>
    [Fact]
    public async Task ApprovalsWhoseScopeCantBeSettled_AreForUnrestrictedCheckersOnly_AndAProductVersionsByItsProduct_F103_OQ173_OQ176()
    {
        var db = fixture.Database;
        var product = "SCP-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var version = Guid.CreateVersion7();
        await db.ExecuteAsOwner("""
            INSERT INTO product.cfg_product_definitions_and_versions (tenant_id, id, code, version, effective_from, record_status, created_by, change_reason,
                                                                     product_code, domain, name, coverages, family_code, class_code, cover_starts_on)
            VALUES (@t, @id, @p, 1, DATE '2026-01-01', 'DRAFT', 'seed', 'scope test', @p, 'GENERAL', 'Scope test', ARRAY['OWN_DAMAGE'], 'GEN-MOTOR', 'MOTOR', 'ISSUE')
            """, ("t", DatabaseFixture.TenantA), ("id", version), ("p", product));

        var failClosed = new (string RefType, string ApprovalType)[]
        {
            (Billing.Contracts.BillingApprovals.RefundBusinessRefType, Billing.Contracts.BillingApprovals.RefundApprovalType),
            (Payments.Contracts.PaymentApprovals.PaymentBusinessRefType, Payments.Contracts.PaymentApprovals.UnknownResolutionApprovalType),
            (CaseApprovals.RedressBusinessRefType, CaseApprovals.RedressApprovalType),
            (Documents.Contracts.DocumentApprovals.LegalHoldBusinessRefType, Documents.Contracts.DocumentApprovals.LegalHoldReleaseType),
            (Documents.Contracts.DocumentApprovals.RecordHoldBusinessRefType, Documents.Contracts.DocumentApprovals.LegalHoldReleaseType),
            (General.Contracts.GeneralApprovals.NonRenewalBusinessRefType, General.Contracts.GeneralApprovals.NonRenewalApprovalType),
            (Health.Contracts.HealthApprovals.RecoveryBusinessRefType, Health.Contracts.HealthApprovals.RecoveryWriteOffApprovalType),
            (Health.Contracts.HealthApprovals.CreditNoteBusinessRefType, Health.Contracts.HealthApprovals.CreditNoteApprovalType),
            (ProductFactory.Contracts.ProductApprovals.BusinessRefType, ProductFactory.Contracts.ProductApprovals.ApprovalType),
        };
        foreach (var (refType, approvalType) in failClosed)
        {
            // None of these records exists, so none has a scope to give: each is held for unrestricted checkers.
            (await Requested(refType, Guid.CreateVersion7(), approvalType)).ShouldBe("True||", $"{refType} fails closed");
        }

        (await Requested(ProductFactory.Contracts.ProductApprovals.BusinessRefType, version, ProductFactory.Contracts.ProductApprovals.ApprovalType))
            .ShouldBe($"True|{product}||False", "a product version's approval is scoped by its product alone");
        (await Requested(StaticDataApprovals.BusinessRefType, Guid.CreateVersion7(), StaticDataApprovals.ApprovalType(1))).ShouldBe("False||", "static data is declared unscoped");

        // A refund is paid from the whole account: one scope when every schedule shares it, otherwise unrestricted checkers only.
        var rows = new RowFiller(db, DateTimeOffset.UtcNow);
        async Task<Guid> Refund(params string[] products)
        {
            var account = await rows.Insert(DatabaseFixture.TenantA, "billing.billing_account", ("status", "ACTIVE"));
            foreach (var p in products)
            {
                await rows.Insert(DatabaseFixture.TenantA, "billing.premium_schedule", ("billing_account_id", account), ("product_code", p), ("org_node_code", "BR-A"));
            }

            return await rows.Insert(DatabaseFixture.TenantA, "billing.refund_request", ("billing_account_id", account), ("amount", 100m), ("currency", "ZAR"),
                ("reason_code", "OVERPAID"), ("status", "REQUESTED"));
        }

        var refund = (Billing.Contracts.BillingApprovals.RefundBusinessRefType, Billing.Contracts.BillingApprovals.RefundApprovalType);
        (await Requested(refund.Item1, await Refund("GEN-HOME", "GEN-HOME"), refund.Item2)).ShouldBe("True|GEN-HOME|BR-A", "the account's one scope");
        (await Requested(refund.Item1, await Refund("GEN-HOME", "GEN-MOTOR"), refund.Item2)).ShouldBe("True||", "an account across scopes fails closed");
    }

    /// <summary>
    /// OQ-173: an approval scoped by product only is seen and decided by a branch-limited checker who holds that product, and not by one who doesn't.
    /// OQ-176: an approval scoped to no product or node is seen and decided only by a checker with unrestricted scope.
    /// </summary>
    [Fact]
    public async Task ProductOnlyApprovals_FollowTheProductScope_AndUnresolvedOnes_OnlyUnrestrictedCheckers_OQ173_OQ176()
    {
        var db = fixture.Database;
        var checker = await db.UserWithPermissions(DatabaseFixture.TenantA, "wf-branch-home", All);
        var authorityRole = await db.RoleWithPermissions(DatabaseFixture.TenantA);
        await db.AssignRole(DatabaseFixture.TenantA, checker, authorityRole);
        await Authority(authorityRole, 50_000m);
        await db.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_products = ARRAY['GEN-HOME'], scope_branches = ARRAY['BR-A'] WHERE user_id = @u", ("u", checker));
        var ownProduct = await SeedApproval(100m, "PRODUCT_VERSION");
        var otherProduct = await SeedApproval(100m, "PRODUCT_VERSION");
        var unresolved = await SeedApproval(100m);
        await db.ExecuteAsOwner("UPDATE workflow.approval SET is_scoped = true, product_code = 'GEN-HOME', org_node_applies = false WHERE id = @a", ("a", ownProduct));
        await db.ExecuteAsOwner("UPDATE workflow.approval SET is_scoped = true, product_code = 'GEN-MOTOR', org_node_applies = false WHERE id = @a", ("a", otherProduct));
        await db.ExecuteAsOwner("UPDATE workflow.approval SET is_scoped = true WHERE id = @a", ("a", unresolved));

        var client = _factory.ClientFor(DatabaseFixture.TenantA, "wf-branch-home");
        (await client.GetAsync($"{Approvals}/{ownProduct}")).StatusCode.ShouldBe(HttpStatusCode.OK, "a product version has no node: the checker's branch limit doesn't apply");
        (await client.GetAsync($"{Approvals}/{otherProduct}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync($"{Approvals}/{unresolved}")).StatusCode.ShouldBe(HttpStatusCode.NotFound, "an unresolved scope is for unrestricted checkers only");
        foreach (var (approval, shown) in new[] { (ownProduct, 1), (otherProduct, 0), (unresolved, 0) })
        {
            var businessRef = await db.ScalarAsOwner<Guid>("SELECT business_ref_id FROM workflow.approval WHERE id = @a", ("a", approval));
            (await (await client.GetAsync($"{Approvals}?businessRefId={businessRef}")).Json()).GetProperty("items").GetArrayLength().ShouldBe(shown, "filtered in the query");
            (await (await client.GetAsync($"{Approvals}?businessRefId={businessRef}&decidable=true")).Json()).GetProperty("items").GetArrayLength().ShouldBe(shown);
        }

        await Refused(client, $"{Approvals}/{otherProduct}", "OUT_OF_SCOPE");
        await Refused(client, $"{Approvals}/{unresolved}", "OUT_OF_SCOPE");
        (await client.Send(HttpMethod.Post, $"{Approvals}/{ownProduct}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var unrestricted = _factory.ClientFor(DatabaseFixture.TenantA, "wf-senior");
        (await unrestricted.GetAsync($"{Approvals}/{unresolved}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await unrestricted.Send(HttpMethod.Post, $"{Approvals}/{unresolved}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// F103: whether a checker can decide an approval is judged by the scope of their approval.approve permission, not of approval.read: a checker who
    /// reads every approval but approves only within a product is not offered (decidable=true) or allowed one outside it.
    /// </summary>
    [Fact]
    public async Task Deciding_IsJudgedByTheApproveScope_NotTheReadScope_F103()
    {
        var db = fixture.Database;
        var checker = await db.UserWithPermissions(DatabaseFixture.TenantA, "wf-reads-all", WorkflowPermissions.ApprovalRead);
        var approveRole = await db.RoleWithPermissions(DatabaseFixture.TenantA, WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove);
        await db.AssignRole(DatabaseFixture.TenantA, checker, approveRole);
        await Authority(approveRole, 50_000m);
        await db.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_products = ARRAY['GEN-HOME'] WHERE user_id = @u AND role_code = @r",
            ("u", checker), ("r", approveRole));
        var outside = await SeedApproval(100m);
        var inside = await SeedApproval(100m);
        foreach (var (approval, product) in new[] { (outside, "GEN-MOTOR"), (inside, "GEN-HOME") })
        {
            await db.ExecuteAsOwner("UPDATE workflow.approval SET is_scoped = true, product_code = @p, org_node_code = 'BR-A' WHERE id = @a", ("p", product), ("a", approval));
        }

        var client = _factory.ClientFor(DatabaseFixture.TenantA, "wf-reads-all");
        (await client.GetAsync($"{Approvals}/{outside}")).StatusCode.ShouldBe(HttpStatusCode.OK, "the read scope is unrestricted");
        foreach (var (approval, decidable) in new[] { (outside, 0), (inside, 1) })
        {
            var businessRef = await db.ScalarAsOwner<Guid>("SELECT business_ref_id FROM workflow.approval WHERE id = @a", ("a", approval));
            (await (await client.GetAsync($"{Approvals}?businessRefId={businessRef}&decidable=true")).Json()).GetProperty("items").GetArrayLength()
                .ShouldBe(decidable, "decidable follows approval.approve's scope");
        }

        await Refused(client, $"{Approvals}/{outside}", "OUT_OF_SCOPE");
        (await client.Send(HttpMethod.Post, $"{Approvals}/{inside}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>Delivers an ApprovalRequested to Workflow as the outbox would; returns the stored approval's is_scoped|product|node[|org_node_applies].</summary>
    private async Task<string> Requested(string refType, Guid refId, string approvalType)
    {
        var requested = new ApprovalRequested(refType, refId, approvalType, _maker, 100m, "ZAR", "Scope test");
        var envelope = new BuildingBlocks.Contracts.EventEnvelope(Guid.CreateVersion7(), DatabaseFixture.TenantA, "workflow.ApprovalRequested", 1, DateTimeOffset.UtcNow,
            "ScopeTest", refId, Guid.CreateVersion7(), null, JsonSerializer.SerializeToElement(requested, JsonSerializerOptions.Web));
        await _factory.Services.GetRequiredService<IEventBus>().Publish(envelope, CancellationToken.None);
        return await fixture.Database.ScalarAsOwner<string>("""
            SELECT is_scoped::text || '|' || coalesce(product_code, '') || '|' || coalesce(org_node_code, '') || CASE WHEN org_node_applies THEN '' ELSE '|False' END
            FROM workflow.approval WHERE tenant_id = @t AND business_ref_id = @r
            """, ("t", DatabaseFixture.TenantA), ("r", refId)) is { } row ? row.Replace("true", "True").Replace("false", "False") : "missing";
    }

    private async Task<(string Url, Guid TaskId)> Start(HttpClient client, string processCode)
    {
        var started = await client.Send(HttpMethod.Post, Processes, new { processCode, businessRefType = "LIFE_CLAIM", businessRefId = Guid.CreateVersion7() }, idempotent: true);
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
    /// BR-096: a process definition made through the governed static data API may name no domain validation (no module
    /// provides one for cases), and a process on such a definition moves on its guards alone.
    /// </summary>
    [Fact]
    public async Task AProcessDefinition_MadeThroughTheApi_NeedsNoDomainValidation_AndItsTasksMove_BR096()
    {
        var db = fixture.Database;
        await db.UserWithPermissions(DatabaseFixture.TenantA, "wf-static", [.. All, "static_data.read", "static_data.create", "static_data.update"]);
        var admin = _factory.ClientFor(DatabaseFixture.TenantA, "wf-static");
        var draft = await admin.Send(HttpMethod.Post, "/api/v1/workflow/static/workflow-definitions", new
        {
            code = "API_PROC", processCode = "API_PROC", states = new[] { "OPEN", "DONE" },
            transitions = new[] { new { from = "OPEN", to = "DONE" } }, changeReason = "set up through the API",
        }, idempotent: true);
        draft.StatusCode.ShouldBe(HttpStatusCode.Created, await draft.Content.ReadAsStringAsync());

        // An existing definition with none, as the onboarding seed writes it: its process completes its task.
        await Static("cfg_workflow_definitions", "NO_CHECK", "process_code, states, transitions, domain_validation",
            "'NO_CHECK', ARRAY['Open','Closed'], '[{\"from\":\"Open\",\"to\":\"Closed\"}]', NULL");
        var client = _factory.ClientFor(DatabaseFixture.TenantA, "wf-maker");
        var (url, taskId) = await Start(client, "NO_CHECK");
        var moved = await client.Send(HttpMethod.Post, $"{url}/tasks/{taskId}/complete", new { toState = "Closed" }, ifMatch: "\"1\"");
        moved.IsSuccessStatusCode.ShouldBeTrue(await moved.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// BR-081: my-authority lists all the authority approving checks: Life's claims limits by claim type (which replace
    /// the delegation table for Life claims), and the static data approval types the delegation table does not configure,
    /// for a holder of static_data.approve. A user without the role or the permission sees neither.
    /// </summary>
    [Fact]
    public async Task MyAuthority_ListsModuleLimitsAndTheStaticDataPermission_AsApprovingChecksThem_BR081()
    {
        var db = fixture.Database;
        var t = DatabaseFixture.TenantA;
        var claimsUser = await db.UserWithPermissions(t, "wf-life-claims", [.. All, "static_data.approve"]);
        var claimsRole = await db.RoleWithPermissions(t);
        await db.AssignRole(t, claimsUser, claimsRole);
        foreach (var (type, limit) in new[] { ("DEATH", 250_000m), ("DISABILITY", 100_000m) })
        {
            await db.ExecuteAsOwner("""
                INSERT INTO life.cfg_claims_authority_limits (tenant_id, code, version, effective_from, record_status, created_by, change_reason, role, claim_type, limit_value)
                VALUES (@t, @code, 1, DATE '2026-01-01', 'EFFECTIVE', 'seed', 'test seed', @role, @type, @limit)
                """, ("t", t), ("code", $"CAL-{claimsRole}-{type}"), ("role", claimsRole), ("type", type), ("limit", limit));
        }

        // A delegation row for Life claims does not count: Life's own limits decide them.
        await Static("cfg_delegation_and_approval_authority", $"LC-{claimsRole}", "role_code, transaction_type, limit_value, delegation_allowed",
            $"'{claimsRole}', '{LifeApprovals.ClaimApprovalType}', 9999999, false");

        var response = await _factory.ClientFor(t, "wf-life-claims").GetAsync($"{Approvals}/my-authority");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Json();
        DocumentedSchema.ResponseProblems(body, "core", "WorkflowMyAuthority").ShouldBeEmpty();
        var rows = body.EnumerateArray().ToList();
        rows.Where(r => r.GetProperty("approvalType").GetString() == LifeApprovals.ClaimApprovalType)
            .Select(r => (r.GetProperty("basis").GetString(), r.GetProperty("appliesTo").GetString(), r.GetProperty("limit").GetDecimal()))
            .ShouldBe([("MODULE_LIMIT", "claimType=DEATH", 250_000m), ("MODULE_LIMIT", "claimType=DISABILITY", 100_000m)]);

        var configured = (await db.ScalarAsOwner<string[]>("""
            SELECT coalesce(array_agg(DISTINCT transaction_type), '{}') FROM workflow.cfg_delegation_and_approval_authority
            WHERE tenant_id = @t AND record_status = 'EFFECTIVE' AND effective_from <= current_date AND (effective_to IS NULL OR effective_to > current_date)
            """, ("t", t)))!.ToHashSet();
        var expected = ApprovalKinds.ApprovalTypes.Where(a => a.StartsWith(StaticDataApprovals.ApprovalTypePrefix, StringComparison.Ordinal) && !configured.Contains(a)).ToList();
        expected.ShouldNotBeEmpty();
        rows.Where(r => r.GetProperty("basis").GetString() == "PERMISSION").Select(r => r.GetProperty("approvalType").GetString()).ShouldBe(expected);

        // The junior holds neither the Life role nor static_data.approve.
        var junior = (await (await _factory.ClientFor(t, "wf-junior").GetAsync($"{Approvals}/my-authority")).Json()).EnumerateArray().ToList();
        junior.ShouldAllBe(r => r.GetProperty("basis").GetString() == "DELEGATION");
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

    /// <summary>
    /// Front-end request (18 November 2026): how many approvals the caller can decide, as decidable=true lists them (BR-004), cheap enough for every page:
    /// capped at 99 with more, and cached for a user.
    /// </summary>
    [Fact]
    public async Task TheDecidableCount_IsTheDecidableListsLength_CappedAt99_BR004()
    {
        const string refType = "BR004_COUNT";
        await SeedApproval(500m, refType);
        await SeedApproval(5_000m, refType);

        async Task<(int Count, bool More)> Count(string user)
        {
            var response = await _factory.ClientFor(DatabaseFixture.TenantA, user).GetAsync($"{Approvals}/decidable-count");
            response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var body = await response.Json();
            DocumentedSchema.ResponseProblems(body, "core", "WorkflowCountDecidableApprovals").ShouldBeEmpty();
            return (body.GetProperty("count").GetInt32(), body.GetProperty("more").GetBoolean());
        }

        async Task<int> Listed(string user)
        {
            var client = _factory.ClientFor(DatabaseFixture.TenantA, user);
            var total = 0;
            string? cursor = null;
            do
            {
                var page = await (await client.GetAsync($"{Approvals}?decidable=true&limit=200{(cursor is null ? "" : $"&cursor={cursor}")}")).Json();
                total += page.GetProperty("items").GetArrayLength();
                cursor = page.GetProperty("nextCursor").GetString();
            }
            while (cursor is not null);
            return total;
        }

        var listed = await Listed("wf-senior");
        listed.ShouldBeLessThan(99, "this tenant's waiting approvals, before the cap is tried");
        (await Count("wf-senior")).ShouldBe((listed, false), "the same approvals the decidable list gives");
        (await Count("wf-none")).ShouldBe((0, false));
        (await Count("wf-maker")).ShouldBe((0, false), "never their own");

        // Past the cap: the count stops at 99 and says there are more.
        for (var i = 0; i < 100; i++)
        {
            await SeedApproval(500m, refType);
        }

        (await Count("wf-junior")).ShouldBe((99, true));
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
            VALUES (@t, @code, 1, DATE '2026-01-01', 'EFFECTIVE', 'seed', 'test seed', {values})
            ON CONFLICT DO NOTHING
            """, ("t", DatabaseFixture.TenantA), ("code", code));
}
