using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.ClaimsFramework.Contracts;
using Insys.Documents.Contracts;
using Insys.General.Contracts;
using AssessmentStatus = Insys.General.Domain.Claims.AssessmentStatus;
using LossAssessment = Insys.General.Domain.Claims.LossAssessment;
using Insys.ProductFactory.Contracts;
using Insys.Rating.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.Logging;

namespace Insys.General.Application;

/// <summary>
/// General's policies (domain-model.json: general consumes PremiumCalculated, ApprovalGranted, ClaimPaymentCompleted),
/// and the reactions that keep its own aggregates consistent with each other through its events.
/// </summary>
public sealed class GeneralPolicies(IGeneralRepository general, IGeneralCodes codes, ISellableProducts products, ITenantContext tenant, IClock clock,
    ILogger<GeneralPolicies> logger) :
    IIntegrationEventHandler<PremiumCalculated>,
    IIntegrationEventHandler<ApprovalGranted>,
    IIntegrationEventHandler<ApprovalRejected>,
    IIntegrationEventHandler<ClaimPaymentCompleted>,
    IIntegrationEventHandler<DocumentGenerated>,
    IIntegrationEventHandler<GeneralEndorsementApproved>,
    IIntegrationEventHandler<GeneralPolicyEndorsed>,
    IIntegrationEventHandler<GeneralPolicyIssued>,
    IIntegrationEventHandler<AssessorAppointed>
{
    /// <summary>
    /// PremiumCalculated → attach the premium to the quote or endorsement it was calculated for (RateQuote,
    /// RateEndorsement; PRD-008). A calculation that does not match its subject is logged and not attached.
    /// </summary>
    public async Task Handle(PremiumCalculated calculated, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (calculated.SourceDomain != GeneralRatingSubjects.SourceDomain)
        {
            return;
        }

        BuildingBlocks.Domain.Result result;
        if (calculated.SubjectType == GeneralRatingSubjects.Quote && await general.Quote(calculated.SubjectId, cancellationToken) is { } quote)
        {
            result = quote.RateQuote(calculated.CalculationId, calculated.ProductCode, calculated.ProductVersion, calculated.RatingVersion, calculated.Premium, calculated.Currency);
        }
        else if (calculated.SubjectType == GeneralRatingSubjects.Endorsement && await general.Endorsement(calculated.SubjectId, cancellationToken) is { } endorsement
                 && await general.Policy(endorsement.PolicyId, cancellationToken) is { } policy
                 && await codes.EndorsementType(endorsement.EndorsementType, clock.UtcToday, cancellationToken) is { } type)
        {
            result = calculated.ProductCode == policy.ProductCode && calculated.Currency == policy.Currency
                ? endorsement.RateEndorsement(calculated.CalculationId, calculated.Premium, policy, type)
                : BuildingBlocks.Domain.Error.Validation(Domain.GeneralErrorCodes.RatingMismatch, "The calculation is for another product or currency.");
        }
        else
        {
            return;
        }

        if (result.IsFailure)
        {
            logger.LogWarning("Premium calculation {CalculationId} not attached to {SubjectType} {SubjectId}: {Code}", calculated.CalculationId, calculated.SubjectType,
                calculated.SubjectId, result.Error.Code);
        }
    }

    /// <summary>ApprovalGranted → ApproveEndorsement or ApproveSettlement.</summary>
    public Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(granted.BusinessRefType, granted.BusinessRefId, approved: true, granted.ApprovalId, granted.CheckerId, cancellationToken);

    /// <summary>ApprovalRejected → RejectEndorsement, or the claim stays under assessment.</summary>
    public Task Handle(ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(rejected.BusinessRefType, rejected.BusinessRefId, approved: false, rejected.ApprovalId, rejected.CheckerId, cancellationToken);

    /// <summary>ClaimPaymentCompleted → SettleClaim.</summary>
    public async Task Handle(ClaimPaymentCompleted completed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (completed.Domain == "GENERAL" && await general.ClaimByNumber(completed.ClaimNumber, cancellationToken) is { } claim)
        {
            claim.SettleClaim(completed.PaidOn, clock.UtcNow);
        }
    }

    /// <summary>The endorsement's document, once generated (GEN-003).</summary>
    public async Task Handle(DocumentGenerated generated, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (generated.BusinessRefType == GeneralDocuments.EndorsementRefType && await general.Endorsement(generated.BusinessRefId, cancellationToken) is { DocumentId: null } endorsement)
        {
            endorsement.AttachDocument(generated.DocumentId);
        }
    }

    /// <summary>An approved endorsement is applied to the policy (ApplyEndorsement).</summary>
    public async Task Handle(GeneralEndorsementApproved approved, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await general.Endorsement(approved.EndorsementId, cancellationToken) is not { } endorsement || await general.Policy(approved.PolicyId, cancellationToken) is not { } policy)
        {
            return;
        }

        var product = await Products.Issued(products, tenant.RequireTenantId(), policy.ProductCode, policy.ProductVersion, clock.UtcToday, cancellationToken);
        var type = await codes.EndorsementType(endorsement.EndorsementType, clock.UtcToday, cancellationToken);
        var applied = product.IsFailure ? product : policy.ApplyEndorsement(endorsement, product.Value, type?.Documents ?? [], clock.UtcNow);
        if (applied.IsFailure)
        {
            // Approved but not applicable (a limit breached since, say): retried, then dead-lettered for a person.
            throw new InvalidOperationException($"Endorsement {endorsement.EndorsementNumber} could not be applied: {applied.Error.Message}");
        }
    }

    /// <summary>The policy applied the endorsement: transition "Policy updated".</summary>
    public async Task Handle(GeneralPolicyEndorsed endorsed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await general.Endorsement(endorsed.EndorsementId, cancellationToken) is { } endorsement)
        {
            endorsement.MarkApplied();
        }
    }

    /// <summary>A renewal's policy is issued: the policy it renews becomes Renewed; the quote's proposal is converted.</summary>
    public async Task Handle(GeneralPolicyIssued issued, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (issued.RenewalOfId is { } renewed && await general.Policy(renewed, cancellationToken) is { Status: Domain.Policies.PolicyStatus.InForce } expiring)
        {
            expiring.MarkRenewed(issued.PolicyId, clock.UtcNow);
        }

        if (await general.Policy(issued.PolicyId, cancellationToken) is { QuoteId: { } quoteId } && await general.ProposalForQuote(quoteId, cancellationToken) is { } proposal)
        {
            proposal.Convert();
        }
    }

    /// <summary>An assessor was appointed: the loss assessment is assigned (the panel was checked when appointing).</summary>
    public async Task Handle(AssessorAppointed appointed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var assessments = await general.Assessments(appointed.ClaimId, cancellationToken);
        if (assessments.Any(a => a.AssessorPartyId == appointed.AssessorPartyId && a.Status != AssessmentStatus.Accepted))
        {
            return;
        }

        var assessment = LossAssessment.AssignAssessor(Guid.CreateVersion7(), appointed.ClaimId, appointed.AssessorPartyId, onApprovedPanel: true);
        general.Add(assessment.Value);
    }

    private async Task Decide(string businessRefType, Guid id, bool approved, Guid approvalId, Guid checkerId, CancellationToken cancellationToken)
    {
        if (businessRefType == GeneralApprovals.EndorsementBusinessRefType && await general.Endorsement(id, cancellationToken) is { } endorsement)
        {
            endorsement.Decide(approved, approvalId, checkerId, clock.UtcNow);
        }
        else if (businessRefType == GeneralApprovals.SettlementBusinessRefType && await general.Claim(id, cancellationToken) is { } claim)
        {
            claim.DecideSettlement(approved, approvalId, checkerId, clock.UtcNow);
        }
    }
}

