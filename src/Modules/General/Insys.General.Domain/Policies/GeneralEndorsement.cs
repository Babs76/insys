/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : General
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
using Insys.BuildingBlocks.Domain;
using Insys.General.Domain.Underwriting;

namespace Insys.General.Domain.Policies;

/// <summary>States from domain-model.json (general › GeneralEndorsement).</summary>
public enum EndorsementStatus
{
    Draft,
    Rated,
    PendingApproval,
    Approved,
    Rejected,
    Applied,
}

public enum EndorsementTrigger
{
    RateEndorsement,
    SubmitEndorsement,
    ApproveEndorsement,
    RejectEndorsement,
    PolicyUpdated,
}

public enum EndorsementAction
{
    AttachRisk,
    DetachRisk,
    ChangeRisk,
}

/// <summary>
/// One change of an endorsement's change set: attach a risk object with its coverages, detach one, or change one's sum
/// insured and coverages (a coverage with sum insured 0 is removed).
/// </summary>
public sealed record EndorsementChange(EndorsementAction Action, Guid RiskObjectId, decimal? SumInsured, IReadOnlyList<CoverageRequest> Coverages);

/// <summary>An endorsement type (general.cfg_endorsement_types): how its premium change is charged.</summary>
public sealed record EndorsementType(string Code, string PremiumMethod, IReadOnlyList<string> Documents, bool RequiresUnderwriting);

/// <summary>Asks Workflow for a second person's approval (SRS-002, ADR-009).</summary>
/// <param name="ExceededLimit">The zone and peril limit the endorsement's added sum insured would exceed (F062 §1), for the approver.</param>
public sealed record EndorsementSubmitted(Guid EndorsementId, string EndorsementNumber, Guid RequestedBy, decimal PremiumDelta, string Currency, DateTimeOffset OccurredAt,
    string? ExceededLimit = null)
    : IDomainEvent;

