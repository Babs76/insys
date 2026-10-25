/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Life
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
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.ClaimsFramework.Contracts;
using Insys.Life.Contracts;
using Insys.Life.Domain.Underwriting;
using Insys.ProductFactory.Contracts;
using Insys.Rating.Contracts;
using Insys.Rules.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.Logging;

namespace Insys.Life.Application;

/// <summary>
/// Life's policies (domain-model.json: life consumes PremiumCalculated, RuleDecisionRecorded, ArrearsDetected,
/// PaymentAllocated, ClaimPaymentCompleted), Workflow's decisions, and the reactions that keep its own aggregates
/// consistent through its events. PaymentAllocated carries no policy, so a payment in grace is seen through Billing's
/// InvoiceSettled, which does.
/// </summary>
public sealed class LifeEventPolicies(ILifeRepository life, ILifeCodes codes, IPolicyArrears arrears, ISellableProducts products, ITenantContext tenant, IBusinessDate businessDate, IClock clock,
    ILogger<LifeEventPolicies> logger) :
    IIntegrationEventHandler<PremiumCalculated>,
    IIntegrationEventHandler<RuleDecisionRecorded>,
    IIntegrationEventHandler<Contracts.LifeProposalSubmitted>,
    IIntegrationEventHandler<Contracts.LifeUnderwritingCaseOpened>,
    IIntegrationEventHandler<Contracts.LifeUnderwritingDecided>,
    IIntegrationEventHandler<Contracts.LifePolicyIssued>,
    IIntegrationEventHandler<ApprovalGranted>,
    IIntegrationEventHandler<ApprovalRejected>,
    IIntegrationEventHandler<ArrearsDetected>,
    IIntegrationEventHandler<InvoiceSettled>,
    IIntegrationEventHandler<ClaimPaymentCompleted>,
    IIntegrationEventHandler<Contracts.LifeClaimApproved>,
    IIntegrationEventHandler<Contracts.LifeClaimSettled>
{
    /// <summary>PremiumCalculated → QuotePremium on the proposal it was calculated for (LIF-001). A mismatch is logged, not attached.</summary>
    public async Task Handle(PremiumCalculated calculated, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (calculated.SourceDomain != LifeSubjects.SourceDomain || calculated.SubjectType != LifeSubjects.Proposal
            || await life.Proposal(calculated.SubjectId, cancellationToken) is not { } proposal)
        {
            return;
        }

        var result = proposal.QuotePremium(calculated.CalculationId, calculated.ProductCode, calculated.ProductVersion, calculated.RatingVersion, calculated.Premium,
            calculated.Currency);
        if (result.IsFailure)
        {
            logger.LogWarning("Premium calculation {CalculationId} not attached to proposal {ProposalId}: {Code}", calculated.CalculationId, proposal.Id, result.Error.Code);
        }
    }

    /// <summary>
    /// RuleDecisionRecorded → attach the eligibility result to the proposal (LIF-001, LIF-002), only from the rule set its
    /// product version names (eligibility_ruleset_code); a product version without one accepts no eligibility result.
    /// </summary>
    public async Task Handle(RuleDecisionRecorded decided, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        // The underwriting rule set's outcome for a case decides whether one underwriter may decide alone.
        if (decided.SubjectType == LifeSubjects.UnderwritingCase && await life.Case(decided.SubjectId, cancellationToken) is { } underwritingCase)
        {
            var recorded = underwritingCase.RecordRuleOutcome(decided.DecisionId, decided.Outcome);
            if (recorded.IsFailure)
            {
                logger.LogWarning("Rule decision {DecisionId} not attached to case {CaseId}: {Code}", decided.DecisionId, underwritingCase.Id, recorded.Error.Code);
            }

            return;
        }

        if (decided.SubjectType == LifeSubjects.Proposal && await life.Proposal(decided.SubjectId, cancellationToken) is { } proposal)
        {
            var product = await products.Version(tenant.RequireTenantId(), proposal.ProductCode, proposal.ProductVersion, businessDate.Today, cancellationToken);
            if (product?.EligibilityRulesetCode is not { } ruleset || ruleset != decided.RulesetCode)
            {
                logger.LogWarning("Rule decision {DecisionId} not attached to proposal {ProposalId}: rule set {RulesetCode} is not the product's eligibility rule set {Expected}",
                    decided.DecisionId, proposal.Id, decided.RulesetCode, product?.EligibilityRulesetCode);
                return;
            }

            var result = proposal.RecordEligibility(decided.DecisionId, decided.RulesetCode, decided.RulesetVersion, decided.Outcome);
            if (result.IsFailure)
            {
                logger.LogWarning("Rule decision {DecisionId} not attached to proposal {ProposalId}: {Code}", decided.DecisionId, proposal.Id, result.Error.Code);
            }
        }
    }

    /// <summary>A submitted proposal → OpenCase (process "Life proposal to policy").</summary>
    public async Task Handle(Contracts.LifeProposalSubmitted submitted, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await life.CaseForProposal(submitted.ProposalId, cancellationToken) is not null || await life.Proposal(submitted.ProposalId, cancellationToken) is not { } proposal)
        {
            return;
        }

        var opened = LifeUnderwritingCase.OpenCase(Guid.CreateVersion7(), proposal, clock.UtcNow);
        if (opened.IsSuccess)
        {
            life.Add(opened.Value);
        }
    }

    /// <summary>The case was opened: the proposal moves to underwriting ("Case opened").</summary>
    public async Task Handle(Contracts.LifeUnderwritingCaseOpened opened, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await life.Proposal(opened.ProposalId, cancellationToken) is { } proposal)
        {
            Ensure(proposal.OpenUnderwriting(), $"Proposal {proposal.ProposalNumber} could not move to underwriting");
        }
    }

    /// <summary>LifeUnderwritingDecided → the proposal is accepted, declined or postponed.</summary>
    public async Task Handle(Contracts.LifeUnderwritingDecided decided, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await life.Proposal(decided.ProposalId, cancellationToken) is { } proposal && Enum.TryParse<UnderwritingOutcome>(decided.Outcome, true, out var outcome))
        {
            Ensure(proposal.ApplyDecision(outcome, clock.UtcNow), $"Proposal {proposal.ProposalNumber} could not take the decision");
        }
    }

    /// <summary>The policy was issued: the proposal is converted ("Policy issued").</summary>
    public async Task Handle(Contracts.LifePolicyIssued issued, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await life.Proposal(issued.ProposalId, cancellationToken) is { } proposal)
        {
            Ensure(proposal.MarkConverted(), $"Proposal {proposal.ProposalNumber} could not be converted");
        }
    }

    /// <summary>
    /// ApprovalGranted → the underwriting decision or the claim is approved. A redelivery of a grant already applied
    /// changes nothing; a record that will not take it (it changed after it was submitted: no decision waiting any more, or
    /// not the one approved) parks the delivery (APPROVAL_NOT_APPLIED; D-5, ADR-022), and Workflow marks the approval not
    /// applied and tells the requester.
    /// </summary>
    public Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(granted.BusinessRefType, granted.BusinessRefId, approved: true, granted.ApprovalId, granted.CheckerId, granted.ApprovalType, granted.SubjectHash,
            cancellationToken);

    /// <summary>
    /// ApprovalRejected (rejected, expired or withdrawn) → the proposed decision is cleared and assessment continues. A
    /// redelivered ending changes nothing; one the record will not take parks the delivery (APPROVAL_ENDING_NOT_APPLIED).
    /// </summary>
    public Task Handle(ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(rejected.BusinessRefType, rejected.BusinessRefId, approved: false, rejected.ApprovalId, rejected.CheckerId, rejected.ApprovalType, rejected.SubjectHash,
            cancellationToken);

    /// <summary>ArrearsDetected → RecordMissedPremium on each of the account's policies: their overdue invoices, and the grace period from the oldest.</summary>
    public async Task Handle(ArrearsDetected detected, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        foreach (var policyId in await life.PoliciesOnAccount(detected.AccountId, cancellationToken))
        {
            await RefreshArrears(policyId, cancellationToken);
        }
    }

    /// <summary>An invoice of a Life policy was paid: a payment received in grace clears the missed premium it covered.</summary>
    public Task Handle(InvoiceSettled settled, EventEnvelope envelope, CancellationToken cancellationToken) =>
        settled.SourceDomain == LifeSubjects.SourceDomain ? RefreshArrears(settled.PolicyRef, cancellationToken) : Task.CompletedTask;

    /// <summary>ClaimPaymentCompleted → mark the claim settled once the approved amount is paid.</summary>
    public async Task Handle(ClaimPaymentCompleted completed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (completed.Domain == LifeSubjects.SourceDomain && await life.ClaimByNumber(completed.ClaimNumber, cancellationToken) is { } claim)
        {
            Ensure(claim.RecordPayment(completed.Amount, completed.PaidOn, clock.UtcNow), $"Claim {claim.ClaimNumber} could not record its payment");
        }
    }

    /// <summary>An approved claim settles the policy's loans (OffsetAgainstBenefit).</summary>
    public async Task Handle(Contracts.LifeClaimApproved approved, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (approved.LoanOffset <= 0)
        {
            return;
        }

        foreach (var loan in await life.ActiveLoans(approved.PolicyId, cancellationToken))
        {
            loan.OffsetAgainstBenefit(businessDate.Today);
        }
    }

    /// <summary>A settled claim closes the benefits it paid (CloseOnClaim).</summary>
    public async Task Handle(Contracts.LifeClaimSettled settled, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await life.Policy(settled.PolicyId, cancellationToken) is { } policy)
        {
            Ensure(policy.CloseOnClaim(settled.PolicyBenefitIds, settled.PaidOn, clock.UtcNow), $"Policy {policy.PolicyNumber} could not close on claim");
        }
    }

    private async Task RefreshArrears(Guid policyId, CancellationToken cancellationToken)
    {
        if (await life.Policy(policyId, cancellationToken) is not { } policy)
        {
            return;
        }

        if (await codes.GraceRule(policy.PremiumFrequency, businessDate.Today, cancellationToken) is not { } grace)
        {
            throw new InvalidOperationException($"No grace period rule is in force for {policy.PremiumFrequency}; policy {policy.PolicyNumber}'s arrears are not recorded.");
        }

        var overdue = await arrears.For(tenant.RequireTenantId(), LifeSubjects.SourceDomain, policy.Id, businessDate.Today, cancellationToken);
        policy.RecordArrears(overdue.OverdueInstalments, overdue.OldestDueDate, grace);
    }

    private async Task Decide(string businessRefType, Guid id, bool approved, Guid approvalId, Guid checkerId, string approvalType, string? subjectHash,
        CancellationToken cancellationToken)
    {
        Result? decided = null;
        if (businessRefType == LifeApprovals.UnderwritingBusinessRefType && await life.Case(id, cancellationToken) is { } underwritingCase)
        {
            if (approved ? underwritingCase.ApprovalApplied(approvalId) : underwritingCase.EndingApplied(approvalId))
            {
                return;
            }

            decided = underwritingCase.Decide(approved, approvalId, checkerId, subjectHash, approvalType, clock.UtcNow);
        }
        else if (businessRefType == LifeApprovals.ClaimBusinessRefType && await life.Claim(id, cancellationToken) is { } claim)
        {
            if (approved ? claim.ApprovalApplied(approvalId) : claim.EndingApplied(approvalId))
            {
                return;
            }

            decided = claim.DecideApproval(approved, approvalId, checkerId, subjectHash, clock.UtcNow);
        }

        // D-5: a decision the record would not take parks the delivery (ADR-022) for a retry and escalation, never only logged:
        // an ending (rejected, expired, withdrawn) as APPROVAL_ENDING_NOT_APPLIED, a grant as APPROVAL_NOT_APPLIED.
        if (decided is { IsFailure: true })
        {
            throw new PrerequisiteMissingException(approved ? PrerequisiteCodes.ApprovalNotApplied : PrerequisiteCodes.ApprovalEndingNotApplied,
                $"{approvalType}:{approvalId}");
        }
    }

    /// <summary>A reaction that cannot apply is retried and then dead-lettered for a person; it is never dropped.</summary>
    private static void Ensure(Result result, string what)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"{what}: {result.Error.Message}");
        }
    }
}

