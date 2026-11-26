/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Workflow
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
using System.Text.Json;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.Approvals;
using Insys.BuildingBlocks.Domain;
using Insys.Workflow.Domain.Approvals;
using Insys.Workflow.Domain.Processes;

namespace Insys.Workflow.Application;

// "My work" (DE-025, DE-044): the caller's own tasks and requests, without the full task list or the approvals screen.
//
// How a task is addressed: every task has a queue code and may have an assignee (workflow.task). The queue code is either a role
// code (V0081: escalations, parked deliveries, held claim payments, referrals and renewal reviews route to a role) or a named queue
// (PARTY_REVIEW, COMPLIANCE, HEALTH_MEMBERSHIP, REQUESTER, or one the tenant's task types and work queues configure, DEFAULT when none
// does). Nothing in the configuration maps a named queue to a role. So a task is the caller's when
//   - it is assigned to them; or
//   - it is unassigned, the process does not exclude them (SRS-002), it is in their scope (F103), and either
//     - its queue is one of their roles in effect, or
//     - their roles grant a function that serves the task's process (WorkServers below: the function that does what the task asks).
// TODO(spec): a queue the tenant configures (cfg_task_types_and_work_queues) names no role or function; its tasks reach a person only
// through a role-coded queue, an assignment, or WorkServers. Recorded in docs/decisions/open-questions.md (OQ-202).

/// <summary>
/// DE-025, DE-028, DE-029, DE-032, DE-004: the functions whose holders serve a built-in process's unassigned tasks, whatever queue they
/// are in: the function that does what the task asks. A process not listed is served only through its queue's role or an assignment.
/// </summary>
public static class WorkServers
{
    private static readonly Dictionary<string, string[]> Served = new(StringComparer.Ordinal)
    {
        [BuiltInProcesses.ParkedDelivery.ProcessCode] = ["RETRY_PARKED_DELIVERY"], // DE-028: the queue is a role (TENANT_ADMIN by default) that can't list them
        [BuiltInProcesses.HeldClaimPayment.ProcessCode] = ["CLAIMS_RELEASE_INSTRUCTION"], // DE-029
        [BuiltInProcesses.PartyMatchReview.ProcessCode] = ["PARTY_RESOLVE_MATCH"], // DE-032: queue PARTY_REVIEW
        [BuiltInProcesses.DueDiligenceHitReview.ProcessCode] = ["PARTY_REVIEW_SCREENING_HIT"], // queue COMPLIANCE
        [BuiltInProcesses.DueDiligenceSanctionsMatch.ProcessCode] = ["PARTY_RECORD_FIC_REPORT"],
        [BuiltInProcesses.DueDiligenceReview.ProcessCode] = ["PARTY_COMPLETE_DUE_DILIGENCE_REVIEW"],
        [BuiltInProcesses.HealthArrearsTermination.ProcessCode] = ["HEALTH_MEMBERSHIP_TERMINATE"], // queue HEALTH_MEMBERSHIP
        [BuiltInProcesses.ProviderMeasureReview.ProcessCode] = ["HEALTH_RECORD_PROVIDER_QUALITY_REVIEW"], // DE-004 (the function is in no template yet)
        [BuiltInProcesses.ProgrammeReview.ProcessCode] = ["HEALTH_RECORD_PROGRAMME_REVIEW"],
        [BuiltInProcesses.CareActivityFollowUp.ProcessCode] = ["HEALTH_RECORD_CARE_PROGRESS"],
        [BuiltInProcesses.RecoveryReview.ProcessCode] = ["HEALTH_RECOVERIES"],
        [BuiltInProcesses.DataSubjectRequest.ProcessCode] = ["PARTY_DATA_SUBJECT_REQUESTS"],
        [BuiltInProcesses.PartyRetentionReview.ProcessCode] = ["PARTY_ANONYMISE"],
        [BuiltInProcesses.GeneralRetention.ProcessCode] = ["GENERAL_WORK_RETENTION_CASE"],
        [BuiltInProcesses.PolicyIssue.ProcessCode] = ["LIFE_ISSUE_POLICY", "GENERAL_ISSUE_POLICY"],
    };

    static WorkServers()
    {
        foreach (var step in ApprovalNextSteps.All.Where(s => s.ServedBy.Count > 0))
        {
            Served[step.Process.ProcessCode] = [.. Served.GetValueOrDefault(step.Process.ProcessCode, []).Union(step.ServedBy, StringComparer.Ordinal)];
        }
    }

    /// <summary>Every process with serving functions, and the functions.</summary>
    public static IReadOnlyDictionary<string, string[]> All => Served;

    /// <summary>The functions that serve the process's tasks; empty when none does.</summary>
    public static IReadOnlyList<string> For(string processCode) => Served.GetValueOrDefault(processCode, []);