public sealed record GeneralEndorsementApproved(Guid EndorsementId, Guid PolicyId, Guid ApprovalId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The GeneralEndorsement aggregate (general.endorsement): effective within the policy period (GEN-003); its premium
/// change comes from a rating request; it changes the policy only after approval.
/// </summary>
public sealed class GeneralEndorsement : AggregateRoot<Guid>
{
    public static readonly StateMachine<EndorsementStatus, EndorsementTrigger> Lifecycle = new StateMachine<EndorsementStatus, EndorsementTrigger>()
        .Allow(EndorsementStatus.Draft, EndorsementTrigger.RateEndorsement, EndorsementStatus.Rated)
        .Allow(EndorsementStatus.Rated, EndorsementTrigger.SubmitEndorsement, EndorsementStatus.PendingApproval)
        .Allow(EndorsementStatus.PendingApproval, EndorsementTrigger.ApproveEndorsement, EndorsementStatus.Approved)
        .Allow(EndorsementStatus.PendingApproval, EndorsementTrigger.RejectEndorsement, EndorsementStatus.Rejected)
        .Allow(EndorsementStatus.Approved, EndorsementTrigger.PolicyUpdated, EndorsementStatus.Applied);

    private List<EndorsementChange> _changes = [];

    private GeneralEndorsement()
    {
    }

    public string EndorsementNumber { get; private set; } = "";

    public Guid PolicyId { get; private set; }

    public string EndorsementType { get; private set; } = "";

    public DateOnly EffectiveDate { get; private set; }

    /// <summary>The premium charged for the rest of the period by the type's premium method.</summary>
    public decimal PremiumDelta { get; private set; }

    public decimal? NewAnnualPremium { get; private set; }

    public EndorsementStatus Status { get; private set; }

    public IReadOnlyList<EndorsementChange> Changes
    {
        get => _changes;
        private set => _changes = value.ToList();
    }

    public Guid? DocumentId { get; private set; }

    public Guid? RatingRequestId { get; private set; }

    public Guid? RequestedBy { get; private set; }

    public Guid? ApprovalId { get; private set; }

    public Guid? DecidedBy { get; private set; }

    /// <summary>Command DraftEndorsement: effective within the policy period (GEN-003), with at least one change.</summary>
    public static Result<GeneralEndorsement> DraftEndorsement(Guid id, string number, GeneralPolicy policy, EndorsementType type, DateOnly effectiveDate,
        IReadOnlyList<EndorsementChange> changes, Guid requestedBy)
    {
        if (policy.Status is not (PolicyStatus.Issued or PolicyStatus.InForce))
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Policy {policy.PolicyNumber} is {policy.Status}.");
        }

        if (effectiveDate < policy.PeriodStart || effectiveDate > policy.PeriodEnd)
        {
            return Error.Validation(GeneralErrorCodes.OutsidePolicyPeriod,
                $"{effectiveDate:yyyy-MM-dd} is outside the policy period {policy.PeriodStart:yyyy-MM-dd} to {policy.PeriodEnd:yyyy-MM-dd} (GEN-003).");
        }

        if (changes.Count == 0)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "An endorsement changes something.");
        }

        var endorsement = new GeneralEndorsement
        {
            Id = Guard.NotEmpty(id), EndorsementNumber = Guard.MaxLength(number, 20), PolicyId = policy.Id, EndorsementType = Guard.MaxLength(type.Code, 15),
            EffectiveDate = effectiveDate, _changes = changes.ToList(), RequestedBy = requestedBy, Status = EndorsementStatus.Draft,
        };
        endorsement.MarkChanged();
        return endorsement;
    }

    /// <summary>
    /// Command RateEndorsement, from Rating's PremiumCalculated: the new annual premium of the endorsed policy. The
    /// premium change for the rest of the period follows the type's method: pro rata, flat (the whole annual change) or
    /// none. TODO(spec): the short-period scale is not specified (OQ-059).
    /// </summary>
    public Result RateEndorsement(Guid calculationId, decimal newAnnualPremium, GeneralPolicy policy, EndorsementType type)
    {
        if (RatingRequestId == calculationId)
        {
            return Result.Success();
        }

        var change = newAnnualPremium - policy.AnnualPremium;
        decimal? delta = type.PremiumMethod switch
        {
            "PRO_RATA" => Math.Round(change * policy.DaysFrom(EffectiveDate) / policy.PeriodDays, 2, MidpointRounding.AwayFromZero),
            "FLAT" => change,
            "NONE" => 0m,
            _ => null,
        };
        if (delta is null)
        {
            return Error.Validation(GeneralErrorCodes.RuleNotSpecified, $"The {type.PremiumMethod} premium method has no scale configured.");
        }

        var result = Fire(EndorsementTrigger.RateEndorsement);
        if (result.IsSuccess)
        {
            RatingRequestId = calculationId;
            NewAnnualPremium = newAnnualPremium;
            PremiumDelta = delta.Value;
        }

        return result;
    }

    /// <summary>Command SubmitEndorsement: Workflow asks a second person to approve it.</summary>
    public Result SubmitEndorsement(Guid submittedBy, string currency, DateTimeOffset now, string? exceededLimit = null)
    {
        var result = Fire(EndorsementTrigger.SubmitEndorsement);
        if (result.IsSuccess)
        {
            RequestedBy = submittedBy;
            Raise(new EndorsementSubmitted(Id, EndorsementNumber, submittedBy, PremiumDelta, currency, now, exceededLimit));
        }

        return result;
    }

    /// <summary>Commands ApproveEndorsement and RejectEndorsement, from Workflow's decision. The checker is never the maker.</summary>
    public Result Decide(bool approved, Guid approvalId, Guid checkerId, DateTimeOffset now)
    {
        if (checkerId == RequestedBy)
        {
            return MakerChecker.EnsureDifferentPeople(RequestedBy.ToString()!, checkerId.ToString());
        }

        var result = Fire(approved ? EndorsementTrigger.ApproveEndorsement : EndorsementTrigger.RejectEndorsement);
        if (result.IsSuccess)
        {
            ApprovalId = approvalId;
            DecidedBy = checkerId;
            if (approved)
            {
                Raise(new GeneralEndorsementApproved(Id, PolicyId, approvalId, now));
            }
        }

        return result;
    }

    /// <summary>Transition "Policy updated": the policy has applied the changes.</summary>
    public Result MarkApplied() => Status == EndorsementStatus.Applied ? Result.Success() : Fire(EndorsementTrigger.PolicyUpdated);

    /// <summary>The endorsement document, once Documents has generated it (GEN-003).</summary>
    public void AttachDocument(Guid documentId)
    {
        DocumentId = documentId;
        MarkChanged();
    }

    private Result Fire(EndorsementTrigger trigger)
    {
        var next = Lifecycle.Fire(Status, trigger);
        if (next.IsFailure)
        {
            return next.Error;
        }

        Status = next.Value;
        MarkChanged();
        return Result.Success();
    }
}
