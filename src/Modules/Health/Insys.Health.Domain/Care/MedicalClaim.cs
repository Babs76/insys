/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Health
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
using Insys.Health.Domain.Adjudication;

namespace Insys.Health.Domain.Care;

/// <summary>States from domain-model.json (health › MedicalClaim).</summary>
public enum MedicalClaimStatus
{
    Received,
    Validated,
    Adjudicated,
    Pended,
    ApprovedForPayment,
    Paid,
    Rejected,
    Reversed,
}

public enum MedicalClaimTrigger
{
    ValidateClaim,
    AdjudicateClaim,
    PendClaim,
    InformationReceived,
    ApproveForPayment,
    PaymentRunReleased,
    RejectClaim,
    ReverseClaim,

    /// <summary>A dispute or appeal of a rejected claim was upheld: the claim is validated again and adjudicated (F013, OQ-103).</summary>
    ReopenClaim,
}

/// <summary>The claim's approval as an outcome (OQ-102): approved for less than was claimed, it is a partial approval.</summary>
public static class MedicalClaimOutcomes
{
    public const string Approved = "APPROVED";
    public const string PartlyApproved = "PARTLY_APPROVED";

    /// <summary>The decision type a partial approval's reason has (claims.ref_claim_decision_reason_codes).</summary>
    public const string PartialReasonType = "PARTIAL";
}

public static class ClaimSources
{
    public const string Provider = "PROVIDER";
    public const string Member = "MEMBER";
}

public sealed record ClaimLineRequest(string TariffCode, string? DiagnosisCode, int Quantity, decimal ClaimedAmount, IReadOnlyList<string>? Modifiers);