public sealed record DueWork(IReadOnlyList<Guid> PoliciesToStart, IReadOnlyList<Guid> PoliciesToExpire, IReadOnlyList<Guid> QuotesToExpire);

/// <summary>The daily jobs' work: policies whose period starts or ends, and quotes past validity.</summary>
public sealed record FindDueWork(DateOnly Day, int Limit) : IQuery<DueWork>;

public enum DueAction
{
    StartPeriod,
    ExpirePolicy,
    ExpireQuote,
}

public sealed record RunDue(Guid Id, DueAction Action, DateOnly Day) : ICommand<bool>;

internal sealed class FindDueWorkHandler(IGeneralRepository general) : IQueryHandler<FindDueWork, DueWork>
{
    public async Task<BuildingBlocks.Domain.Result<DueWork>> Handle(FindDueWork query, CancellationToken cancellationToken) =>
        new DueWork(await general.PoliciesToStart(query.Day, query.Limit, cancellationToken), await general.PoliciesToExpire(query.Day, query.Limit, cancellationToken),
            await general.QuotesToExpire(query.Day, query.Limit, cancellationToken));
}

/// <summary>Transitions "Period starts", "Period ends without renewal" and "Validity ends".</summary>
internal sealed class RunDueHandler(IGeneralRepository general, IClock clock) : ICommandHandler<RunDue, bool>
{
    public async Task<BuildingBlocks.Domain.Result<bool>> Handle(RunDue command, CancellationToken cancellationToken)
    {
        BuildingBlocks.Domain.Result result;
        switch (command.Action)
        {
            case DueAction.ExpireQuote:
                if (await general.Quote(command.Id, cancellationToken) is not { } quote)
                {
                    return false;
                }

                result = quote.ExpireQuote(command.Day, clock.UtcNow);
                break;
            default:
                if (await general.Policy(command.Id, cancellationToken) is not { } policy)
                {
                    return false;
                }

                result = command.Action == DueAction.StartPeriod ? policy.StartPeriod(command.Day) : policy.ExpirePolicy(command.Day);
                break;
        }

        return result.IsFailure ? result.Error : true;
    }
}
