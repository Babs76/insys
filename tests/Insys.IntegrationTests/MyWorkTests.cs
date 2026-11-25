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
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.Approvals;
using Insys.Workflow.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// "My work" and the step after an approval (docs/specs/addenda/my-work.md): DE-025 (tasks their owners can't see), DE-028, DE-029, DE-032,
/// DE-044 (a maker's own requests), DE-045 (approved, then a step nobody is told about), DE-015, DE-023. Events are delivered to Workflow's own
/// consumers only, so no other module acts on the made-up records.
/// </summary>
public sealed class MyWorkTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static readonly string[] MyTaskFunctions = ["WORKFLOW_MY_TASKS", "WORKFLOW_CLAIM_MY_TASK", "WORKFLOW_COMPLETE_MY_TASK"];
    private static readonly string[] MyRequestFunctions = ["WORKFLOW_MY_REQUESTS", "WORKFLOW_WITHDRAW_MY_REQUEST"];

    private sealed class World
    {
        public Guid Tenant { get; init; }

        public string Suffix { get; init; } = "";

        public InsysApiFactory Factory { get; init; } = null!;

        public Guid MakerId { get; set; }

        public HttpClient Maker { get; set; } = null!;
    }

    private async Task<World> NewWorld(params string[] makerFunctions)
    {
        var factory = await fixture.Host();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var world = new World { Tenant = Guid.CreateVersion7(), Suffix = suffix, Factory = factory };
        await fixture.Database.SeedTenant(world.Tenant, "MW" + suffix.ToUpperInvariant());
        world.MakerId = await fixture.Database.UserWithFunctions(world.Tenant, $"maker-{suffix}", [.. MyTaskFunctions, .. MyRequestFunctions, .. makerFunctions]);
        world.Maker = factory.ClientFor(world.Tenant, $"maker-{suffix}");
        return world;
    }

    private async Task<(Guid Id, HttpClient Client)> User(World world, string name, params string[] functions)
    {
        var id = await fixture.Database.UserWithFunctions(world.Tenant, $"{name}-{world.Suffix}", functions);
        return (id, world.Factory.ClientFor(world.Tenant, $"{name}-{world.Suffix}"));
    }

    private static async Task Deliver<T>(World world, T integrationEvent, string consumer, Guid aggregateId) where T : IIntegrationEvent
    {
        var type = IntegrationEventTypes.Describe(typeof(T));
        var envelope = new EventEnvelope(Guid.CreateVersion7(), world.Tenant, type.Type, type.Version, DateTimeOffset.UtcNow, typeof(T).Name, aggregateId, Guid.CreateVersion7(),
            null, JsonSerializer.SerializeToElement(integrationEvent, JsonSerializerOptions.Web));
        var delivery = await world.Factory.Services.GetRequiredService<IEventBus>().DeliverTo(envelope, consumer, CancellationToken.None);
        delivery.Error.ShouldBeNull($"{consumer}: {delivery.Error}");
        delivery.Status.ShouldBe(DeliveryStatus.Processed, $"{consumer}: {delivery.Code} {delivery.Item}");
    }

    /// <summary>An approval of the type, asked for by the maker, as Workflow records it from the module's request.</summary>
    private async Task<Guid> Requested(World world, string approvalType, string businessRefType, Guid businessRefId, decimal amount = 1250m)
    {
        await Deliver(world, new ApprovalRequested(businessRefType, businessRefId, approvalType, world.MakerId, amount, "ZAR", "test request"),
            "workflow.RequestApprovalPolicy", businessRefId);
        return await fixture.Database.ScalarAsOwner<Guid>(
            "SELECT id FROM workflow.approval WHERE tenant_id = @t AND business_ref_id = @r AND status = 'REQUESTED'", ("t", world.Tenant), ("r", businessRefId));
    }

    private Task Granted(World world, Guid approval, string approvalType, string businessRefType, Guid businessRefId, Guid checker) =>
        Deliver(world, new Workflow.Contracts.ApprovalGranted(approval, businessRefType, businessRefId, approvalType, world.MakerId, checker),
            "workflow.ApprovalStepPolicies.NextStepGranted", approval);

    private static async Task<List<JsonElement>> Mine(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/workflow/tasks/mine?limit=100");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return [.. (await response.Json()).GetProperty("items").EnumerateArray()];
    }

    private static JsonElement? TaskOf(List<JsonElement> tasks, string processCode, Guid businessRefId) =>
        tasks.Where(t => t.GetProperty("processCode").GetString() == processCode && t.GetProperty("businessRefId").GetGuid() == businessRefId)
            .Select(t => (JsonElement?)t).FirstOrDefault();

    private Task<string?> ProcessState(World world, string processCode, Guid businessRefId) =>
        fixture.Database.ScalarAsOwner<string?>(
            "SELECT state FROM workflow.process_instance WHERE tenant_id = @t AND process_code = @p AND business_ref_id = @r ORDER BY id DESC LIMIT 1",
            ("t", world.Tenant), ("p", processCode), ("r", businessRefId));

    public static TheoryData<int> Steps()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < ApprovalNextSteps.All.Count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    /// <summary>
    /// DE-045, DE-015, DE-029: each row of the table opens its step's task when the approval is granted, for the maker or for whoever serves the
    /// queue (never the maker of a release step, SRS-002), and the record's event closes it. The requester is told by APPROVAL_OUTCOME unless the
    /// step is theirs (DE-044).
    /// </summary>
    [Theory]
    [MemberData(nameof(Steps))]
    public async Task AGrantedApproval_OpensItsNextStep_ForWhoeverServesIt_AndTheRecordsEventClosesIt_DE045(int row)
    {
        var step = ApprovalNextSteps.All[row];
        var world = await NewWorld(step.ServedBy.ToArray());
        var (_, server) = await User(world, "server", [.. MyTaskFunctions, .. step.ServedBy]);
        var (_, bystander) = await User(world, "bystander", MyTaskFunctions);
        var record = Guid.CreateVersion7();
        var approval = await Requested(world, step.ApprovalType, step.BusinessRefType, record);
        approval.ShouldNotBe(Guid.Empty);
        await Granted(world, approval, step.ApprovalType, step.BusinessRefType, record, Guid.CreateVersion7());

        var code = step.Process.ProcessCode;
        (await ProcessState(world, code, record)).ShouldBe(step.Process.States[0], $"{step.ApprovalType} on {step.BusinessRefType} opened no {code}");
        var makerTask = TaskOf(await Mine(world.Maker), code, record);
        TaskOf(await Mine(bystander), code, record).ShouldBeNull("a user who serves neither the queue nor the step sees it");
        if (step.Assignee == StepAssignee.Maker)
        {
            makerTask.ShouldNotBeNull();
            makerTask.Value.GetProperty("assignedToMe").GetBoolean().ShouldBeTrue();
            TaskOf(await Mine(server), code, record).ShouldBeNull("the maker's own step is theirs only");
            (await ProcessState(world, "APPROVAL_OUTCOME", record)).ShouldBeNull("the step itself tells the maker");
        }
        else
        {
            var task = TaskOf(await Mine(server), code, record);
            task.ShouldNotBeNull($"a holder of {string.Join(", ", step.ServedBy)} serves {code}");
            task.Value.GetProperty("queueCode").GetString().ShouldBe(step.Queue);
            task.Value.GetProperty("assignedToMe").GetBoolean().ShouldBeFalse();
            task.Value.GetProperty("approvalId").GetGuid().ShouldBe(approval);
            task.Value.GetProperty("businessRefType").GetString().ShouldBe(step.BusinessRefType);
            task.Value.GetProperty("payoutPurpose").GetString().ShouldBe(step.PayoutPurpose);
            task.Value.GetProperty("payoutCurrency").GetString().ShouldBe(step.PayoutPurpose is null ? null : "ZAR");
            task.Value.GetProperty("allowedTransitions").EnumerateArray().Select(t => t.GetProperty("toState").GetString()).ToArray()
                .ShouldBe(new[] { step.Process.States[^1] });
            if (step.ExcludeMaker)
            {
                makerTask.ShouldBeNull("the maker holds the serving function too, but may not do the step (SRS-002)");
            }

            var outcome = TaskOf(await Mine(world.Maker), "APPROVAL_OUTCOME", record);
            outcome.ShouldNotBeNull("the requester is told of the decision (DE-044)");
            outcome.Value.GetProperty("informational").GetBoolean().ShouldBeTrue();
        }

        await Close(world, step, record);
        (await ProcessState(world, code, record)).ShouldBe("COMPLETED:" + step.Process.States[^1], $"{step.ClosedBy} closes {code}");
        TaskOf(await Mine(step.Assignee == StepAssignee.Maker ? world.Maker : server), code, record).ShouldBeNull();
    }

    private static Task Close(World world, NextStep step, Guid record) => step.ClosedBy switch
    {
        "sdm.StaticDataVersionActivated" => Deliver(world, new BuildingBlocks.Contracts.StaticData.StaticDataVersionActivated(record,
                new BuildingBlocks.Contracts.StaticData.StaticRecordRef("workflow", "cfg_approval_expiry", "APPROVAL_EXPIRY", "workflow", "approval-expiry",
                    Guid.CreateVersion7(), "X", 1, false), TestClock.Today, null),
            "workflow.NextStepClosing.StepActivated", record),
        "claims.ClaimPaymentInstructed" => Deliver(world, new ClaimsFramework.Contracts.ClaimPaymentInstructed(record, "CLM-1", "GENERAL", Guid.CreateVersion7(),
            Guid.CreateVersion7(), 1250m, "ZAR", 1), "workflow.NextStepClosing.StepInstructed", record),
        "payment.PayoutReleased" => Deliver(world, new Payments.Contracts.PayoutReleased(record, "PB-1", "REFUND", 1, 1250m, "ZAR"),
            "workflow.NextStepClosing.StepPayoutReleased", record),
        "health.ProviderPaymentRunReleased" => Deliver(world, new Health.Contracts.ProviderPaymentRunReleased(record, "RUN-1", 1250m, "ZAR", [], TestClock.Today),
            "workflow.NextStepClosing.StepRunReleased", record),
        "payment.PaymentBatched" => Deliver(world, new Payments.Contracts.PaymentBatched(Guid.CreateVersion7(), Guid.CreateVersion7(), step.PayoutPurpose!, "ZAR",
            step.BusinessRefType, record), "workflow.NextStepClosing.StepBatched", record),
        _ => throw new InvalidOperationException($"No test closes {step.ClosedBy}."),
    };

    /// <summary>DE-045: an approval type the table doesn't list opens no step; its requester is still told (DE-044).</summary>
    [Fact]
    public async Task AnApprovalTypeNotInTheTable_OpensNoStep_ButTellsTheRequester_DE045()
    {
        var world = await NewWorld();
        var record = Guid.CreateVersion7();
        var approval = await Requested(world, "CREDIT_NOTE", "CREDIT_NOTE_REQUEST", record);
        await Granted(world, approval, "CREDIT_NOTE", "CREDIT_NOTE_REQUEST", record, Guid.CreateVersion7());
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM workflow.process_instance WHERE tenant_id = @t AND business_ref_id = @r AND process_code <> 'APPROVAL_OUTCOME'",
            ("t", world.Tenant), ("r", record))).ShouldBe(0);
        TaskOf(await Mine(world.Maker), "APPROVAL_OUTCOME", record).ShouldNotBeNull();
    }

    /// <summary>
    /// DE-045: an accepted Life proposal's "issue the policy" goes to the underwriter who submitted it; an accepted General one with no staff submitter
    /// to whoever issues General policies. Each closes when the policy is issued.
    /// </summary>
    [Fact]
    public async Task AnAcceptedProposal_GoesToItsSubmitter_OrTheIssuers_AndClosesWhenThePolicyIsIssued_DE045()
    {
        var world = await NewWorld();
        var (_, issuer) = await User(world, "issuer", [.. MyTaskFunctions, "GENERAL_ISSUE_POLICY"]);
        var life = Guid.CreateVersion7();
        await Deliver(world, new Life.Contracts.LifeProposalAccepted(life, "LP-1", world.MakerId), "workflow.PolicyIssueTasks.LifeProposalAccepted", life);
        var lifeTask = TaskOf(await Mine(world.Maker), "POLICY_ISSUE", life);
        lifeTask.ShouldNotBeNull();
        lifeTask.Value.GetProperty("assignedToMe").GetBoolean().ShouldBeTrue();
        lifeTask.Value.GetProperty("screen").GetString().ShouldBe("LIFE_PROPOSALS");

        var general = Guid.CreateVersion7();
        await Deliver(world, new General.Contracts.GeneralProposalAccepted(general, Guid.CreateVersion7(), null), "workflow.PolicyIssueTasks.GeneralProposalAccepted", general);
        TaskOf(await Mine(issuer), "POLICY_ISSUE", general).ShouldNotBeNull("a holder of GENERAL_ISSUE_POLICY serves it");
        TaskOf(await Mine(world.Maker), "POLICY_ISSUE", general).ShouldBeNull();

        var party = Guid.CreateVersion7();
        await Deliver(world, new Life.Contracts.LifePolicyIssued(Guid.CreateVersion7(), "LPOL-1", life, "LIFE01", 1, party, party, Guid.CreateVersion7(), "MONTHLY",
            "DEBIT_ORDER", "ZAR", 100m, TestClock.Today, null, 100_000m), "workflow.PolicyIssueTasks.LifePolicyIssued", life);
        await Deliver(world, new General.Contracts.GeneralProposalConverted(general, Guid.CreateVersion7(), Guid.CreateVersion7()),
            "workflow.PolicyIssueTasks.GeneralProposalConverted", general);
        (await ProcessState(world, "POLICY_ISSUE", life)).ShouldBe("COMPLETED:ISSUED");
        (await ProcessState(world, "POLICY_ISSUE", general)).ShouldBe("COMPLETED:ISSUED");
    }

    /// <summary>
    /// DE-025, DE-029: a user claims and completes a task they serve; completing needs no target when the process allows one ("done"); another user
    /// can't take it once claimed, and someone who serves nothing is refused.
    /// </summary>
    [Fact]
    public async Task AServerClaimsAndCompletesTheirTask_OthersCannot_DE025()
    {
        var world = await NewWorld();
        var (first, one) = await User(world, "first", [.. MyTaskFunctions, "CLAIMS_RELEASE_INSTRUCTION"]);
        var (_, two) = await User(world, "second", [.. MyTaskFunctions, "CLAIMS_RELEASE_INSTRUCTION"]);
        var (_, nobody) = await User(world, "nobody", MyTaskFunctions);
        var instruction = Guid.CreateVersion7();
        var approval = await Requested(world, "CLAIM_PAYMENT", "CLAIM_PAYMENT", instruction);
        await Granted(world, approval, "CLAIM_PAYMENT", "CLAIM_PAYMENT", instruction, Guid.CreateVersion7());
        var task = TaskOf(await Mine(one), "CLAIM_PAY_RELEASE", instruction)!.Value;
        var taskId = task.GetProperty("id").GetGuid();
        var version = $"\"{task.GetProperty("rowVersion").GetInt64()}\"";

        var refused = await nobody.Send(HttpMethod.Post, $"/api/v1/workflow/tasks/mine/{taskId}/claim", ifMatch: version);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Json()).GetProperty("code").GetString().ShouldBe("TASK_NOT_YOURS");
        (await world.Maker.Send(HttpMethod.Post, $"/api/v1/workflow/tasks/mine/{taskId}/claim", ifMatch: version)).StatusCode.ShouldBe(HttpStatusCode.Forbidden,
            "the drafter may not release (SRS-002)");

        var claimed = await one.Send(HttpMethod.Post, $"/api/v1/workflow/tasks/mine/{taskId}/claim", ifMatch: version);
        claimed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await claimed.Content.ReadAsStringAsync());
        var claimedVersion = claimed.Headers.ETag!.Tag;
        var taken = await two.Send(HttpMethod.Post, $"/api/v1/workflow/tasks/mine/{taskId}/claim", ifMatch: claimedVersion);
        taken.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await taken.Json()).GetProperty("code").GetString().ShouldBe("TASK_CLAIMED_BY_ANOTHER");
        TaskOf(await Mine(two), "CLAIM_PAY_RELEASE", instruction).ShouldBeNull("a task someone claimed is theirs");
        TaskOf(await Mine(one), "CLAIM_PAY_RELEASE", instruction)!.Value.GetProperty("assigneeId").GetGuid().ShouldBe(first);

        (await one.Send(HttpMethod.Post, $"/api/v1/workflow/tasks/mine/{taskId}/complete", ifMatch: version)).StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        var done = await one.Send(HttpMethod.Post, $"/api/v1/workflow/tasks/mine/{taskId}/complete", ifMatch: claimedVersion);
        done.StatusCode.ShouldBe(HttpStatusCode.NoContent, await done.Content.ReadAsStringAsync());
        (await ProcessState(world, "CLAIM_PAY_RELEASE", instruction)).ShouldBe("COMPLETED:RELEASED");
    }

    /// <summary>
    /// DE-028: a parked delivery's task goes to a role (TENANT_ADMIN by default) and to whoever retries parked deliveries, and closes when the
    /// delivery resolves or is discarded. DE-025: one in a queue that is the user's role is theirs too.
    /// </summary>
    [Fact]
    public async Task AParkedDeliveryTask_ReachesItsRoleAndWhoeverRetries_AndClosesWhenResolvedOrDiscarded_DE028()
    {
        var world = await NewWorld();
        var (_, retrier) = await User(world, "retrier", [.. MyTaskFunctions, "RETRY_PARKED_DELIVERY"]);
        var role = await fixture.Database.RoleWithFunctions(world.Tenant, MyTaskFunctions);
        var (adminId, admin) = await User(world, "admin");
        await fixture.Database.AssignRole(world.Tenant, adminId, role);

        var resolved = Guid.CreateVersion7();
        var discarded = Guid.CreateVersion7();
        foreach (var parked in new[] { resolved, discarded })
        {
            await Deliver(world, new Integration.Contracts.ParkedDeliveryEscalated(parked, 1, role, "billing.Something", "SOME_CODE", null, 1, DateTimeOffset.UtcNow.AddDays(1)),
                "workflow.OpenParkedDeliveryTask", parked);
        }

        TaskOf(await Mine(retrier), "PARKED_DELIVERY", resolved).ShouldNotBeNull();
        TaskOf(await Mine(admin), "PARKED_DELIVERY", discarded).ShouldNotBeNull("its queue is the admin's role");
        TaskOf(await Mine(world.Maker), "PARKED_DELIVERY", resolved).ShouldBeNull();

        await Deliver(world, new Integration.Contracts.ParkedDeliveryResolved(resolved, "billing.Something", "SOME_CODE", null, "x.Y", Guid.CreateVersion7()),
            "workflow.ParkedDeliveryTaskClosing.ParkedResolved", resolved);
        await Deliver(world, new Integration.Contracts.ParkedDeliveryDiscarded(discarded, "billing.Something", "SOME_CODE", null, "x.Y", Guid.CreateVersion7()),
            "workflow.ParkedDeliveryTaskClosing.ParkedDiscarded", discarded);
        (await ProcessState(world, "PARKED_DELIVERY", resolved)).ShouldBe("COMPLETED:RESOLVED");
        (await ProcessState(world, "PARKED_DELIVERY", discarded)).ShouldBe("COMPLETED:RESOLVED");
    }

    /// <summary>
    /// DE-044: my requests lists the maker's own approvals, newest first, with status; they withdraw one from it without WORKFLOW_APPROVALS, and
    /// can't withdraw another person's. A rejection tells them (APPROVAL_OUTCOME, informational: "done" closes it), and asking again closes the
    /// follow-up of an expired one (DE-023).
    /// </summary>
    [Fact]
    public async Task MyRequests_ListsMineWithTheirDecision_AndWithdrawsWithoutTheApprovalsScreen_DE044()
    {
        var world = await NewWorld();
        var (otherId, other) = await User(world, "other", MyRequestFunctions);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var firstApproval = await Requested(world, "REFUND", "REFUND_REQUEST", first);
        var secondApproval = await Requested(world, "REFUND", "REFUND_REQUEST", second);

        var page = await (await world.Maker.GetAsync("/api/v1/workflow/approvals/mine")).Json();
        var items = page.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("id").GetGuid()).ShouldBe([secondApproval, firstApproval], "newest first");
        items.ShouldAllBe(i => i.GetProperty("status").GetString() == "Requested");
        (await (await other.GetAsync("/api/v1/workflow/approvals/mine")).Json()).GetProperty("items").GetArrayLength().ShouldBe(0, "only one's own");
        var paged = await (await world.Maker.GetAsync("/api/v1/workflow/approvals/mine?limit=1")).Json();
        var cursor = paged.GetProperty("nextCursor").GetString();
        (await (await world.Maker.GetAsync($"/api/v1/workflow/approvals/mine?limit=1&cursor={Uri.EscapeDataString(cursor!)}")).Json()).GetProperty("items")[0]
            .GetProperty("id").GetGuid().ShouldBe(firstApproval);

        var version = $"\"{items[1].GetProperty("rowVersion").GetInt64()}\"";
        var notMine = await other.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/mine/{firstApproval}/withdraw", new { reason = "not mine" }, ifMatch: version);
        notMine.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await notMine.Json()).GetProperty("code").GetString().ShouldBe("APPROVAL_NOT_REQUESTER");
        var withdrawn = await world.Maker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/mine/{firstApproval}/withdraw", new { reason = "asked by mistake" },
            ifMatch: version);
        withdrawn.StatusCode.ShouldBe(HttpStatusCode.NoContent, await withdrawn.Content.ReadAsStringAsync());
        (await (await world.Maker.GetAsync("/api/v1/workflow/approvals/mine?status=withdrawn")).Json()).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ShouldBe([firstApproval]);

        // Rejected: told by an informational task, closed by "done".
        await Deliver(world, new Workflow.Contracts.ApprovalRejected(secondApproval, "REFUND_REQUEST", second, "REFUND", world.MakerId, otherId, "No"),
            "workflow.ApprovalStepPolicies.NextStepRejected", secondApproval);
        var told = TaskOf(await Mine(world.Maker), "APPROVAL_OUTCOME", second)!.Value;
        told.GetProperty("informational").GetBoolean().ShouldBeTrue();
        told.GetProperty("screen").GetString().ShouldNotBeNull();
        var read = await world.Maker.Send(HttpMethod.Post, $"/api/v1/workflow/tasks/mine/{told.GetProperty("id").GetGuid()}/complete",
            ifMatch: $"\"{told.GetProperty("rowVersion").GetInt64()}\"");
        read.StatusCode.ShouldBe(HttpStatusCode.NoContent, await read.Content.ReadAsStringAsync());
        TaskOf(await Mine(world.Maker), "APPROVAL_OUTCOME", second).ShouldBeNull();

        // DE-023: an expired request's follow-up closes when the requester asks again.
        var third = Guid.CreateVersion7();
        var thirdApproval = await Requested(world, "REFUND", "REFUND_REQUEST", third);
        await fixture.Database.ExecuteAsOwner("UPDATE workflow.approval SET expires_at = now() - interval '1 minute' WHERE id = @id", ("id", thirdApproval));
        await world.Factory.Services.GetRequiredService<Workflow.Infrastructure.WorkflowJobs>().Run(CancellationToken.None);
        TaskOf(await Mine(world.Maker), "APPROVAL_FOLLOW_UP", third).ShouldNotBeNull();
        await Deliver(world, new ApprovalRequested("REFUND_REQUEST", third, "REFUND", world.MakerId, 10m, "ZAR", "again"), "workflow.RequesterTaskClosing.RequesterAskedAgain",
            third);
        (await ProcessState(world, "APPROVAL_FOLLOW_UP", third)).ShouldBe("COMPLETED:DONE");
    }

    /// <summary>DE-025: without own_task.read the list is refused, and own_task.read alone never gives the full task list.</summary>
    [Fact]
    public async Task MyTasks_NeedsItsOwnFunction_AndGivesNoFullTaskList_DE025()
    {
        var world = await NewWorld();
        var (_, none) = await User(world, "none", "WORKFLOW_MY_REQUESTS");
        (await none.GetAsync("/api/v1/workflow/tasks/mine")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await world.Maker.GetAsync("/api/v1/workflow/tasks?queueCode=DEFAULT")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