public sealed record LifeDueWork(IReadOnlyList<Guid> PoliciesToStart, IReadOnlyList<Guid> PoliciesPastGrace, IReadOnlyList<Guid> LapsedPolicies,
    IReadOnlyList<Guid> SchemesToActivate);

/// <summary>The daily jobs' work: policies whose inception has come, policies past grace, lapsed policies, and schemes whose start has come.</summary>
public sealed record FindLifeDueWork(DateOnly Day, int Limit) : IQuery<LifeDueWork>;

public enum LifeDueAction
{
    StartCover,
    Lapse,
    Terminate,
    ActivateScheme,
}

public sealed record RunLifeDue(Guid Id, LifeDueAction Action, DateOnly Day) : ICommand<bool>;

internal sealed class FindLifeDueWorkHandler(ILifeRepository life) : IQueryHandler<FindLifeDueWork, LifeDueWork>
{
    public async Task<Result<LifeDueWork>> Handle(FindLifeDueWork query, CancellationToken cancellationToken) =>
        new LifeDueWork(await life.PoliciesToStart(query.Day, query.Limit, cancellationToken), await life.PoliciesPastGrace(query.Day, query.Limit, cancellationToken),
            await life.LapsedPolicies(query.Limit, cancellationToken), await life.SchemesToActivate(query.Day, query.Limit, cancellationToken));
}

