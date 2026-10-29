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

    /// <summary>F006: grace ended unpaid; the policy was cancelled from before the loss.</summary>
    DeclineForNonPayment,
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

/// <param name="HoldReason">PREMIUM_ARREARS when the loss falls in grace without cover and the arrears are unpaid (F006): the payment waits for them.</param>
/// <param name="PremiumOffset">The arrears taken from the settlement, by the tenant's grace rule (F006); <paramref name="Amount"/> is what is paid.</param>
public sealed record GeneralClaimApproved(Guid ClaimId, string ClaimNumber, Guid PayeePartyId, Guid BankAccountId, string PayeeBankHash, decimal Amount, string Currency,
    string ReserveType, Guid ApprovalId, Guid CheckerId, DateTimeOffset OccurredAt, Guid? PolicyId = null, string? HoldReason = null, decimal PremiumOffset = 0m) : IDomainEvent;

/// <summary>The policy's arrears were paid from a claim's settlement (F006, the tenant's rule): Billing settles them.</summary>
public sealed record GeneralPremiumOffsetFromClaim(Guid PolicyId, Guid ClaimId, string ClaimNumber, decimal Amount, string Currency, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Who is told of a full decline and its rights (OQ-102): the policyholder, the reason's customer wording, in the policy's currency.</summary>
public sealed record ClaimRightsNotice(Guid RecipientPartyId, string? DecisionReasons, string Currency, DateOnly DecidedOn);

/// <summary>
/// How an approved settlement is paid when the loss falls in grace without cover (F006): held for the arrears, or paid less
/// <paramref name="PremiumOffset"/> (the arrears, which the settlement covers).
/// </summary>
public sealed record SettlementCondition(Guid PolicyId, bool HoldForArrears, decimal PremiumOffset)
{
    /// <summary>Why an instruction waits for a policy's arrears (the Claims Framework's hold reason).</summary>
    public const string PremiumArrears = "PREMIUM_ARREARS";
}

/// <param name="Amount">The gross the claim paid (OQ-137): what a payee received plus <paramref name="PremiumOffset"/>.</param>
public sealed record GeneralClaimSettled(Guid ClaimId, string ClaimNumber, Guid PolicyId, decimal Amount, string Currency, DateOnly PaidOn, DateTimeOffset OccurredAt,
    decimal PremiumOffset = 0m)
    : IDomainEvent;

public sealed record GeneralClaimClosed(Guid ClaimId, string ClaimNumber, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record GeneralClaimDeclined(Guid ClaimId, string ClaimNumber, string DecisionCode, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// A claim was partly approved (OQ-102): the claimant is sent their rights on the declined part (CLAIM_DECISION_RIGHTS,
/// PPR 17.6.3) with the decision's reasons. Identifiers, amounts and the reason's customer wording only.
/// </summary>
public sealed record GeneralClaimDecisionRightsDue(Guid ClaimId, string ClaimNumber, Guid RecipientPartyId, string? ProductCode, string? OrgNodeCode, string? DecisionReasons,
    decimal Amount, decimal ClaimedAmount, string Currency, DateOnly DecidedOn, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The claim's decision as an outcome (OQ-102): a settlement below the claimed amount is a partial approval.</summary>
public static class ClaimDecisionOutcomes
{
    public const string Approved = "APPROVED";
    public const string PartlyApproved = "PARTLY_APPROVED";
    public const string Declined = "DECLINED";

    /// <summary>The decision type a partial approval's reason has (claims.ref_claim_decision_reason_codes).</summary>
    public const string PartialReasonType = "PARTIAL";
}

/// <summary>
/// What the settlement is: the accepted assessment, the coverage, the payee and their verified bank account (with the
/// keyed hash of its number), the reserve, and the maker's decision and rationale (CLM-003).
/// </summary>
/// <param name="DecisionType">The decision type of <paramref name="DecisionCode"/> as a claim decision reason in force, or null when it is none (OQ-102).</param>
public sealed record SettlementRequest(decimal AssessedAmount, decimal ExcessApplied, Guid PayeePartyId, Guid BankAccountId, string PayeeBankHash, string ReserveType,
    string DecisionCode, string Rationale, string? DecisionType = null);

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
        // F006 (Wave 4): grace ended unpaid, so a claim for a loss from the cancellation date is declined at any stage short of payment.
        .Allow(ClaimStatus.FirstNotification, ClaimTrigger.DeclineForNonPayment, ClaimStatus.Declined)
        .Allow(ClaimStatus.Registered, ClaimTrigger.DeclineForNonPayment, ClaimStatus.Declined)
        .Allow(ClaimStatus.UnderAssessment, ClaimTrigger.DeclineForNonPayment, ClaimStatus.Declined)
        .Allow(ClaimStatus.Approved, ClaimTrigger.DeclineForNonPayment, ClaimStatus.Declined)
        .Allow(ClaimStatus.Approved, ClaimTrigger.PaymentCompleted, ClaimStatus.Settled)
        .Allow(ClaimStatus.Settled, ClaimTrigger.CloseClaim, ClaimStatus.Closed)
        .Allow(ClaimStatus.Declined, ClaimTrigger.CloseClaim, ClaimStatus.Closed)
        .Allow(ClaimStatus.Closed, ClaimTrigger.ReopenClaim, ClaimStatus.Reopened)
        // A declined claim whose dispute or appeal is upheld is reassessed without being closed first (F011, F013, OQ-103).
        .Allow(ClaimStatus.Declined, ClaimTrigger.ReopenClaim, ClaimStatus.Reopened)
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

    /// <summary>
    /// What the claim was assessed at under the policy before its limits: the accepted assessment less the excess (OQ-102).
    /// A settlement below it is a partial approval. TODO(spec): General records no amount claimed by the claimant.
    /// </summary>
    public decimal? ClaimedAmount { get; private set; }

    /// <summary>The policy's arrears deducted from the settlement (F006, OQ-137): the payee receives the settlement less this; the claim paid the gross.</summary>
    public decimal PremiumOffset { get; private set; }

    /// <summary>The decision's outcome (OQ-102): APPROVED, PARTLY_APPROVED or DECLINED; null until decided.</summary>
    public string? DecisionOutcome { get; private set; }

    public bool PartlyApproved => DecisionOutcome == ClaimDecisionOutcomes.PartlyApproved;

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

        // OQ-102: below what the claim was assessed at (less the excess), the settlement is a partial approval and gives its reason.
        var claimed = Math.Max(0, request.AssessedAmount - request.ExcessApplied);
        if (amount < claimed && request.DecisionType != ClaimDecisionOutcomes.PartialReasonType)
        {
            return Error.Validation(GeneralErrorCodes.RationaleRequired,
                $"The settlement of {amount:0.00} {currency} is below the {claimed:0.00} {currency} claimed: a partial approval gives a decision reason of type PARTIAL (OQ-102).");
        }

        ClaimedAmount = claimed;

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
    public Result DecideSettlement(bool approved, Guid approvalId, Guid checkerId, string? subjectHash, DateTimeOffset now, SettlementCondition? condition = null)
    {
        // F006: a claim declined because grace ended unpaid while its settlement waited for approval: the decision is moot.
        if (!SettlementPending && Status == ClaimStatus.Declined && DeclinedForNonPayment)
        {
            return Result.Success();
        }

        if (!SettlementPending)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "No settlement is waiting for approval.");
        }

        if (subjectHash != SettlementSubjectHash)
        {
            return Error.Conflict(GeneralErrorCodes.ApprovalSubjectChanged, "The approval was for another settlement.");
        }

        if (checkerId != Guid.Empty && checkerId == SettlementRequestedBy)
        {
            return MakerChecker.EnsureDifferentPeople(SettlementRequestedBy.ToString()!, checkerId.ToString());
        }

        if (approved && checkerId == Guid.Empty)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "A settlement is approved by a checker.");
        }

        // Every ending other than approval (rejected, expired, withdrawn): the claim stays under assessment, the settlement cleared.
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
            ClaimedAmount = null;
            MarkChanged();
            return Result.Success();
        }

        var result = Fire(ClaimTrigger.ApproveSettlement);
        if (result.IsSuccess)
        {
            SettlementApprovalId = approvalId;
            DecidedBy = checkerId;
            DecidedAt = now;
            DecisionOutcome = SettlementAmount < ClaimedAmount ? ClaimDecisionOutcomes.PartlyApproved : ClaimDecisionOutcomes.Approved;
            var offset = condition is { HoldForArrears: false, PremiumOffset: > 0 } ? Math.Min(condition.PremiumOffset, SettlementAmount!.Value) : 0m;
            PremiumOffset = offset;
            Raise(new GeneralClaimApproved(Id, ClaimNumber!, SettlementPayeePartyId!.Value, SettlementBankAccountId!.Value, SettlementPayeeBankHash!, SettlementAmount!.Value - offset,
                SettlementCurrency!, SettlementReserveType!, approvalId, checkerId, now, PolicyId, condition is { HoldForArrears: true } ? SettlementCondition.PremiumArrears : null,
                offset));
            if (offset > 0)
            {
                Raise(new GeneralPremiumOffsetFromClaim(PolicyId, Id, ClaimNumber!, offset, SettlementCurrency!, now));
            }
        }

        return result;
    }

    /// <summary>Whether this approval's grant is already applied (the claim was approved by it, and may be settled since): a redelivery changes nothing.</summary>
    public bool SettlementApprovalApplied(Guid approvalId) => SettlementApprovalId == approvalId;

    /// <summary>
    /// Whether a settlement approval's ending (not granted) is already applied, so a redelivery changes nothing: no
    /// settlement is waiting (it was cleared), and the claim was not approved by that same approval.
    /// </summary>
    public bool SettlementEndingApplied(Guid approvalId) => !SettlementPending && SettlementApprovalId != approvalId;

    /// <summary>Whether the claim was declined because its policy's grace ended unpaid (F006).</summary>
    public bool DeclinedForNonPayment => Status == ClaimStatus.Declined && DecisionRationale?.StartsWith(NonPaymentRationale, StringComparison.Ordinal) == true;

    private const string NonPaymentRationale = "Grace ended unpaid";

    /// <summary>
    /// Command DeclineForNonPayment (F006): the policy's grace ended unpaid and it was cancelled from <paramref name="cancelledFrom"/>,
    /// so a claim for a loss from then is declined with the grace rule's claim decision, whatever stage it reached short of
    /// payment. A settlement waiting for approval is dropped; a payment held for the arrears is withdrawn.
    /// </summary>
    public Result DeclineForNonPayment(string decisionCode, DateOnly cancelledFrom, DateTimeOffset now, ClaimRightsNotice? rights = null)
    {
        if (LossDate < cancelledFrom || Status is not (ClaimStatus.FirstNotification or ClaimStatus.Registered or ClaimStatus.UnderAssessment or ClaimStatus.Approved))
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Claim {ClaimNumber} is {Status} for a loss on {LossDate:yyyy-MM-dd}.");
        }

        var claimed = ClaimedAmount ?? SettlementAmount ?? 0m;
        var result = Fire(ClaimTrigger.DeclineForNonPayment);
        if (result.IsSuccess)
        {
            SettlementAmount = null;
            SettlementCurrency = null;
            SettlementPayeePartyId = null;
            SettlementBankAccountId = null;
            SettlementPayeeBankHash = null;
            SettlementReserveType = null;
            SettlementRequestedBy = null;
            DecisionCode = Guard.MaxLength(decisionCode, 15);
            DecisionRationale = $"{NonPaymentRationale}: the policy was cancelled for non-payment from {cancelledFrom:yyyy-MM-dd}.";
            DecidedAt = now;
            DecisionOutcome = ClaimDecisionOutcomes.Declined;
            Raise(new GeneralClaimDeclined(Id, ClaimNumber ?? "", DecisionCode, now));

            // OQ-102: a claim declined in full sends the claimant their rights on the decision, with the reason's wording.
            if (rights is not null)
            {
                Raise(new GeneralClaimDecisionRightsDue(Id, ClaimNumber ?? "", Guard.NotEmpty(rights.RecipientPartyId), ProductCode, OrgNodeCode, rights.DecisionReasons, 0m,
                    claimed, rights.Currency, rights.DecidedOn, now));
            }
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
            DecisionOutcome = ClaimDecisionOutcomes.Declined;
            Raise(new GeneralClaimDeclined(Id, ClaimNumber!, DecisionCode, now));
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
            Raise(new GeneralClaimSettled(Id, ClaimNumber!, PolicyId, SettlementAmount!.Value, SettlementCurrency!, paidOn, now, PremiumOffset));
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

    /// <summary>
    /// A dispute or appeal of the claim was upheld or partly upheld (F011, F012, F013, OQ-103): a declined or closed claim
    /// is reopened for reassessment, with the case as its reason. Returns false, changing nothing, for a claim that is
    /// open already (a redelivery, or a claim still being assessed) or that cannot be reopened (approved or settled and not closed).
    /// </summary>
    public bool ReopenForCase(string caseNumber)
    {
        if (string.IsNullOrWhiteSpace(caseNumber) || Status is not (ClaimStatus.Declined or ClaimStatus.Closed))
        {
            return false;
        }

        return ReopenClaim().IsSuccess;
    }

    /// <summary>
    /// The claimant's rights on a partial approval (OQ-102, PPR 17.6.3): asks Communications for CLAIM_DECISION_RIGHTS to
    /// <paramref name="recipientPartyId"/> with the reason's customer wording. Only a partly approved claim sends it.
    /// </summary>
    public Result NoticeDecisionRights(Guid recipientPartyId, string? decisionReasons, DateOnly decidedOn, DateTimeOffset now)
    {
        if (!PartlyApproved || SettlementAmount is not { } amount || ClaimedAmount is not { } claimed)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Only a partly approved claim sends the rights on its declined part.");
        }

        Raise(new GeneralClaimDecisionRightsDue(Id, ClaimNumber!, Guard.NotEmpty(recipientPartyId), ProductCode, OrgNodeCode, decisionReasons, amount, claimed,
            SettlementCurrency!, decidedOn, now));
        return Result.Success();
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
