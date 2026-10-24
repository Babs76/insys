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
using Insys.General.Domain.Policies;
using Insys.General.Domain.Underwriting;

namespace Insys.General.Domain.Claims;

/// <summary>States from domain-model.json (general › GeneralClaim).</summary>
public enum ClaimStatus
{
    FirstNotification,
    Registered,
    UnderAssessment,
    Approved,
    Declined,
    Settled,
    Closed,
    Reopened,
}

public enum ClaimTrigger
{
    RegisterClaim,
    AppointAssessor,
    ApproveSettlement,
    DeclineClaim,
    PaymentCompleted,
    CloseClaim,
    ReopenClaim,
    AssessmentResumes,
}

public sealed record GeneralClaimRegistered(Guid ClaimId, string ClaimNumber, Guid PolicyId, DateOnly LossDate, string PerilCode, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>An assessor was appointed: the LossAssessment is assigned (a separate aggregate, made consistent by this event).</summary>
public sealed record AssessorAppointed(Guid ClaimId, Guid AssessorPartyId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Asks Workflow for a second person's approval of the settlement amount, within authority (SRS-002).</summary>
public sealed record SettlementRequested(Guid ClaimId, string ClaimNumber, Guid RequestedBy, decimal Amount, string Currency, string SubjectHash, DateTimeOffset OccurredAt)
    : IDomainEvent;

public sealed record GeneralClaimApproved(Guid ClaimId, string ClaimNumber, Guid PayeePartyId, Guid BankAccountId, string PayeeBankHash, decimal Amount, string Currency,
    string ReserveType, Guid ApprovalId, Guid CheckerId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record GeneralClaimSettled(Guid ClaimId, string ClaimNumber, Guid PolicyId, decimal Amount, string Currency, DateOnly PaidOn, DateTimeOffset OccurredAt)
    : IDomainEvent;

public sealed record GeneralClaimClosed(Guid ClaimId, string ClaimNumber, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// What the settlement is: the accepted assessment, the coverage, the payee and their verified bank account (with the
/// keyed hash of its number), the reserve, and the maker's decision and rationale (CLM-003).
/// </summary>
public sealed record SettlementRequest(decimal AssessedAmount, decimal ExcessApplied, Guid PayeePartyId, Guid BankAccountId, string PayeeBankHash, string ReserveType,
    string DecisionCode, string Rationale);

/// <summary>
/// The excess or deductible of a coverage's schedule (product.cfg_limit_and_deductible_schedules, GEN-008): a fixed
/// amount, a percentage of the claim, or the greater or lesser of the two.
/// </summary>
public static class ExcessCalculator
{
    public static decimal Excess(IEnumerable<CoverageLimit> schedule, decimal claimAmount)
    {
        decimal total = 0;
        foreach (var line in schedule.Where(l => l.LimitType is "EXCESS" or "DEDUCTIBLE"))
        {
            var fixedAmount = line.Amount ?? 0;
            var percent = Math.Round(claimAmount * (line.Percentage ?? 0) / 100m, 2, MidpointRounding.AwayFromZero);
            total += line.Rule switch
            {
                "PERCENT_OF_CLAIM" => percent,
                "GREATER_OF" => Math.Max(fixedAmount, percent),
                "LESSER_OF" => Math.Min(fixedAmount, percent),
                _ => fixedAmount,
            };
        }

        return Math.Min(total, claimAmount);
    }

    /// <summary>
    /// The settlement (GEN-008): the assessed amount less the excess, within the coverage's sum insured and its limits
    /// and sublimits. TODO(spec): valuation rules beyond the sum insured are not specified (OQ-059).
    /// </summary>
    public static decimal Settlement(decimal assessedAmount, decimal excessApplied, decimal sumInsured, IEnumerable<CoverageLimit> limits)
    {
        var cap = limits.Where(l => l.LimitType is "LIMIT" or "SUBLIMIT" && l.Amount is not null).Select(l => l.Amount!.Value).Append(sumInsured).Min();
        return Math.Max(0, Math.Min(assessedAmount - excessApplied, cap));
    }
}

/// <summary>Entity ClaimParty (general.claim_party): a claimant, third party, witness or other party to the claim.</summary>
public sealed class ClaimParty : Entity<Guid>, ITenantScoped
{
    private ClaimParty()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid ClaimId { get; private set; }

    public Guid PartyId { get; private set; }

    public string Role { get; private set; } = "";

    /// <summary>A third party's insurer reference.</summary>
    public string? InsurerRef { get; private set; }

    public long RowVersion { get; private set; }

    internal static ClaimParty Create(Guid claimId, Guid partyId, string role, string? insurerRef) =>
        new() { Id = Guid.CreateVersion7(), ClaimId = claimId, PartyId = Guard.NotEmpty(partyId), Role = Guard.MaxLength(role, 15), InsurerRef = insurerRef };
}

/// <summary>
/// The GeneralClaim aggregate (general.claim, claim_party). The loss date is within the policy period with valid cover
/// (GEN-006); settlement applies excess, limits and valuation rules (GEN-008); approval is within authority by a second
/// person (SRS-002); a decision records its rationale (CLM-003).
/// </summary>
public sealed class GeneralClaim : AggregateRoot<Guid>
{
    public static readonly StateMachine<ClaimStatus, ClaimTrigger> Lifecycle = new StateMachine<ClaimStatus, ClaimTrigger>()
        .Allow(ClaimStatus.FirstNotification, ClaimTrigger.RegisterClaim, ClaimStatus.Registered)
        .Allow(ClaimStatus.Registered, ClaimTrigger.AppointAssessor, ClaimStatus.UnderAssessment)
        .Allow(ClaimStatus.UnderAssessment, ClaimTrigger.ApproveSettlement, ClaimStatus.Approved)
        .Allow(ClaimStatus.UnderAssessment, ClaimTrigger.DeclineClaim, ClaimStatus.Declined)
        .Allow(ClaimStatus.Approved, ClaimTrigger.PaymentCompleted, ClaimStatus.Settled)
        .Allow(ClaimStatus.Settled, ClaimTrigger.CloseClaim, ClaimStatus.Closed)
        .Allow(ClaimStatus.Declined, ClaimTrigger.CloseClaim, ClaimStatus.Closed)
        .Allow(ClaimStatus.Closed, ClaimTrigger.ReopenClaim, ClaimStatus.Reopened)
        .Allow(ClaimStatus.Reopened, ClaimTrigger.AssessmentResumes, ClaimStatus.UnderAssessment);

    private readonly List<ClaimParty> _parties = [];

    private GeneralClaim()
    {
    }

    /// <summary>Allocated by the Claims Framework at registration (CLM-005); none at first notification.</summary>
    public string? ClaimNumber { get; private set; }

    public Guid PolicyId { get; private set; }

    /// <summary>The policy's product, kept on the claim for scoping its lists (F103).</summary>
    public string? ProductCode { get; private set; }

    /// <summary>The organisation node (normally a branch) that owns the record (F103, addenda/access-scopes.md §2); its children inherit it.</summary>
    public string? OrgNodeCode { get; private set; }

    /// <summary>The channel the record was created through (F005, addenda/channel-permissions.md §3); null when the tenant has no channels.</summary>
    public string? ChannelCode { get; private set; }

    /// <summary>Records the channel of the command that created the record, once (F005).</summary>
    public void RecordChannel(string? channelCode)
    {
        ChannelCode ??= channelCode is null ? null : Guard.MaxLength(channelCode, 12);
    }

    /// <summary>A claim is scoped as its policy: the policy's product and organisation node (§1, §2).</summary>
    public void PlaceAt(string productCode, string? orgNodeCode)
    {
        ProductCode ??= Guard.MaxLength(productCode, 15);
        OrgNodeCode ??= orgNodeCode is null ? null : Guard.MaxLength(orgNodeCode, 15);
    }

    public Guid? RiskObjectId { get; private set; }

    public string CoverageCode { get; private set; } = "";

    public DateOnly LossDate { get; private set; }

    public DateTimeOffset NotifiedAt { get; private set; }

    public string CauseCode { get; private set; } = "";

    public string PerilCode { get; private set; } = "";

    public string? CatEventCode { get; private set; }

    public ClaimStatus Status { get; private set; }

    public string? Description { get; private set; }

    public decimal? SettlementAmount { get; private set; }

    public string? SettlementCurrency { get; private set; }

    public Guid? SettlementPayeePartyId { get; private set; }

    /// <summary>The payee's bank account (party.bank_account) the settlement is paid to.</summary>
    public Guid? SettlementBankAccountId { get; private set; }

    /// <summary>The keyed hash of that account's number, as approved; the number itself is held only by the Party module.</summary>
    public string? SettlementPayeeBankHash { get; private set; }

    /// <summary>What the settlement approval covers: payee, bank account, amount, currency and reserve.</summary>
    public string? SettlementSubjectHash => SettlementAmount is null ? null
        : ApprovalSubject.Hash("GENERAL_SETTLEMENT", Id, SettlementPayeePartyId, SettlementBankAccountId, SettlementPayeeBankHash, SettlementAmount, SettlementCurrency,
            SettlementReserveType);

    public string? SettlementReserveType { get; private set; }

    public Guid? SettlementRequestedBy { get; private set; }

    public Guid? SettlementApprovalId { get; private set; }

    public string? DecisionCode { get; private set; }

    public string? DecisionRationale { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public IReadOnlyList<ClaimParty> Parties => _parties;

    /// <summary>A settlement is waiting for Workflow's decision.</summary>
    public bool SettlementPending => SettlementRequestedBy is not null && Status == ClaimStatus.UnderAssessment;

    /// <summary>Command NotifyLoss: the first notification, before cover is validated.</summary>
    public static Result<GeneralClaim> NotifyLoss(Guid id, Guid policyId, Guid? riskObjectId, string coverageCode, DateOnly lossDate, string causeCode, string perilCode,
        string? catEventCode, string? description, DateOnly today, DateTimeOffset now)
    {
        if (lossDate > today)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A loss date is not in the future.");
        }

        var claim = new GeneralClaim
        {
            Id = Guard.NotEmpty(id), PolicyId = Guard.NotEmpty(policyId), RiskObjectId = riskObjectId, CoverageCode = Guard.MaxLength(coverageCode, 20), LossDate = lossDate,
            NotifiedAt = now, CauseCode = Guard.MaxLength(causeCode, 15), PerilCode = Guard.MaxLength(perilCode, 10), CatEventCode = catEventCode, Description = description,
            Status = ClaimStatus.FirstNotification,
        };
        claim.MarkChanged();
        return claim;
    }

    /// <summary>
    /// Command RegisterClaim: the cover is validated (GEN-006) and the claim takes the number the Claims Framework
    /// allocated for it (CLM-005).
    /// </summary>
    public Result RegisterClaim(GeneralPolicy policy, string claimNumber, DateTimeOffset now)
    {
        if (policy.Id != PolicyId)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "The claim is on another policy.");
        }

        var covered = policy.CoversLoss(RiskObjectId, CoverageCode, LossDate);
        if (covered.IsFailure)
        {
            return covered;
        }

        var result = Fire(ClaimTrigger.RegisterClaim);
        if (result.IsSuccess)
        {
            ClaimNumber = Guard.MaxLength(claimNumber, 25);
            Raise(new GeneralClaimRegistered(Id, claimNumber, PolicyId, LossDate, PerilCode, now));
        }

        return result;
    }

    public void AddParty(Guid partyId, string role, string? insurerRef)
    {
        _parties.Add(ClaimParty.Create(Id, partyId, role, insurerRef));
        MarkChanged();
    }

    /// <summary>Command AppointAssessor: the claim goes under assessment (also when a reopened claim's assessment resumes).</summary>
    public Result AppointAssessor(Guid assessorPartyId, DateTimeOffset now)
    {
        var result = Fire(Status == ClaimStatus.Reopened ? ClaimTrigger.AssessmentResumes : ClaimTrigger.AppointAssessor);
        if (result.IsSuccess)
        {
            Raise(new AssessorAppointed(Id, assessorPartyId, now));
        }

        return result;
    }

    /// <summary>
    /// Command ApproveSettlement: the settlement applies the excess and the coverage's limits (GEN-008) and waits for a
    /// second person's approval in Workflow, within authority (SRS-002). The claim stays under assessment until then.
    /// </summary>
    /// <param name="aggregateRemaining">What remains of the coverage's aggregate claims limit in the period, when it has one (F062 §2).</param>
    public Result RequestSettlement(SettlementRequest request, GeneralCoverage coverage, IEnumerable<CoverageLimit> limits, string currency, Guid requestedBy,
        DateTimeOffset now, decimal? aggregateRemaining = null)
    {
        if (Status != ClaimStatus.UnderAssessment || SettlementPending)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, SettlementPending ? "A settlement is already waiting for approval." : $"The claim is {Status}.");
        }

        if (string.IsNullOrWhiteSpace(request.Rationale))
        {
            return Error.Validation(GeneralErrorCodes.RationaleRequired, "A decision records its rationale (CLM-003).");
        }

        var amount = ExcessCalculator.Settlement(request.AssessedAmount, request.ExcessApplied, coverage.SumInsured, limits);
        if (amount <= 0)
        {
            return Error.Validation(GeneralErrorCodes.NothingToSettle, "After the excess and limits there is nothing to settle; decline the claim instead.");
        }

        if (aggregateRemaining is { } remaining && amount > remaining)
        {
            return Error.Validation(GeneralErrorCodes.AggregateLimitExceeded,
                $"The settlement of {amount:0.00} {currency} is more than the {remaining:0.00} {currency} remaining of the coverage's aggregate limit.");
        }

        SettlementAmount = amount;
        SettlementCurrency = currency;
        SettlementPayeePartyId = Guard.NotEmpty(request.PayeePartyId);
        SettlementBankAccountId = request.BankAccountId;
        SettlementPayeeBankHash = request.PayeeBankHash;
        SettlementReserveType = Guard.MaxLength(request.ReserveType, 15);
        SettlementRequestedBy = requestedBy;
        DecisionCode = Guard.MaxLength(request.DecisionCode, 15);
        DecisionRationale = request.Rationale;
        Raise(new SettlementRequested(Id, ClaimNumber!, requestedBy, amount, currency, SettlementSubjectHash!, now));
        MarkChanged();
        return Result.Success();
    }

    /// <summary>
    /// Workflow's decision on the settlement. Approved, the claim is Approved; rejected, it stays under assessment. It
    /// applies only to the settlement it was asked for: the same payee, bank details, amount and reserve.
    /// </summary>
    public Result DecideSettlement(bool approved, Guid approvalId, Guid checkerId, string? subjectHash, DateTimeOffset now)
    {
        if (!SettlementPending)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "No settlement is waiting for approval.");
        }

        if (subjectHash != SettlementSubjectHash)
        {
            return Error.Conflict(GeneralErrorCodes.ApprovalSubjectChanged, "The approval was for another settlement.");
        }

        if (checkerId == SettlementRequestedBy)
        {
            return MakerChecker.EnsureDifferentPeople(SettlementRequestedBy.ToString()!, checkerId.ToString());
        }

        if (!approved)
        {
            SettlementAmount = null;
            SettlementCurrency = null;
            SettlementPayeePartyId = null;
            SettlementBankAccountId = null;
            SettlementPayeeBankHash = null;
            SettlementReserveType = null;
            SettlementRequestedBy = null;
            DecisionCode = null;
            DecisionRationale = null;
            MarkChanged();
            return Result.Success();
        }

        var result = Fire(ClaimTrigger.ApproveSettlement);
        if (result.IsSuccess)
        {
            SettlementApprovalId = approvalId;
            DecidedBy = checkerId;
            DecidedAt = now;
            Raise(new GeneralClaimApproved(Id, ClaimNumber!, SettlementPayeePartyId!.Value, SettlementBankAccountId!.Value, SettlementPayeeBankHash!, SettlementAmount!.Value,
                SettlementCurrency!, SettlementReserveType!, approvalId, checkerId, now));
        }

        return result;
    }

    /// <summary>Command DeclineClaim, with the decision and its rationale (CLM-003).</summary>
    public Result DeclineClaim(string decisionCode, string rationale, Guid decidedBy, DateTimeOffset now)
    {
        if (SettlementPending)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "A settlement is waiting for approval.");
        }

        if (string.IsNullOrWhiteSpace(rationale))
        {
            return Error.Validation(GeneralErrorCodes.RationaleRequired, "A decision records its rationale (CLM-003).");
        }

        var result = Fire(ClaimTrigger.DeclineClaim);
        if (result.IsSuccess)
        {
            DecisionCode = Guard.MaxLength(decisionCode, 15);
            DecisionRationale = rationale;
            DecidedBy = decidedBy;
            DecidedAt = now;
        }

        return result;
    }

    /// <summary>Command SettleClaim (transition "Payment completed"), from the Claims Framework's ClaimPaymentCompleted.</summary>
    public Result SettleClaim(DateOnly paidOn, DateTimeOffset now)
    {
        if (Status == ClaimStatus.Settled)
        {
            return Result.Success();
        }

        var result = Fire(ClaimTrigger.PaymentCompleted);
        if (result.IsSuccess)
        {
            Raise(new GeneralClaimSettled(Id, ClaimNumber!, PolicyId, SettlementAmount!.Value, SettlementCurrency!, paidOn, now));
        }

        return result;
    }

    public Result CloseClaim(DateTimeOffset now)
    {
        var result = Fire(ClaimTrigger.CloseClaim);
        if (result.IsSuccess)
        {
            Raise(new GeneralClaimClosed(Id, ClaimNumber!, now));
        }

        return result;
    }

    /// <summary>Command ReopenClaim. The earlier settlement stays on record; a new one is requested when assessment resumes.</summary>
    public Result ReopenClaim()
    {
        var result = Fire(ClaimTrigger.ReopenClaim);
        if (result.IsSuccess)
        {
            SettlementRequestedBy = null;
        }

        return result;
    }

    private Result Fire(ClaimTrigger trigger)
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