/// <summary>
/// Transitions "Inception date reached", LapsePolicy (process "Missed premium to lapse": when grace ends and missed
/// payments reach the product's limit, with the arrears read again from Billing), "Reinstatement window passed" and
/// "Start date reached".
/// </summary>
internal sealed class RunLifeDueHandler(ILifeRepository life, ILifeCodes codes, IPolicyArrears arrears, ITenantContext tenant, IClock clock)
    : ICommandHandler<RunLifeDue, bool>
{
    public async Task<Result<bool>> Handle(RunLifeDue command, CancellationToken cancellationToken)
    {
        if (command.Action == LifeDueAction.ActivateScheme)
        {
            return await life.Scheme(command.Id, cancellationToken) is { } scheme ? Done(scheme.Activate(command.Day)) : false;
        }

        if (await life.Policy(command.Id, cancellationToken) is not { } policy)
        {
            return false;
        }

        if (command.Action == LifeDueAction.StartCover)
        {
            return Done(policy.StartCover(command.Day));
        }

        if (await codes.LapseRule(policy.ProductCode, command.Day, cancellationToken) is not { } rule)
        {
            return Error.Validation(Domain.LifeErrorCodes.CodeNotInForce, $"{policy.ProductCode} has no lapse and reinstatement rule in force.");
        }

        if (command.Action == LifeDueAction.Terminate)
        {
            return Done(policy.TerminateLapsed(rule, command.Day, clock.UtcNow));
        }

        if (await codes.GraceRule(policy.PremiumFrequency, command.Day, cancellationToken) is { } grace)
        {
            var overdue = await arrears.For(tenant.RequireTenantId(), LifeSubjects.SourceDomain, policy.Id, command.Day, cancellationToken);
            policy.RecordArrears(overdue.OverdueInstalments, overdue.OldestDueDate, grace);
        }

        return Done(policy.LapsePolicy(rule, command.Day, clock.UtcNow));
    }

    private static Result<bool> Done(Result result) => result.IsFailure ? result.Error : true;
}