    /// <summary>The processes whose unassigned tasks a holder of these functions serves.</summary>
    public static IReadOnlyList<string> ServedBy(IReadOnlySet<string> functions) =>
        [.. Served.Where(s => s.Value.Any(functions.Contains)).Select(s => s.Key).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The screen that opens the record a task is about, for the reference types processes use that no approval declares (BR-005 for the
    /// rest, through ApprovalKinds).
    /// </summary>
    public static string? ScreenFor(string businessRefType) => ApprovalKinds.ScreenFor(businessRefType) ?? businessRefType switch
    {
        "PARKED_DELIVERY" => "INTEGRATION_PARKED_DELIVERIES",
        "PARTY" => "PARTIES",
        "DATA_SUBJECT_REQUEST" => "PARTY_DATA_SUBJECT_REQUESTS",
        "PARTY_RETENTION" => "PARTIES",
        "LIFE_PROPOSAL" => "LIFE_PROPOSALS",
        "GENERAL_PROPOSAL" => "GENERAL_PROPOSALS",
        "GENERAL_QUOTE" => "GENERAL_QUOTES",
        "GENERAL_RENEWAL" => "GENERAL_RENEWALS",
        "GENERAL_RETENTION_CASE" => "GENERAL_RETENTION_CASES",
        "AGGREGATION_ZONE" => "GENERAL_AGGREGATION_ALERTS",
        "HEALTH_MEMBERSHIP" => "HEALTH_MEMBERSHIPS",
        "HEALTH_PROVIDER_MEASURE" => "HEALTH_PROVIDER_PERFORMANCE",
        "HEALTH_PROGRAMME_REVIEW" => "HEALTH_PROGRAMME_ENROLMENTS",
        "HEALTH_CARE_ACTIVITY" => "HEALTH_COMPLEX_CASES",
        "HEALTH_RECOVERY_REVIEW" => "HEALTH_RECOVERIES",
        _ => null,
    };
}

/// <summary>Who works a next step: the approval's maker (assigned to them) or a queue.</summary>
public enum StepAssignee
{
    Maker,
    Queue,
}

/// <summary>
/// One row of the table of what follows a granted approval (DE-045): the step's process, who works it, and the event that closes it.
/// </summary>
/// <param name="BusinessRefType">The record type the approval is about; the step's process is about the same record.</param>
/// <param name="Queue">The task's queue: the role the step's work belongs to in the role templates.</param>
/// <param name="ServedBy">The functions that do the step; their holders see the task whatever their role is called.</param>
/// <param name="ExcludeMaker">The approval's maker may not work it (the drafter or builder of a release step: SRS-002).</param>
/// <param name="ClosedBy">The event that closes the task, by its type (for the record; the handlers below act on it).</param>
/// <param name="PayoutPurpose">For a "build a payout batch" step: the batch's purpose (Payments' PaymentPurposes).</param>
public sealed record NextStep(string ApprovalType, string BusinessRefType, ProcessDefinition Process, StepAssignee Assignee, string Queue,
    IReadOnlyList<string> ServedBy, bool ExcludeMaker, string ClosedBy, string? PayoutPurpose = null);

/// <summary>
/// DE-045, DE-015, DE-029: what a granted approval leads to. An approval type and record type not listed here opens no task (its record
/// moves on by itself, or its next step is already a task of its own). docs/specs/addenda/my-work.md describes the table.
/// </summary>
public static class ApprovalNextSteps
{
    private const string BatchBuilder = "FINANCE_OFFICER";
    private const string Releaser = "FINANCE_MANAGER";
    private const string Built = "payment.PaymentBatched";

    public static readonly IReadOnlyList<NextStep> All =
    [
        // DE-015: an approved static data version or import is activated by its maker, who holds SD_<module>_MAINTAIN; an import's lines carry its change request.
        new("STATIC_DATA_T1", "STATIC_DATA_CHANGE", BuiltInProcesses.StaticDataActivation, StepAssignee.Maker, RequesterFollowUp.QueueCode, [], false,
            "sdm.StaticDataVersionActivated"),
        new("STATIC_DATA_T2", "STATIC_DATA_CHANGE", BuiltInProcesses.StaticDataActivation, StepAssignee.Maker, RequesterFollowUp.QueueCode, [], false,
            "sdm.StaticDataVersionActivated"),
        // DE-029: an approved new instruction is released by a claims manager, never its drafter (SOD_CLAIM_PAYMENT).
        new("CLAIM_PAYMENT", "CLAIM_PAYMENT", BuiltInProcesses.ClaimPaymentRelease, StepAssignee.Queue, "CLAIMS_MANAGER", ["CLAIMS_RELEASE_INSTRUCTION"], true,
            "claims.ClaimPaymentInstructed"),
        // An approved batch or run is released by a finance manager, never its builder.
        new("PAYOUT_RELEASE", "PAYOUT_BATCH", BuiltInProcesses.PayoutBatchRelease, StepAssignee.Queue, Releaser, ["PAYMENTS_RELEASE_BATCH"], true, "payment.PayoutReleased"),
        new("PROVIDER_PAYMENT_RUN", "HEALTH_PAYMENT_RUN", BuiltInProcesses.PaymentRunRelease, StepAssignee.Queue, Releaser, ["HEALTH_RELEASE_RUN"], true,
            "health.ProviderPaymentRunReleased"),
        // An approved payout waits for a finance officer to build a batch of its purpose and currency.
        new("REFUND", "REFUND_REQUEST", BuiltInProcesses.PayoutBatchBuild, StepAssignee.Queue, BatchBuilder, ["PAYMENTS_BUILD_BATCH"], false, Built, "REFUND"),
        new("COMMISSION_STATEMENT", "COMMISSION_STATEMENT", BuiltInProcesses.PayoutBatchBuild, StepAssignee.Queue, BatchBuilder, ["PAYMENTS_BUILD_BATCH"], false, Built,
            "COMMISSION"),
        new("CASE_REDRESS", "CASE_REDRESS", BuiltInProcesses.PayoutBatchBuild, StepAssignee.Queue, BatchBuilder, ["PAYMENTS_BUILD_BATCH"], false, Built, "CASE_REDRESS"),
        new("PAYOUT_RETRY", "REFUND_REQUEST", BuiltInProcesses.PayoutBatchBuild, StepAssignee.Queue, BatchBuilder, ["PAYMENTS_BUILD_BATCH"], false, Built, "REFUND"),
        new("PAYOUT_RETRY", "COMMISSION_STATEMENT", BuiltInProcesses.PayoutBatchBuild, StepAssignee.Queue, BatchBuilder, ["PAYMENTS_BUILD_BATCH"], false, Built,
            "COMMISSION"),
        new("PAYOUT_RETRY", "CASE_REDRESS", BuiltInProcesses.PayoutBatchBuild, StepAssignee.Queue, BatchBuilder, ["PAYMENTS_BUILD_BATCH"], false, Built,
            "CASE_REDRESS"),
        new("PAYOUT_RETRY", "HEALTH_REMITTANCE", BuiltInProcesses.PayoutBatchBuild, StepAssignee.Queue, BatchBuilder, ["PAYMENTS_BUILD_BATCH"], false, Built,
            "CLAIM_PAYOUT"),
    ];

    public static NextStep? For(string approvalType, string businessRefType) =>
        All.FirstOrDefault(s => s.ApprovalType == approvalType && s.BusinessRefType == businessRefType);
}

/// <summary>Opens and closes the one-step tasks that follow approvals and records (DE-045): one running per process and record.</summary>
public sealed class StepTasks(IProcessRepository processes, IProcessQueries queries, IApprovalRepository approvals, Contracts.IBusinessTime businessTime,
    ITenantContext tenant, IClock clock, IAuditTrail audit)
{
    /// <summary>The currency of the approval's amount: the payout batch's currency for a "build a payout batch" step.</summary>
    public async Task<string?> ApprovalCurrency(Guid approvalId, CancellationToken cancellationToken) =>
        (await approvals.Get(approvalId, cancellationToken))?.Currency;

    /// <summary>Opens the task unless one of the process is already running for the record. Returns the process, or null when none was opened.</summary>
    public async Task<ProcessInstance?> Open(ProcessDefinition definition, string businessRefType, Guid businessRefId, string queue, Guid? assignee,
        CancellationToken cancellationToken, Guid? approvalId = null, Guid? excludedUserId = null, string? payoutPurpose = null, string? payoutCurrency = null,
        IReadOnlyDictionary<string, string?>? detail = null, string? notice = null)
    {
        if (await Running(definition, businessRefType, businessRefId, cancellationToken) is not [])
        {
            return null;
        }

        var now = clock.UtcNow;
        var due = await businessTime.Add(tenant.RequireTenantId(), null, now, "P1D", cancellationToken);
        var process = ProcessInstance.StartProcess(Guid.CreateVersion7(), definition, businessRefType, businessRefId, _ => new TaskRouting(queue, null, due), now).Value;
        process.Follow(approvalId, excludedUserId, payoutPurpose, payoutCurrency);
        if (notice is not null)
        {
            process.Tell(notice);
        }

        if (assignee is { } user)
        {
            process.AssignTask(process.OpenTask!.Id, user);
        }

        await processes.Add(process, cancellationToken);
        var details = new Dictionary<string, string?>(detail ?? new Dictionary<string, string?>())
        {
            ["process"] = $"{definition.ProcessCode}@{definition.Version}", ["queue"] = queue, ["approval"] = approvalId?.ToString(),
        };
        audit.Record(new AuditEntry("WORKFLOW", "StartProcess", nameof(ProcessInstance), process.Id.ToString(), details));
        return process;
    }

    /// <summary>Closes every running task of the process for the record, moving it to its final state. Returns how many it closed.</summary>
    public async Task<int> Close(ProcessDefinition definition, string businessRefType, Guid businessRefId, string why, CancellationToken cancellationToken)
    {
        var closed = 0;
        foreach (var id in await Running(definition, businessRefType, businessRefId, cancellationToken))
        {
            if (await processes.Get(id, cancellationToken) is not { OpenTask: { } task } process)
            {
                continue;
            }

            var completed = process.CompleteTask(task.Id, definition.States[^1], definition, new Dictionary<string, object?>(),
                _ => new TaskRouting(task.QueueCode, null, clock.UtcNow), clock.UtcNow);
            if (completed.IsFailure)
            {
                throw new InvalidOperationException($"The {definition.ProcessCode} task of {businessRefType} {businessRefId} cannot be closed: {completed.Error.Code}");
            }

            audit.Record(new AuditEntry("WORKFLOW", "CompleteTask", nameof(ProcessInstance), process.Id.ToString(),
                new Dictionary<string, string?> { ["process"] = definition.ProcessCode, ["why"] = why }));
            closed++;
        }

        return closed;
    }

    private async Task<IReadOnlyList<Guid>> Running(ProcessDefinition definition, string businessRefType, Guid businessRefId, CancellationToken cancellationToken) =>
        (await queries.List(businessRefType, businessRefId, null, 100, cancellationToken))
        .Where(p => p.ProcessCode == definition.ProcessCode && p.Status == ProcessStatus.Running).Select(p => p.Id).ToList();
}

/// <summary>
/// DE-045, DE-044: a decision on an approval. Granted: the next step in <see cref="ApprovalNextSteps"/> gets its task. The requester is told
/// of the decision by an informational APPROVAL_OUTCOME task (approved or rejected), unless the next step is already theirs (static data:
/// they activate it). An expiry, or a withdrawal for them, already opens APPROVAL_FOLLOW_UP (RequesterFollowUp), and a withdrawal by them
/// tells nobody. An approval the platform raised (BR-109) has no person to tell.
/// </summary>
public sealed class ApprovalStepPolicies(StepTasks steps) : IIntegrationEventHandler<Contracts.ApprovalGranted>, IIntegrationEventHandler<Contracts.ApprovalRejected>
{
    public async Task Handle(Contracts.ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var step = ApprovalNextSteps.For(granted.ApprovalType, granted.BusinessRefType);
        var system = MakerChecker.IsSystem(granted.MakerId);
        if (step is not null && !(step.Assignee == StepAssignee.Maker && system))
        {
            var currency = step.PayoutPurpose is null ? null : await steps.ApprovalCurrency(granted.ApprovalId, cancellationToken);
            await steps.Open(step.Process, granted.BusinessRefType, granted.BusinessRefId, step.Queue, step.Assignee == StepAssignee.Maker ? granted.MakerId : null,
                cancellationToken, granted.ApprovalId, step.ExcludeMaker && !system ? granted.MakerId : null, step.PayoutPurpose, currency,
                new Dictionary<string, string?> { ["approvalType"] = granted.ApprovalType });
        }

        if (!system && step?.Assignee != StepAssignee.Maker)
        {
            await Outcome(granted.ApprovalId, granted.BusinessRefType, granted.BusinessRefId, granted.MakerId, "APPROVED", cancellationToken);
        }
    }

    public async Task Handle(Contracts.ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (rejected.Ending == Contracts.ApprovalEndings.Rejected && !MakerChecker.IsSystem(rejected.MakerId))
        {
            await Outcome(rejected.ApprovalId, rejected.BusinessRefType, rejected.BusinessRefId, rejected.MakerId, "REJECTED", cancellationToken);
        }
    }

    private Task Outcome(Guid approvalId, string businessRefType, Guid businessRefId, Guid makerId, string outcome, CancellationToken cancellationToken) =>
        steps.Open(BuiltInProcesses.ApprovalOutcome, businessRefType, businessRefId, RequesterFollowUp.QueueCode, makerId, cancellationToken, approvalId,
            detail: new Dictionary<string, string?> { ["outcome"] = outcome });
}

/// <summary>
/// DE-023, DE-044: the requester's follow-up and outcome tasks close themselves when they ask again (a new approval for the record), and a
/// not-applied approval's follow-up closes when a retry of its delivery applies it after all.
/// </summary>
public sealed class RequesterTaskClosing(StepTasks steps, IApprovalRepository approvals) :
    IIntegrationEventHandler<ApprovalRequested>,
    IIntegrationEventHandler<Integration.Contracts.ParkedDeliveryResolved>
{
    public async Task Handle(ApprovalRequested requested, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        await steps.Close(BuiltInProcesses.ApprovalFollowUp, requested.BusinessRefType, requested.BusinessRefId, "ASKED_AGAIN", cancellationToken);
        await steps.Close(BuiltInProcesses.ApprovalOutcome, requested.BusinessRefType, requested.BusinessRefId, "ASKED_AGAIN", cancellationToken);
    }

    public async Task Handle(Integration.Contracts.ParkedDeliveryResolved resolved, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (ApprovalNotAppliedPolicy.ApprovalOf(resolved.Code, resolved.EventType, resolved.Item) is { } approvalId
            && await approvals.Get(approvalId, cancellationToken) is { } approval)
        {
            await steps.Close(BuiltInProcesses.ApprovalFollowUp, approval.BusinessRefType, approval.BusinessRefId, "APPLIED", cancellationToken);
        }
    }
}

/// <summary>DE-028: a parked delivery's escalation tasks close when the delivery resolves (a retry applied it) or a person discards it.</summary>
public sealed class ParkedDeliveryTaskClosing(StepTasks steps) :
    IIntegrationEventHandler<Integration.Contracts.ParkedDeliveryResolved>,
    IIntegrationEventHandler<Integration.Contracts.ParkedDeliveryDiscarded>
{
    public Task Handle(Integration.Contracts.ParkedDeliveryResolved resolved, EventEnvelope envelope, CancellationToken cancellationToken) =>
        steps.Close(BuiltInProcesses.ParkedDelivery, Integration.Contracts.ParkedDeliveryEscalated.BusinessRefType, resolved.ParkedDeliveryId, "RESOLVED", cancellationToken);

    public Task Handle(Integration.Contracts.ParkedDeliveryDiscarded discarded, EventEnvelope envelope, CancellationToken cancellationToken) =>
        steps.Close(BuiltInProcesses.ParkedDelivery, Integration.Contracts.ParkedDeliveryEscalated.BusinessRefType, discarded.ParkedDeliveryId, "DISCARDED",
            cancellationToken);
}

/// <summary>DE-045, DE-015, DE-029: each next step's task closes on its record's event (the ClosedBy column of <see cref="ApprovalNextSteps"/>).</summary>
public sealed class NextStepClosing(StepTasks steps) :
    IIntegrationEventHandler<BuildingBlocks.Contracts.StaticData.StaticDataVersionActivated>,
    IIntegrationEventHandler<ClaimsFramework.Contracts.ClaimPaymentInstructed>,
    IIntegrationEventHandler<Payments.Contracts.PayoutReleased>,
    IIntegrationEventHandler<Health.Contracts.ProviderPaymentRunReleased>,
    IIntegrationEventHandler<Payments.Contracts.PaymentBatched>
{
    public async Task Handle(BuildingBlocks.Contracts.StaticData.StaticDataVersionActivated activated, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (activated.ChangeRequestId is { } changeRequestId)
        {
            await steps.Close(BuiltInProcesses.StaticDataActivation, StaticData.Contracts.StaticDataApprovals.BusinessRefType, changeRequestId, "ACTIVATED",
                cancellationToken);
        }
    }

    /// <summary>The instruction was released: its release task, and a held payment's task (DE-029), close.</summary>
    public async Task Handle(ClaimsFramework.Contracts.ClaimPaymentInstructed instructed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var type = ClaimsFramework.Contracts.ClaimsApprovals.InstructionBusinessRefType;
        await steps.Close(BuiltInProcesses.ClaimPaymentRelease, type, instructed.InstructionId, "RELEASED", cancellationToken);
        await steps.Close(BuiltInProcesses.HeldClaimPayment, type, instructed.InstructionId, "RELEASED", cancellationToken);
    }

    public Task Handle(Payments.Contracts.PayoutReleased released, EventEnvelope envelope, CancellationToken cancellationToken) =>
        steps.Close(BuiltInProcesses.PayoutBatchRelease, Payments.Contracts.PaymentApprovals.PayoutBusinessRefType, released.BatchId, "RELEASED", cancellationToken);

    public Task Handle(Health.Contracts.ProviderPaymentRunReleased released, EventEnvelope envelope, CancellationToken cancellationToken) =>
        steps.Close(BuiltInProcesses.PaymentRunRelease, Health.Contracts.HealthApprovals.RunBusinessRefType, released.RunId, "RELEASED", cancellationToken);

    public async Task Handle(Payments.Contracts.PaymentBatched batched, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (batched.BusinessRefType is { } type && batched.BusinessRefId is { } id)
        {
            await steps.Close(BuiltInProcesses.PayoutBatchBuild, type, id, "BATCHED", cancellationToken);
        }
    }
}

/// <summary>
/// DE-016, OQ-204 (Ade, 25 November 2026): someone other than its maker discarded a static data draft left untouched for the tenant's days. The maker is
/// told by an informational STATIC_DRAFT_DISCARDED task assigned to them, whose notice names the table, code and version, who discarded it, when and why
/// (as APPROVAL_OUTCOME tells a requester, DE-044). A maker's own discard raises no event, so tells nobody.
/// </summary>
public sealed class StaticDraftDiscardNotices(StepTasks steps) : IIntegrationEventHandler<BuildingBlocks.Contracts.StaticData.StaticDataDraftDiscarded>
{
    /// <summary>What the task is about: the discarded version (its ID; the version no longer exists).</summary>
    public const string BusinessRefType = StaticData.Contracts.StaticDataApprovals.DiscardedVersionRefType;

    public Task Handle(BuildingBlocks.Contracts.StaticData.StaticDataDraftDiscarded discarded, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var record = discarded.Record;
        var notice = new StaticDraftDiscardNotice(record.EntityCode, $"{record.ModuleRoute}/static/{record.EntityRoute}{(record.Global ? "/global" : "")}", record.Code,
            record.Version, record.Global, discarded.DiscardedBy, discarded.DiscardedAt, discarded.Reason);
        return steps.Open(BuiltInProcesses.StaticDraftDiscarded, BusinessRefType, record.RecordId, RequesterFollowUp.QueueCode, discarded.MakerId, cancellationToken,
            detail: new Dictionary<string, string?> { ["discardedBy"] = discarded.DiscardedBy.ToString() },
            notice: JsonSerializer.Serialize(notice, StaticDraftDiscardNotice.Json));
    }
}

/// <summary>
/// DE-016, OQ-204: what a STATIC_DRAFT_DISCARDED task tells its maker: the draft someone else discarded, who, when and why. Only IDs, no other personal data.
/// </summary>
/// <param name="EntityCode">The table's entity code (as a change request's).</param>
/// <param name="EntityRoute">The table's API route under /api/v1, ending "/global" for a platform-global row (as a change request's).</param>
/// <param name="Code">The draft's code: the code is free for a new draft.</param>
/// <param name="VersionNumber">The version that was discarded.</param>
/// <param name="DiscardedBy">The user who discarded it, holding the table's maintain function.</param>
/// <param name="Reason">Why they discarded it, as they gave it.</param>
public sealed record StaticDraftDiscardNotice(string EntityCode, string EntityRoute, string Code, int VersionNumber, bool Global, Guid DiscardedBy,
    DateTimeOffset DiscardedAt, string Reason)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// DE-045: an accepted Life or General proposal waits for its policy: a POLICY_ISSUE task about the proposal, assigned to the staff user who
/// submitted it, or else in the underwriters' queue (served by the issue function). It closes when the policy is issued.
/// </summary>
public sealed class PolicyIssueTasks(StepTasks steps) :
    IIntegrationEventHandler<Life.Contracts.LifeProposalAccepted>,
    IIntegrationEventHandler<Life.Contracts.LifePolicyIssued>,
    IIntegrationEventHandler<General.Contracts.GeneralProposalAccepted>,
    IIntegrationEventHandler<General.Contracts.GeneralProposalConverted>
{
    public Task Handle(Life.Contracts.LifeProposalAccepted accepted, EventEnvelope envelope, CancellationToken cancellationToken) =>
        steps.Open(BuiltInProcesses.PolicyIssue, Life.Contracts.LifeProposalTasks.BusinessRefType, accepted.ProposalId, "LIFE_UNDERWRITER", accepted.SubmittedBy,
            cancellationToken, detail: new Dictionary<string, string?> { ["proposal"] = accepted.ProposalNumber });

    public async Task Handle(Life.Contracts.LifePolicyIssued issued, EventEnvelope envelope, CancellationToken cancellationToken) =>
        await steps.Close(BuiltInProcesses.PolicyIssue, Life.Contracts.LifeProposalTasks.BusinessRefType, issued.ProposalId, "ISSUED", cancellationToken);

    public Task Handle(General.Contracts.GeneralProposalAccepted accepted, EventEnvelope envelope, CancellationToken cancellationToken) =>
        steps.Open(BuiltInProcesses.PolicyIssue, General.Contracts.GeneralProposalTasks.BusinessRefType, accepted.ProposalId, "GENERAL_UNDERWRITER",
            accepted.SubmittedBy, cancellationToken);

    public async Task Handle(General.Contracts.GeneralProposalConverted converted, EventEnvelope envelope, CancellationToken cancellationToken) =>
        await steps.Close(BuiltInProcesses.PolicyIssue, General.Contracts.GeneralProposalTasks.BusinessRefType, converted.ProposalId, "ISSUED", cancellationToken);
}

// ---- The caller's own tasks (DE-025) ------------------------------------------------------------------------------------------

/// <summary>The caller and what they serve: their roles in effect (queues) and the processes their functions serve (<see cref="WorkServers"/>).</summary>
public sealed record MyTasksFilter(Guid UserId, IReadOnlyCollection<string> Queues, IReadOnlyCollection<string> ServedProcesses);

/// <summary>An open task with its process, as the store reads it.</summary>
public sealed record MyTaskRow(Guid TaskId, Guid ProcessId, string ProcessCode, int ProcessVersion, string State, long ProcessRowVersion, string BusinessRefType,
    Guid BusinessRefId, string TaskType, string QueueCode, Guid? AssigneeId, string? SlaCode, DateTimeOffset DueAt, Guid? ApprovalId, ApprovalStatus? ApprovalStatus,
    string? PayoutPurpose, string? PayoutCurrency, string? Notice = null);

/// <summary>One of the caller's open tasks (DE-025): what it asks, what it is about, and how to complete it.</summary>
/// <param name="Id">The task's ID, for claiming and completing it.</param>
/// <param name="RowVersion">The process's version: send it as If-Match to claim or complete the task.</param>
/// <param name="BusinessRefType">What the task is about: with <paramref name="BusinessRefId"/>, the record it links to.</param>
/// <param name="Screen">The SCREEN function that opens that record (BR-005), or null when none is declared.</param>
/// <param name="QueueCode">The queue it is in: a role code, or a named queue (REQUESTER for the requester's own).</param>
/// <param name="AssignedToMe">True when it is assigned to the caller; false when it waits unassigned in a queue they serve.</param>
/// <param name="Informational">It only tells the caller something (APPROVAL_OUTCOME, STATIC_DRAFT_DISCARDED): completing it is "done".</param>
/// <param name="ApprovalId">The approval the task follows or tells about; null for a task that follows none.</param>
/// <param name="ApprovalStatus">That approval's status now (Approved, Rejected ...).</param>
/// <param name="PayoutPurpose">For a "build a payout batch" task (PAYOUT_BATCH_BUILD): the purpose of the batch to build.</param>
/// <param name="PayoutCurrency">And its currency.</param>
/// <param name="AllowedTransitions">The states completing the task may move the process to; one means "done" needs no choice.</param>
/// <param name="DraftDiscarded">For a STATIC_DRAFT_DISCARDED task (OQ-204): the draft someone else discarded, who, when and why; null for any other task.</param>
public sealed record MyTaskView(Guid Id, Guid ProcessId, string ProcessCode, string TaskType, [property: BusinessRefTypeField] string BusinessRefType,
    Guid BusinessRefId, [property: AlwaysPresent] string? Screen, string QueueCode, [property: AlwaysPresent] Guid? AssigneeId, bool AssignedToMe, DateTimeOffset DueAt,
    [property: AlwaysPresent] string? SlaCode, bool Informational, [property: AlwaysPresent] Guid? ApprovalId,
    [property: AlwaysPresent] ApprovalStatus? ApprovalStatus, [property: AlwaysPresent] string? PayoutPurpose, [property: AlwaysPresent] string? PayoutCurrency,
    IReadOnlyList<ProcessTransitionView> AllowedTransitions, long RowVersion, [property: AlwaysPresent] StaticDraftDiscardNotice? DraftDiscarded = null);

public sealed record ListMyTasks(int Limit, OpenTaskCursor? After = null) : IQuery<IReadOnlyList<MyTaskView>>;

/// <summary>One of the caller's open tasks, as my tasks lists it (BR-116: its version for If-Match); not found when it isn't theirs.</summary>
public sealed record GetMyTask(Guid TaskId) : IQuery<MyTaskView>;

/// <summary>One of the caller's own requests (BR-116: its version for If-Match); not found when another person asked for it.</summary>
public sealed record GetMyApproval(Guid ApprovalId) : IQuery<ApprovalView>;

/// <summary>Claim one of the caller's tasks: assign an unassigned task in a queue they serve to themselves.</summary>
public sealed record ClaimMyTask(Guid TaskId, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

/// <summary>Complete one of the caller's tasks, with a target state the process allows; without one when it allows only one ("done").</summary>
public sealed record CompleteMyTask(Guid TaskId, string? ToState, JsonElement? Variables, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

public sealed class CompleteMyTaskValidator : AbstractValidator<CompleteMyTask>
{
    public CompleteMyTaskValidator() => RuleFor(c => c.ToState).MaximumLength(ProcessDefinition.MaxStateLength);
}

/// <summary>Whether the caller may work a task (DE-025): the rules at the top of this file.</summary>
public sealed class MyWorkAccess(IExecutionContext execution, ExecutionScope scope)
{
    public Result<Guid> User() => Checker.Require(execution);

    public MyTasksFilter Filter(Guid userId) =>
        new(userId, [.. scope.RoleCodes], WorkServers.ServedBy(scope.Functions?.Usable ?? new HashSet<string>(StringComparer.Ordinal)));

    /// <summary>Null when the user may work the task; otherwise why not.</summary>
    public Error? Refusal(ProcessInstance process, ProcessTask task, Guid userId)
    {
        if (!process.MayBeWorkedBy(userId))
        {
            return Error.Forbidden(ErrorCodes.MakerCheckerSamePerson, "You asked for the approval this task follows, so another person must do it (SRS-002).");
        }

        if (task.AssigneeId == userId)
        {
            return null;
        }

        if (task.AssigneeId is not null)
        {
            return Error.Conflict(WorkflowErrorCodes.TaskClaimedByAnother, "Someone else has taken this task.");
        }

        var filter = Filter(userId);
        var serves = filter.Queues.Contains(task.QueueCode) || filter.ServedProcesses.Contains(process.ProcessCode);
        var scoped = execution.CurrentScope;
        var inScope = !process.IsScoped || (process.OrgNodeApplies ? scoped : scoped.WithoutOrgNodes()).Allows(process.ProductCode, process.OrgNodeCode);
        return serves && inScope ? null : Error.Forbidden(WorkflowErrorCodes.TaskNotYours, "This task isn't assigned to you, and isn't in a queue your roles serve.");
    }

    public static async Task<Result<(ProcessInstance Process, ProcessTask Task)>> Load(IProcessRepository processes, Guid taskId, long expectedVersion,
        CancellationToken cancellationToken)
    {
        if (await processes.ByTask(taskId, cancellationToken) is not { } process || process.Tasks.SingleOrDefault(t => t.Id == taskId) is not { } task)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The task was not found.");
        }

        if (process.RowVersion != expectedVersion)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The task has changed since it was read.");
        }

        return task.Status != Domain.Processes.TaskStatus.Open
            ? Error.Conflict(ErrorCodes.InvalidStateTransition, "The task is not open.")
            : (process, task);
    }
}

internal sealed class ListMyTasksHandler(IProcessQueries queries, IWorkflowConfiguration configuration, IBusinessDate businessDate, MyWorkAccess access)
    : IQueryHandler<ListMyTasks, IReadOnlyList<MyTaskView>>, IQueryHandler<GetMyTask, MyTaskView>
{
    public async Task<Result<IReadOnlyList<MyTaskView>>> Handle(ListMyTasks query, CancellationToken cancellationToken)
    {
        var user = access.User();
        return user.IsFailure ? user.Error : Result.Success(await Views(user.Value, query.After, query.Limit, null, cancellationToken));
    }

    public async Task<Result<MyTaskView>> Handle(GetMyTask query, CancellationToken cancellationToken)
    {
        var user = access.User();
        if (user.IsFailure)
        {
            return user.Error;
        }

        return (await Views(user.Value, null, 1, query.TaskId, cancellationToken)) is [var view]
            ? view
            : Error.NotFound(ErrorCodes.NotFound, "The task was not found among your open tasks.");
    }

    private async Task<IReadOnlyList<MyTaskView>> Views(Guid userId, OpenTaskCursor? after, int limit, Guid? taskId, CancellationToken cancellationToken)
    {
        var definitions = new Dictionary<(string, int), ProcessDefinition?>();
        var views = new List<MyTaskView>();
        foreach (var row in await queries.MyTasks(access.Filter(userId), after, limit, cancellationToken, taskId))
        {
            var key = (row.ProcessCode, row.ProcessVersion);
            if (!definitions.TryGetValue(key, out var definition))
            {
                var read = await configuration.Definition(row.ProcessCode, row.ProcessVersion, businessDate.Today, cancellationToken);
                definitions[key] = definition = read.IsSuccess ? read.Value : null;
            }

            var transitions = definition is null
                ? []
                : definition.Transitions.Where(t => t.From == row.State).GroupBy(t => t.To).Select(g => new ProcessTransitionView(g.Key, g.All(t => t.Guard is not null)))
                    .ToList();
            views.Add(new MyTaskView(row.TaskId, row.ProcessId, row.ProcessCode, row.TaskType, row.BusinessRefType, row.BusinessRefId,
                WorkServers.ScreenFor(row.BusinessRefType), row.QueueCode, row.AssigneeId, row.AssigneeId == userId, row.DueAt, row.SlaCode,
                row.ProcessCode == BuiltInProcesses.ApprovalOutcome.ProcessCode || row.ProcessCode == BuiltInProcesses.StaticDraftDiscarded.ProcessCode, row.ApprovalId,
                row.ApprovalStatus, row.PayoutPurpose, row.PayoutCurrency, transitions, row.ProcessRowVersion,
                row.ProcessCode == BuiltInProcesses.StaticDraftDiscarded.ProcessCode && row.Notice is { } notice
                    ? JsonSerializer.Deserialize<StaticDraftDiscardNotice>(notice, StaticDraftDiscardNotice.Json)
                    : null));
        }

        return views;
    }
}

internal sealed class ClaimMyTaskHandler(IProcessRepository processes, MyWorkAccess access, IAuditTrail audit) : ICommandHandler<ClaimMyTask, long>
{
    public async Task<Result<long>> Handle(ClaimMyTask command, CancellationToken cancellationToken)
    {
        var user = access.User();
        if (user.IsFailure)
        {
            return user.Error;
        }

        var loaded = await MyWorkAccess.Load(processes, command.TaskId, command.ExpectedVersion, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var (process, task) = loaded.Value;
        if (access.Refusal(process, task, user.Value) is { } refusal)
        {
            return refusal;
        }

        if (task.AssigneeId == user.Value)
        {
            return process.RowVersion;
        }

        audit.Record(new AuditEntry("WORKFLOW", "ClaimTask", nameof(ProcessInstance), process.Id.ToString(),
            new Dictionary<string, string?> { ["task"] = task.Id.ToString(), ["assignee"] = user.Value.ToString() }));
        var result = process.AssignTask(task.Id, user.Value);
        return result.IsFailure ? result.Error : process.RowVersion;
    }
}

internal sealed class CompleteMyTaskHandler(IProcessRepository processes, MyWorkAccess access, TaskCompletion completion, IAuditTrail audit)
    : ICommandHandler<CompleteMyTask, long>
{
    public async Task<Result<long>> Handle(CompleteMyTask command, CancellationToken cancellationToken)
    {
        var user = access.User();
        if (user.IsFailure)
        {
            return user.Error;
        }

        var loaded = await MyWorkAccess.Load(processes, command.TaskId, command.ExpectedVersion, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var (process, task) = loaded.Value;
        if (access.Refusal(process, task, user.Value) is { } refusal)
        {
            return refusal;
        }

        var definition = await completion.Definition(process, cancellationToken);
        if (definition.IsFailure)
        {
            return definition.Error;
        }

        // BR-028: the target is one the process allows from its state; with only one, "done" needs none.
        var allowed = definition.Value.Transitions.Where(t => t.From == process.DefinitionState).Select(t => t.To).Distinct(StringComparer.Ordinal).ToList();
        var toState = string.IsNullOrWhiteSpace(command.ToState) ? allowed.Count == 1 ? allowed[0] : null : command.ToState;
        if (toState is null)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"Choose the state to move the process to: one of {string.Join(", ", allowed)}.",
                new Dictionary<string, string[]> { ["toState"] = ["The process allows more than one move from here; name one."] });
        }

        if (task.AssigneeId is null)
        {
            audit.Record(new AuditEntry("WORKFLOW", "ClaimTask", nameof(ProcessInstance), process.Id.ToString(),
                new Dictionary<string, string?> { ["task"] = task.Id.ToString(), ["assignee"] = user.Value.ToString() }));
            process.AssignTask(task.Id, user.Value);
        }

        var result = await completion.Complete(process, task.Id, toState, command.Variables, cancellationToken);
        return result.IsFailure ? result.Error : process.RowVersion;
    }
}

// ---- The caller's own requests (DE-044) ---------------------------------------------------------------------------------------

/// <summary>The caller's own requests for approval, newest first, with their status and decision; optionally of one status.</summary>
public sealed record ListMyApprovals(string? Status, Guid? BeforeId, int Limit) : IQuery<IReadOnlyList<ApprovalView>>;

internal sealed class ListMyApprovalsHandler(IApprovalQueries queries, IExecutionContext execution)
    : IQueryHandler<ListMyApprovals, IReadOnlyList<ApprovalView>>, IQueryHandler<GetMyApproval, ApprovalView>
{
    public async Task<Result<ApprovalView>> Handle(GetMyApproval query, CancellationToken cancellationToken)
    {
        var user = Checker.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        return await queries.Mine(user.Value, null, null, 1, cancellationToken, query.ApprovalId) is [var view]
            ? view
            : Error.NotFound(ErrorCodes.NotFound, "The approval was not found among your requests.");
    }

    public async Task<Result<IReadOnlyList<ApprovalView>>> Handle(ListMyApprovals query, CancellationToken cancellationToken)
    {
        var user = Checker.Require(execution);
        return user.IsFailure ? user.Error : Result.Success(await queries.Mine(user.Value, query.Status, query.BeforeId, query.Limit, cancellationToken));
    }
}