public sealed record MedicalClaimSubmitted(Guid ClaimId, string ClaimNumber, string Source, string? ProviderId, Guid MembershipId, DateOnly ServiceDate, decimal TotalClaimed,
    string Currency, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record HighCostLine(int LineNo, string RuleCode);

/// <summary>The claim was adjudicated: the authorisations it used, and the lines above a high-cost threshold.</summary>
public sealed record MedicalClaimAdjudicated(Guid ClaimId, string ClaimNumber, Guid MembershipId, bool Pended, decimal TotalPaid, string Currency,
    IReadOnlyList<AuthorisationUse> AuthorisationUses, IReadOnlyList<HighCostLine> HighCost, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record MedicalClaimSettled(Guid ClaimId, string ClaimNumber, string? ProviderId, Guid PaymentRunId, decimal Amount, string Currency, DateOnly PaidOn,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record MedicalClaimRejected(Guid ClaimId, string ClaimNumber, Guid MembershipId, string ReasonCode, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// A claim was partly approved (OQ-102): the member is sent their rights on the declined part (CLAIM_DECISION_RIGHTS). As
/// every Health notice, it carries the claim number, the date and the total paid only: never a reason or a line (HLT-008).
/// </summary>
/// <param name="DecisionReasons">
/// The decline reason's configured customer wording (Ade, 29 October 2026), never the claim's own reason text, lines or
/// diagnoses (HLT-008); null when the reason has no wording in force.
/// </param>
public sealed record MedicalClaimDecisionRightsDue(Guid ClaimId, string ClaimNumber, Guid RecipientPartyId, string? ProductCode, string? OrgNodeCode, decimal TotalPaid,
    string Currency, DateOnly DecidedOn, DateTimeOffset OccurredAt, string? DecisionReasons = null) : IDomainEvent;

public sealed record MedicalClaimReversed(Guid ClaimId, string ClaimNumber, string? ProviderId, decimal Amount, string Currency, string Reason, DateTimeOffset OccurredAt)
    : IDomainEvent;

/// <summary>Entity MedicalClaimLine (health.medical_claim_line), with its adjudication (ADR-012).</summary>
public sealed class MedicalClaimLine : Entity<Guid>, ITenantScoped
{
    private MedicalClaimLine()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid ClaimId { get; private set; }

    public int LineNo { get; private set; }

    public string TariffCode { get; private set; } = "";

    public string? DiagnosisCode { get; private set; }

    public int Quantity { get; private set; }

    public decimal ClaimedAmount { get; private set; }

    public string[]? Modifiers { get; private set; }

    public string? BenefitCategory { get; private set; }

    public string? NetworkStatus { get; private set; }

    public decimal? TariffAmount { get; private set; }

    public decimal AllowedAmount { get; private set; }

    public decimal DeductibleAmount { get; private set; }

    public decimal CopayAmount { get; private set; }

    public decimal PaidAmount { get; private set; }

    public decimal MemberLiability { get; private set; }

    public string? Outcome { get; private set; }

    public string? AdjustmentReason { get; private set; }

    public Guid? PreauthLineId { get; private set; }

    public long RowVersion { get; private set; }

    public ClaimLineInput ToInput() => new(LineNo, TariffCode, DiagnosisCode, Quantity, ClaimedAmount, Modifiers ?? []);

    internal static MedicalClaimLine Create(Guid claimId, int lineNo, ClaimLineRequest request) => new()
    {
        Id = Guid.CreateVersion7(), ClaimId = claimId, LineNo = lineNo, TariffCode = Guard.MaxLength(request.TariffCode, 15), DiagnosisCode = request.DiagnosisCode,
        Quantity = request.Quantity, ClaimedAmount = request.ClaimedAmount, Modifiers = request.Modifiers is { Count: > 0 } m ? [.. m] : null,
        MemberLiability = request.ClaimedAmount,
    };

    internal void Apply(LineResult result)
    {
        Outcome = result.Outcome.ToString().ToUpperInvariant();
        AdjustmentReason = result.Reason;
        BenefitCategory = result.Category;
        NetworkStatus = result.NetworkStatus is { } network ? (network == Adjudication.NetworkStatus.InNetwork ? "IN_NETWORK" : "OUT_OF_NETWORK") : null;
        TariffAmount = result.Tariff;
        AllowedAmount = result.Allowed;
        DeductibleAmount = result.Deductible;
        CopayAmount = result.CoPayment;
        PaidAmount = result.Paid;
        MemberLiability = result.MemberLiability;
        PreauthLineId = result.PreauthLineId;
    }
}

/// <summary>
/// The MedicalClaim aggregate (health.medical_claim, medical_claim_line): a provider's or member's claim (HLT-006). Each
/// line is validated and adjudicated against membership, cover, limits, tariff, preauthorisation and exclusions
/// (HLT-007); the paid amount is at most allowed less the co-payment; payments are traceable to the run that paid them
/// (HLT-009). A pended claim changes no accumulator until it is adjudicated again (ADR-012).
/// </summary>
public sealed class MedicalClaim : AggregateRoot<Guid>
{
    public static readonly StateMachine<MedicalClaimStatus, MedicalClaimTrigger> Lifecycle = new StateMachine<MedicalClaimStatus, MedicalClaimTrigger>()
        .Allow(MedicalClaimStatus.Received, MedicalClaimTrigger.ValidateClaim, MedicalClaimStatus.Validated)
        .Allow(MedicalClaimStatus.Validated, MedicalClaimTrigger.AdjudicateClaim, MedicalClaimStatus.Adjudicated)
        .Allow(MedicalClaimStatus.Adjudicated, MedicalClaimTrigger.PendClaim, MedicalClaimStatus.Pended)
        .Allow(MedicalClaimStatus.Pended, MedicalClaimTrigger.InformationReceived, MedicalClaimStatus.Adjudicated)
        .Allow(MedicalClaimStatus.Adjudicated, MedicalClaimTrigger.ApproveForPayment, MedicalClaimStatus.ApprovedForPayment)
        .Allow(MedicalClaimStatus.ApprovedForPayment, MedicalClaimTrigger.PaymentRunReleased, MedicalClaimStatus.Paid)
        .Allow(MedicalClaimStatus.Validated, MedicalClaimTrigger.RejectClaim, MedicalClaimStatus.Rejected)
        // F006 (Wave 4.1): a claim held for unpaid contributions is declined when the membership is suspended for arrears.
        .Allow(MedicalClaimStatus.Pended, MedicalClaimTrigger.RejectClaim, MedicalClaimStatus.Rejected)
        .Allow(MedicalClaimStatus.Paid, MedicalClaimTrigger.ReverseClaim, MedicalClaimStatus.Reversed)
        .Allow(MedicalClaimStatus.Rejected, MedicalClaimTrigger.ReopenClaim, MedicalClaimStatus.Validated);

    private readonly List<MedicalClaimLine> _lines = [];

    private MedicalClaim()
    {
    }

    public string ClaimNumber { get; private set; } = "";

    public string Source { get; private set; } = ClaimSources.Provider;

    public string? ProviderId { get; private set; }

    public Guid MembershipId { get; private set; }

    public Guid PatientPartyId { get; private set; }

    public DateOnly ServiceDate { get; private set; }

    public DateTimeOffset SubmittedAt { get; private set; }

    public string FormatCode { get; private set; } = "";

    public Guid? PreauthId { get; private set; }

    public decimal TotalClaimed { get; private set; }

    public decimal? TotalPaid { get; private set; }

    public MedicalClaimStatus Status { get; private set; }

    public string Currency { get; private set; } = "";

    public string? PendReason { get; private set; }

    public DateTimeOffset? AdjudicatedAt { get; private set; }

    public Guid? ApprovedBy { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public Guid? PaymentRunId { get; private set; }

    public DateTimeOffset? ReversedAt { get; private set; }

    /// <summary>The approval's outcome (OQ-102): APPROVED, or PARTLY_APPROVED when some lines were declined; null until approved.</summary>
    public string? DecisionOutcome { get; private set; }

    /// <summary>A partial approval's reason (claims.ref_claim_decision_reason_codes, decision type PARTIAL).</summary>
    public string? DecisionReasonCode { get; private set; }

    public bool PartlyApproved => DecisionOutcome == MedicalClaimOutcomes.PartlyApproved;

    public IReadOnlyList<MedicalClaimLine> Lines => _lines;

    /// <summary>Command SubmitClaim.</summary>
    public static Result<MedicalClaim> SubmitClaim(Guid id, string claimNumber, string source, string? providerId, Guid membershipId, Guid patientPartyId,
        DateOnly serviceDate, string formatCode, string currency, Guid? preauthId, IReadOnlyList<ClaimLineRequest> lines, DateTimeOffset now)
    {
        if (lines.Count == 0 || lines.Any(l => l.Quantity <= 0 || l.ClaimedAmount <= 0) || (source == ClaimSources.Provider && providerId is null))
        {
            return Error.Validation(HealthErrorCodes.ClaimInvalid, "A claim has lines with a quantity and amount above zero; a provider claim names the provider.");
        }

        var claim = new MedicalClaim
        {
            Id = id, ClaimNumber = Guard.MaxLength(claimNumber, 25), Source = source, ProviderId = providerId, MembershipId = membershipId, PatientPartyId = patientPartyId,
            ServiceDate = serviceDate, SubmittedAt = now, FormatCode = Guard.MaxLength(formatCode, 15), Currency = currency, PreauthId = preauthId,
            TotalClaimed = lines.Sum(l => l.ClaimedAmount), Status = MedicalClaimStatus.Received,
        };
        claim._lines.AddRange(lines.Select((l, i) => MedicalClaimLine.Create(id, i + 1, l)));
        claim.Raise(new MedicalClaimSubmitted(id, claim.ClaimNumber, source, providerId, membershipId, serviceDate, claim.TotalClaimed, currency, now));
        return claim;
    }

    /// <summary>Command ValidateClaim: membership, cover and codes (HLT-007). A claim that fails validation is rejected with the reason.</summary>
    public Result ValidateClaim(string? problem, DateTimeOffset now)
    {
        var result = Fire(MedicalClaimTrigger.ValidateClaim);
        if (result.IsFailure || problem is null)
        {
            return result;
        }

        PendReason = Guard.MaxLength(problem, 30);
        TotalPaid = 0;
        var rejected = Fire(MedicalClaimTrigger.RejectClaim);
        if (rejected.IsSuccess)
        {
            Raise(new MedicalClaimRejected(Id, ClaimNumber, MembershipId, PendReason, now));
        }

        return rejected;
    }

    /// <summary>Command AdjudicateClaim (or "Information received" for a pended claim): the lines take the result; a line that pends pends the claim.</summary>
    public Result AdjudicateClaim(AdjudicationResult result, DateTimeOffset now)
    {
        var trigger = Status == MedicalClaimStatus.Pended ? MedicalClaimTrigger.InformationReceived : MedicalClaimTrigger.AdjudicateClaim;
        var fired = Fire(trigger);
        if (fired.IsFailure)
        {
            return fired;
        }

        foreach (var line in result.Lines)
        {
            _lines.Single(l => l.LineNo == line.LineNo).Apply(line);
        }

        AdjudicatedAt = now;
        PendReason = result.Lines.FirstOrDefault(l => l.Outcome == LineOutcome.Pend)?.Reason;
        TotalPaid = result.Pended ? null : result.Paid;
        Raise(new MedicalClaimAdjudicated(Id, ClaimNumber, MembershipId, result.Pended, result.Paid, Currency, result.Pended ? [] : result.AuthorisationUses,
            result.Pended ? [] : result.Lines.Where(l => l.HighCostRule is not null).Select(l => new HighCostLine(l.LineNo, l.HighCostRule!)).ToList(), now));
        return result.Pended ? Fire(MedicalClaimTrigger.PendClaim) : Result.Success();
    }

    /// <summary>
    /// Command ApproveForPayment: the adjudicated amount is approved by a person. A claim some of whose lines adjudication
    /// declined, while it pays the rest, is a partial approval (OQ-102), which gives a decision reason of type PARTIAL. Paying
    /// a line at the tariff, or less a co-payment or deductible, is the plan's terms, not a decline.
    /// </summary>
    /// <param name="reasonType">The decision type of <paramref name="reasonCode"/> as a claim decision reason in force, or null when it is none.</param>
    public Result ApproveForPayment(Guid by, DateTimeOffset now, string? reasonCode = null, string? reasonType = null)
    {
        var partly = Status == MedicalClaimStatus.Adjudicated && TotalPaid > 0 && _lines.Any(l => l.Outcome == "REJECT");
        if (partly && (string.IsNullOrWhiteSpace(reasonCode) || reasonType != MedicalClaimOutcomes.PartialReasonType))
        {
            return Error.Validation(HealthErrorCodes.RationaleRequired,
                $"Claim {ClaimNumber} declines some of its lines and pays {TotalPaid:0.00} {Currency}: a partial approval gives a decision reason of type PARTIAL (OQ-102).");
        }

        var result = Fire(MedicalClaimTrigger.ApproveForPayment);
        if (result.IsSuccess)
        {
            ApprovedBy = by;
            ApprovedAt = now;
            DecisionOutcome = partly ? MedicalClaimOutcomes.PartlyApproved : MedicalClaimOutcomes.Approved;
            DecisionReasonCode = partly ? Guard.MaxLength(reasonCode!, 20) : null;
        }

        return result;
    }

    /// <summary>
    /// The member's rights on a decision that declines all or part of the claim (OQ-102; Ade, 29 October 2026): asks
    /// Communications for CLAIM_DECISION_RIGHTS to the notice's recipient (the patient, or the principal for a dependant
    /// under 18), with the reason's customer wording. A partial approval or a rejection sends it.
    /// </summary>
    public Result NoticeDecisionRights(Guid recipientPartyId, string? productCode, string? orgNodeCode, DateOnly decidedOn, DateTimeOffset now,
        string? decisionReasons = null)
    {
        if (!PartlyApproved && Status != MedicalClaimStatus.Rejected)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Only a claim declined in full or in part sends the rights on its decision.");
        }

        Raise(new MedicalClaimDecisionRightsDue(Id, ClaimNumber, Guard.NotEmpty(recipientPartyId), productCode, orgNodeCode, TotalPaid ?? 0, Currency, decidedOn, now,
            decisionReasons));
        return Result.Success();
    }

    /// <summary>
    /// A dispute or appeal of the claim was upheld or partly upheld (F013, OQ-103): a rejected claim goes back to Validated
    /// to be adjudicated again. Returns false, changing nothing, for a claim that is not rejected (a redelivery, or a claim
    /// still being decided). TODO(spec): reassessing an adjudicated, approved or paid claim after an upheld dispute is not specified.
    /// </summary>
    public bool ReopenForCase(string caseNumber)
    {
        if (string.IsNullOrWhiteSpace(caseNumber) || Status != MedicalClaimStatus.Rejected || Fire(MedicalClaimTrigger.ReopenClaim).IsFailure)
        {
            return false;
        }

        TotalPaid = null;
        PendReason = null;
        return true;
    }

    /// <summary>Command RejectClaim, after validation.</summary>
    public Result RejectClaim(string reason, DateTimeOffset now)
    {
        var result = Fire(MedicalClaimTrigger.RejectClaim);
        if (result.IsSuccess)
        {
            PendReason = Guard.MaxLength(reason, 30);
            TotalPaid = 0;
            Raise(new MedicalClaimRejected(Id, ClaimNumber, MembershipId, PendReason, now));
        }

        return result;
    }

    /// <summary>Taken into a payment run (cut off and calculated with it).</summary>
    public Result AssignToRun(Guid runId)
    {
        if (Status != MedicalClaimStatus.ApprovedForPayment || PaymentRunId is not null)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Claim {ClaimNumber} is not waiting for a payment run.");
        }

        PaymentRunId = runId;
        MarkChanged();
        return Result.Success();
    }

    /// <summary>
    /// Leaves a payment run whose approval ended without being granted, so a new run takes the claim (D-2). Only a claim
    /// still in that run and not yet paid leaves it; returns whether it did.
    /// </summary>
    public bool LeaveRun(Guid runId)
    {
        if (PaymentRunId != runId || Status != MedicalClaimStatus.ApprovedForPayment)
        {
            return false;
        }

        PaymentRunId = null;
        MarkChanged();
        return true;
    }

    /// <summary>Transition "Payment run released".</summary>
    public Result MarkPaid(Guid runId, DateOnly paidOn, DateTimeOffset now)
    {
        if (PaymentRunId != runId)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Claim {ClaimNumber} is not in that run.");
        }

        var result = Fire(MedicalClaimTrigger.PaymentRunReleased);
        if (result.IsSuccess)
        {
            Raise(new MedicalClaimSettled(Id, ClaimNumber, ProviderId, runId, TotalPaid ?? 0, Currency, paidOn, now));
        }

        return result;
    }

    /// <summary>Command ReverseClaim: the consumption is reversed (by the application, with this claim) and a recovery is raised.</summary>
    public Result ReverseClaim(string reason, DateTimeOffset now)
    {
        var result = Fire(MedicalClaimTrigger.ReverseClaim);
        if (result.IsSuccess)
        {
            ReversedAt = now;
            Raise(new MedicalClaimReversed(Id, ClaimNumber, ProviderId, TotalPaid ?? 0, Currency, reason, now));
        }

        return result;
    }

    private Result Fire(MedicalClaimTrigger trigger)
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
