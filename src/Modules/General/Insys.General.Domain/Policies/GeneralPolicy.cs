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

/// <summary>States from domain-model.json (general › GeneralPolicy).</summary>
public enum PolicyStatus
{
    Issued,
    InForce,
    Cancelled,
    Expired,
    Renewed,
}

public enum PolicyTrigger
{
    PeriodStarts,
    CancelPolicy,
    ReinstatePolicy,
    PeriodEndsWithoutRenewal,
    RenewPolicy,
}

/// <summary>How the policy is billed: the account, payer, frequency and collection method.</summary>
public sealed record BillingArrangement(Guid BillingAccountRef, Guid PayerPartyId, string Frequency, string CollectionMethod);

/// <summary>A cancellation reason and its refund rule (general.cfg_cancellation_reasons_and_refund_rules, GEN-004).</summary>
public sealed record CancellationRule(string ReasonCode, string Initiator, string RefundBasis, decimal Fee);

public sealed record GeneralPolicyIssued(Guid PolicyId, string PolicyNumber, string ProductCode, int ProductVersion, Guid PolicyholderPartyId, BillingArrangement Billing,
    string Currency, decimal AnnualPremium, DateOnly PeriodStart, DateOnly PeriodEnd, Guid? RenewalOfId, decimal SumInsured, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record GeneralPolicyEndorsed(Guid PolicyId, string PolicyNumber, Guid EndorsementId, string EndorsementNumber, DateOnly EffectiveDate,
    decimal NewAnnualPremium, decimal PremiumDelta, string Currency, string ProductCode, int ProductVersion, IReadOnlyList<string> DocumentTypes, decimal NewSumInsured,
    DateTimeOffset OccurredAt, Guid? PolicyholderPartyId = null) : IDomainEvent;

public sealed record GeneralPolicyRenewed(Guid PolicyId, string PolicyNumber, Guid RenewalPolicyId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record GeneralRenewalInvited(Guid PolicyId, string PolicyNumber, string ProductCode, int ProductVersion, DateOnly PeriodEnd, string TemplateCode,
    DateTimeOffset OccurredAt, Guid? PolicyholderPartyId = null) : IDomainEvent;

public sealed record GeneralPolicyCancelled(Guid PolicyId, string PolicyNumber, string ReasonCode, DateOnly EffectiveDate, decimal RefundAmount, string Currency,
    string ProductCode, int ProductVersion, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record GeneralPolicyReinstated(Guid PolicyId, string PolicyNumber, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Entity GeneralCoverage (general.coverage), modelled inside the policy so limits and premium totals stay consistent.</summary>
public sealed class GeneralCoverage : Entity<Guid>, ITenantScoped
{
    private GeneralCoverage()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PolicyRiskId { get; private set; }

    public string CoverageCode { get; private set; } = "";

    public decimal SumInsured { get; private set; }

    public string? ExcessScheduleCode { get; private set; }

    public decimal Premium { get; internal set; }

    public string[]? Extensions { get; private set; }

    public long RowVersion { get; private set; }

    internal static GeneralCoverage Create(Guid riskId, CoverageRequest request) => new()
    {
        Id = Guid.CreateVersion7(), PolicyRiskId = riskId, CoverageCode = Guard.MaxLength(request.CoverageCode, 20), SumInsured = request.SumInsured,
        ExcessScheduleCode = request.ExcessScheduleCode,
    };

    internal void Change(decimal sumInsured, string? excessScheduleCode)
    {
        SumInsured = sumInsured;
        ExcessScheduleCode = excessScheduleCode ?? ExcessScheduleCode;
    }
}

/// <summary>Entity PolicyRisk (general.policy_risk): a risk object the policy insures (GEN-002), from when, and until when.</summary>
public sealed class PolicyRisk : Entity<Guid>, ITenantScoped
{
    private readonly List<GeneralCoverage> _coverages = [];

    private PolicyRisk()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PolicyId { get; private set; }

    public Guid RiskObjectId { get; private set; }

    public decimal SumInsured { get; private set; }

    public DateOnly AttachedFrom { get; private set; }

    public DateOnly? DetachedOn { get; private set; }

    public long RowVersion { get; private set; }

    public IReadOnlyList<GeneralCoverage> Coverages => _coverages;

    /// <summary>Attached on the day: from its attachment date, and before it was detached.</summary>
    public bool AttachedOn(DateOnly day) => AttachedFrom <= day && (DetachedOn is null || day < DetachedOn);

    internal static PolicyRisk Create(Guid policyId, RiskRequest request, DateOnly attachedFrom)
    {
        var risk = new PolicyRisk { Id = Guid.CreateVersion7(), PolicyId = policyId, RiskObjectId = request.RiskObjectId, SumInsured = request.SumInsured, AttachedFrom = attachedFrom };
        risk._coverages.AddRange(request.Coverages.Select(c => GeneralCoverage.Create(risk.Id, c)));
        return risk;
    }

    internal void Detach(DateOnly on) => DetachedOn = on;

    internal void SetSumInsured(decimal sumInsured) => SumInsured = sumInsured;

    internal void AddCoverage(CoverageRequest request) => _coverages.Add(GeneralCoverage.Create(Id, request));

    internal void RemoveCoverage(GeneralCoverage coverage) => _coverages.Remove(coverage);
}

/// <summary>Entity Cancellation (general.cancellation): kept as history when the policy is reinstated.</summary>
public sealed class Cancellation : Entity<Guid>, ITenantScoped
{
    private Cancellation()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PolicyId { get; private set; }

    public string ReasonCode { get; private set; } = "";

    public DateOnly EffectiveDate { get; private set; }

    public decimal RefundAmount { get; private set; }

    public string RefundBasis { get; private set; } = "";

    public decimal Fee { get; private set; }

    public Guid? CancelledBy { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public Guid? ReinstatedBy { get; private set; }

    public DateTimeOffset? ReinstatedAt { get; private set; }

    public long RowVersion { get; private set; }

    internal static Cancellation Create(Guid policyId, CancellationRule rule, DateOnly effectiveDate, decimal refund, Guid by, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(), PolicyId = policyId, ReasonCode = rule.ReasonCode, EffectiveDate = effectiveDate, RefundAmount = refund, RefundBasis = rule.RefundBasis,
        Fee = rule.Fee, CancelledBy = by, CancelledAt = now,
    };

    internal void Reinstate(Guid by, DateTimeOffset now)
    {
        ReinstatedBy = by;
        ReinstatedAt = now;
    }
}

/// <summary>
/// The GeneralPolicy aggregate (general.policy, policy_risk, coverage, cancellation). It insures one or more risk
/// objects (GEN-002); its coverages stay within the product's limits (§7.2); its premium equals the sum of its
/// coverage premiums; renewal, cancellation and reinstatement follow product rules (GEN-004).
/// </summary>
public sealed class GeneralPolicy : AggregateRoot<Guid>
{
    public static readonly StateMachine<PolicyStatus, PolicyTrigger> Lifecycle = new StateMachine<PolicyStatus, PolicyTrigger>()
        .Allow(PolicyStatus.Issued, PolicyTrigger.PeriodStarts, PolicyStatus.InForce)
        .Allow(PolicyStatus.InForce, PolicyTrigger.CancelPolicy, PolicyStatus.Cancelled)
        .Allow(PolicyStatus.Cancelled, PolicyTrigger.ReinstatePolicy, PolicyStatus.InForce)
        .Allow(PolicyStatus.InForce, PolicyTrigger.PeriodEndsWithoutRenewal, PolicyStatus.Expired)
        .Allow(PolicyStatus.InForce, PolicyTrigger.RenewPolicy, PolicyStatus.Renewed);

    private readonly List<PolicyRisk> _risks = [];
    private readonly List<Cancellation> _cancellations = [];

    private GeneralPolicy()
    {
    }

    public string PolicyNumber { get; private set; } = "";

    public string ProductCode { get; private set; } = "";

    /// <summary>The organisation node (normally a branch) that owns the record (F103, addenda/access-scopes.md §2); its children inherit it.</summary>
    public string? OrgNodeCode { get; private set; }


    public int ProductVersion { get; private set; }

    public int RatingVersion { get; private set; }

    public Guid PolicyholderPartyId { get; private set; }

    public PolicyStatus Status { get; private set; }

    public DateOnly PeriodStart { get; private set; }

    public DateOnly PeriodEnd { get; private set; }

    public string Currency { get; private set; } = "";

    public decimal AnnualPremium { get; private set; }

    public string? IntermediaryCode { get; private set; }

    public Guid BillingAccountRef { get; private set; }

    public Guid? PayerPartyId { get; private set; }

    public string? BillingFrequency { get; private set; }

    public string? CollectionMethod { get; private set; }

    public Guid? RenewalOfId { get; private set; }

    /// <summary>When the renewal invitation was sent (GEN-004).</summary>
    public DateOnly? RenewalInvitedOn { get; private set; }

    public Guid? QuoteId { get; private set; }

    /// <summary>ISSUE: cover starts when the policy is issued and its period has started. FIRST_PAYMENT: also not before the first payment.</summary>
    public string CoverStartsOn { get; private set; } = "ISSUE";

    /// <summary>The day the first premium was paid in full, for cover that starts on first payment.</summary>
    public DateOnly? FirstPaidOn { get; private set; }

    /// <summary>Days of cover in the period: its end date is the last day of cover (OQ-059).</summary>
    public int PeriodDays => PeriodEnd.DayNumber - PeriodStart.DayNumber + 1;

    /// <summary>Days of cover from a date to the end of the period, both included.</summary>
    public int DaysFrom(DateOnly day) => PeriodEnd.DayNumber - day.DayNumber + 1;

    private bool CoverMayStart => CoverStartsOn == "ISSUE" || FirstPaidOn is not null;

    public IReadOnlyList<PolicyRisk> Risks => _risks;

    /// <summary>The sum insured of the risks attached on the day (reinsurance cessions, OQ-063).</summary>
    public decimal SumInsuredOn(DateOnly day) => _risks.Where(r => r.AttachedOn(day)).Sum(r => r.SumInsured);

    public IReadOnlyList<Cancellation> Cancellations => _cancellations;

    public Cancellation? CancellationInEffect => _cancellations.SingleOrDefault(c => c.ReinstatedAt is null);

    public IEnumerable<GeneralCoverage> Coverages => _risks.Where(r => r.DetachedOn is null).SelectMany(r => r.Coverages);

    public BillingArrangement Billing => new(BillingAccountRef, PayerPartyId ?? PolicyholderPartyId, BillingFrequency ?? "", CollectionMethod ?? "");

    /// <summary>Command IssuePolicy from the accepted quote version; in force at once when its period has started.</summary>
    public static Result<GeneralPolicy> IssuePolicy(Guid id, string policyNumber, GeneralQuote quote, BillingArrangement billing, Guid? renewalOfId, DateOnly today,
        DateTimeOffset now, string coverStartsOn = "ISSUE")
    {
        if (quote.Status != QuoteStatus.Accepted)
        {
            return Error.Conflict(GeneralErrorCodes.QuoteNotAccepted, $"A policy is issued from an accepted quote; quote {quote.QuoteNumber} v{quote.QuoteVersion} is {quote.Status}.");
        }

        var policy = New(id, policyNumber, quote.ProductCode, quote.ProductVersion, quote.RatingVersion!.Value, quote.ProposerPartyId, quote.Currency, quote.PeriodStart,
            quote.PeriodEnd, quote.IntermediaryCode, billing, renewalOfId, quote.Id);
        policy.CoverStartsOn = coverStartsOn;
        policy.OrgNodeCode = quote.OrgNodeCode;
        policy._risks.AddRange(quote.Risks.Select(r => PolicyRisk.Create(id, r.ToRequest(), quote.PeriodStart)));
        policy.SetPremium(quote.Premium!.Value);
        return policy.Issued(today, now);
    }

    /// <summary>Command RenewPolicy without re-rating: the next period, with the same risks, coverages and premium. The renewed policy becomes Renewed.</summary>
    public static Result<GeneralPolicy> RenewPolicy(Guid id, string policyNumber, GeneralPolicy expiring, DateOnly today, DateTimeOffset now)
    {
        if (expiring.Status != PolicyStatus.InForce)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Only a policy in force is renewed; {expiring.PolicyNumber} is {expiring.Status}.");
        }

        var start = expiring.PeriodEnd.AddDays(1);
        var renewal = New(id, policyNumber, expiring.ProductCode, expiring.ProductVersion, expiring.RatingVersion, expiring.PolicyholderPartyId, expiring.Currency, start,
            NextPeriodEnd(expiring.PeriodStart, expiring.PeriodEnd, start), expiring.IntermediaryCode, expiring.Billing, expiring.Id, null);
        renewal.CoverStartsOn = expiring.CoverStartsOn;
        renewal.OrgNodeCode = expiring.OrgNodeCode;
        foreach (var risk in expiring._risks.Where(r => r.DetachedOn is null))
        {
            renewal._risks.Add(PolicyRisk.Create(id, new RiskRequest(risk.RiskObjectId, risk.SumInsured,
                risk.Coverages.Select(c => new CoverageRequest(c.CoverageCode, c.SumInsured, c.ExcessScheduleCode)).ToList()), start));
        }

        renewal.SetPremium(expiring.AnnualPremium);
        return renewal.Issued(today, now);
    }

    /// <summary>The policy this one renews becomes Renewed (transition RenewPolicy).</summary>
    public Result MarkRenewed(Guid renewalPolicyId, DateTimeOffset now)
    {
        var result = Fire(PolicyTrigger.RenewPolicy);
        if (result.IsSuccess)
        {
            Raise(new GeneralPolicyRenewed(Id, PolicyNumber, renewalPolicyId, now));
        }

        return result;
    }

    /// <summary>
    /// The next period after <paramref name="start"/>: as many whole months as the expiring period (so a year stays a
    /// year across leap days), or else as many days.
    /// </summary>
    public static DateOnly NextPeriodEnd(DateOnly periodStart, DateOnly periodEnd, DateOnly start)
    {
        var after = periodEnd.AddDays(1);
        var months = (after.Year - periodStart.Year) * 12 + after.Month - periodStart.Month;
        return months > 0 && periodStart.AddMonths(months) == after
            ? start.AddMonths(months).AddDays(-1)
            : start.AddDays(periodEnd.DayNumber - periodStart.DayNumber);
    }

    /// <summary>Transition "Period starts": from its start date, and for cover that starts on first payment, once paid.</summary>
    public Result StartPeriod(DateOnly today) =>
        Status == PolicyStatus.Issued && PeriodStart <= today && CoverMayStart ? Fire(PolicyTrigger.PeriodStarts)
            : Error.Conflict(ErrorCodes.InvalidStateTransition, $"Policy {PolicyNumber} is {Status}, from {PeriodStart:yyyy-MM-dd}{(CoverMayStart ? "" : ", waiting for its first payment")}.");

    /// <summary>
    /// The first premium was paid in full (Billing's InvoiceSettled): for cover that starts on first payment, cover starts
    /// now if the period has started (domain-model.json: "Confirm cover where payment is a condition").
    /// </summary>
    public Result RecordFirstPayment(DateOnly paidOn, DateOnly today)
    {
        if (FirstPaidOn is not null)
        {
            return Result.Success();
        }

        FirstPaidOn = paidOn;
        MarkChanged();
        return Status == PolicyStatus.Issued && PeriodStart <= today ? Fire(PolicyTrigger.PeriodStarts) : Result.Success();
    }

    /// <summary>Transition "Period ends without renewal": the day after the last day of cover.</summary>
    /// <summary>
    /// The renewal invitation (GEN-004, OQ-066 item 3): once, for a policy in force, from the rule's notice days before
    /// its period ends. Documents generates it from the rule's invitation template.
    /// </summary>
    public Result InviteRenewal(int noticeDays, string templateCode, DateOnly today, DateTimeOffset now)
    {
        if (Status != PolicyStatus.InForce || RenewalInvitedOn is not null || today < PeriodEnd.AddDays(-noticeDays))
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Policy {PolicyNumber} is not due a renewal invitation.");
        }

        RenewalInvitedOn = today;
        Raise(new GeneralRenewalInvited(Id, PolicyNumber, ProductCode, ProductVersion, PeriodEnd, templateCode, now, PolicyholderPartyId));
        return Result.Success();
    }

    public Result ExpirePolicy(DateOnly today) =>
        Status == PolicyStatus.InForce && PeriodEnd < today ? Fire(PolicyTrigger.PeriodEndsWithoutRenewal)
            : Error.Conflict(ErrorCodes.InvalidStateTransition, $"Policy {PolicyNumber} is {Status}, to the end of {PeriodEnd:yyyy-MM-dd}.");

    /// <summary>
    /// Command CancelPolicy by the reason's refund rule (GEN-004): pro rata returns the unexpired part of the annual
    /// premium less the fee; none returns nothing. There is no cover from the effective date, so the unexpired days are
    /// the effective date to the end of the period, both included. TODO(spec): the short-period scale is not specified
    /// (OQ-059).
    /// </summary>
    public Result CancelPolicy(CancellationRule rule, DateOnly effectiveDate, Guid cancelledBy, DateTimeOffset now)
    {
        if (effectiveDate < PeriodStart || effectiveDate > PeriodEnd)
        {
            return Error.Validation(GeneralErrorCodes.OutsidePolicyPeriod, $"{effectiveDate:yyyy-MM-dd} is outside the policy period.");
        }

        decimal refund;
        switch (rule.RefundBasis)
        {
            case "PRO_RATA":
                var unexpired = AnnualPremium * DaysFrom(effectiveDate) / PeriodDays;
                refund = Math.Max(0, Math.Round(unexpired, 2, MidpointRounding.AwayFromZero) - rule.Fee);
                break;
            case "NONE":
                refund = 0;
                break;
            default:
                return Error.Validation(GeneralErrorCodes.RuleNotSpecified, $"The {rule.RefundBasis} refund basis has no scale configured.");
        }

        var result = Fire(PolicyTrigger.CancelPolicy);
        if (result.IsSuccess)
        {
            _cancellations.Add(Cancellation.Create(Id, rule, effectiveDate, refund, cancelledBy, now));
            Raise(new GeneralPolicyCancelled(Id, PolicyNumber, rule.ReasonCode, effectiveDate, refund, Currency, ProductCode, ProductVersion, now));
        }

        return result;
    }

    /// <summary>
    /// Command ReinstatePolicy (where permitted): the cancellation is kept as history. TODO(spec): the product rules
    /// that permit reinstatement are not specified, so it is permitted until the period's last day (OQ-059).
    /// </summary>
    public Result ReinstatePolicy(Guid reinstatedBy, DateOnly today, DateTimeOffset now)
    {
        if (Status == PolicyStatus.Cancelled && today > PeriodEnd)
        {
            return Error.Conflict(GeneralErrorCodes.ReinstatementNotPermitted, $"Policy {PolicyNumber}'s period has ended.");
        }

        var result = Fire(PolicyTrigger.ReinstatePolicy);
        if (result.IsSuccess)
        {
            CancellationInEffect!.Reinstate(reinstatedBy, now);
            Raise(new GeneralPolicyReinstated(Id, PolicyNumber, now));
        }

        return result;
    }

    /// <summary>
    /// Whether the policy covers a loss (GEN-006): the loss date is within the period, end date included, and before
    /// any cancellation in effect; for cover that starts on first payment, not before it was paid; the risk object was
    /// attached on that date, and the coverage is on it.
    /// </summary>
    public Result CoversLoss(Guid? riskObjectId, string coverageCode, DateOnly lossDate)
    {
        if (Status == PolicyStatus.Issued || lossDate < PeriodStart || lossDate > PeriodEnd
            || (CoverStartsOn == "FIRST_PAYMENT" && (FirstPaidOn is not { } paid || lossDate < paid)))
        {
            return Error.Validation(GeneralErrorCodes.NotCovered, $"Policy {PolicyNumber} was not in force on {lossDate:yyyy-MM-dd}.");
        }

        if (CancellationInEffect is { } cancelled && lossDate >= cancelled.EffectiveDate)
        {
            return Error.Validation(GeneralErrorCodes.NotCovered, $"Policy {PolicyNumber} was cancelled from {cancelled.EffectiveDate:yyyy-MM-dd}.");
        }

        var risks = _risks.Where(r => r.AttachedOn(lossDate) && (riskObjectId is null || r.RiskObjectId == riskObjectId)).ToList();
        if (risks.Count == 0 || !risks.Any(r => r.Coverages.Any(c => c.CoverageCode == coverageCode)))
        {
            return Error.Validation(GeneralErrorCodes.NotCovered,
                $"Policy {PolicyNumber} had no {coverageCode} cover{(riskObjectId is null ? "" : " on that risk object")} on {lossDate:yyyy-MM-dd}.");
        }

        return Result.Success();
    }

    /// <summary>The coverage that answers a claim on the risk object, if any.</summary>
    public GeneralCoverage? Coverage(Guid? riskObjectId, string coverageCode) =>
        _risks.Where(r => riskObjectId is null || r.RiskObjectId == riskObjectId).SelectMany(r => r.Coverages).FirstOrDefault(c => c.CoverageCode == coverageCode);

    /// <summary>
    /// Command ApplyEndorsement: an approved endorsement's changes, effective from its date, and the new annual premium
    /// (transition "Policy updated" of the endorsement). Coverages stay within the product's limits.
    /// </summary>
    public Result ApplyEndorsement(GeneralEndorsement endorsement, ProductTerms product, IReadOnlyList<string> documentTypes, DateTimeOffset now)
    {
        if (endorsement.PolicyId != Id || endorsement.Status != EndorsementStatus.Approved)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Only an approved endorsement of this policy is applied.");
        }

        if (Status is not (PolicyStatus.Issued or PolicyStatus.InForce))
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Policy {PolicyNumber} is {Status}.");
        }

        foreach (var change in endorsement.Changes)
        {
            var applied = Apply(change, endorsement.EffectiveDate);
            if (applied.IsFailure)
            {
                return applied;
            }
        }

        var limits = product.Check(Coverages.Select(c => new CoverageRequest(c.CoverageCode, c.SumInsured, c.ExcessScheduleCode)), Currency);
        if (limits.IsFailure || !_risks.Any(r => r.DetachedOn is null))
        {
            return limits.IsFailure ? limits : Error.Validation(ErrorCodes.ValidationFailed, "A policy keeps at least one risk object.");
        }

        SetPremium(endorsement.NewAnnualPremium!.Value);
        Raise(new GeneralPolicyEndorsed(Id, PolicyNumber, endorsement.Id, endorsement.EndorsementNumber, endorsement.EffectiveDate, AnnualPremium,
            endorsement.PremiumDelta, Currency, ProductCode, ProductVersion, documentTypes, SumInsuredOn(endorsement.EffectiveDate), now, PolicyholderPartyId));
        MarkChanged();
        return Result.Success();
    }

    private Result Apply(EndorsementChange change, DateOnly effectiveDate)
    {
        var risk = _risks.FirstOrDefault(r => r.RiskObjectId == change.RiskObjectId && r.DetachedOn is null);
        switch (change.Action)
        {
            case EndorsementAction.AttachRisk when risk is null && change.SumInsured > 0 && change.Coverages.Count > 0:
                _risks.Add(PolicyRisk.Create(Id, new RiskRequest(change.RiskObjectId, change.SumInsured!.Value, change.Coverages), effectiveDate));
                return Result.Success();
            case EndorsementAction.DetachRisk when risk is not null:
                risk.Detach(effectiveDate);
                return Result.Success();
            case EndorsementAction.ChangeRisk when risk is not null:
                if (change.SumInsured is > 0)
                {
                    risk.SetSumInsured(change.SumInsured.Value);
                }

                foreach (var coverage in change.Coverages)
                {
                    var existing = risk.Coverages.FirstOrDefault(c => c.CoverageCode == coverage.CoverageCode);
                    if (coverage.SumInsured == 0 && existing is not null)
                    {
                        risk.RemoveCoverage(existing);
                    }
                    else if (existing is not null)
                    {
                        existing.Change(coverage.SumInsured, coverage.ExcessScheduleCode);
                    }
                    else if (coverage.SumInsured > 0)
                    {
                        risk.AddCoverage(coverage);
                    }
                }

                return risk.Coverages.Count > 0
                    ? Result.Success()
                    : Error.Validation(ErrorCodes.ValidationFailed, "A risk object keeps at least one coverage; detach it instead.");
            default:
                return Error.Validation(GeneralErrorCodes.EndorsementChangeInvalid, $"{change.Action} cannot be applied to risk object {change.RiskObjectId}.");
        }
    }

    /// <summary>
    /// The premium equals the sum of the coverage premiums. TODO(spec): rating gives one premium, so it is spread over
    /// the coverages in proportion to their sums insured, any rounding difference on the largest (OQ-059).
    /// </summary>
    private void SetPremium(decimal annualPremium)
    {
        AnnualPremium = annualPremium;
        var coverages = Coverages.OrderByDescending(c => c.SumInsured).ThenBy(c => c.CoverageCode, StringComparer.Ordinal).ToList();
        var total = coverages.Sum(c => c.SumInsured);
        foreach (var coverage in coverages)
        {
            coverage.Premium = total == 0 ? 0 : Math.Round(annualPremium * coverage.SumInsured / total, 2, MidpointRounding.AwayFromZero);
        }

        if (coverages.Count > 0)
        {
            coverages[0].Premium += annualPremium - coverages.Sum(c => c.Premium);
        }

        foreach (var detached in _risks.Where(r => r.DetachedOn is not null).SelectMany(r => r.Coverages))
        {
            detached.Premium = 0;
        }
    }

    private static GeneralPolicy New(Guid id, string number, string productCode, int productVersion, int ratingVersion, Guid holder, string currency, DateOnly start,
        DateOnly end, string? intermediary, BillingArrangement billing, Guid? renewalOfId, Guid? quoteId) => new()
    {
        Id = Guard.NotEmpty(id),
        PolicyNumber = Guard.MaxLength(number, 15),
        ProductCode = productCode,
        ProductVersion = productVersion,
        RatingVersion = ratingVersion,
        PolicyholderPartyId = holder,
        Currency = currency,
        PeriodStart = start,
        PeriodEnd = end,
        IntermediaryCode = intermediary,
        BillingAccountRef = Guard.NotEmpty(billing.BillingAccountRef),
        PayerPartyId = billing.PayerPartyId,
        BillingFrequency = Guard.MaxLength(billing.Frequency, 15),
        CollectionMethod = Guard.MaxLength(billing.CollectionMethod, 15),
        RenewalOfId = renewalOfId,
        QuoteId = quoteId,
        Status = PolicyStatus.Issued,
    };

    private Result<GeneralPolicy> Issued(DateOnly today, DateTimeOffset now)
    {
        Raise(new GeneralPolicyIssued(Id, PolicyNumber, ProductCode, ProductVersion, PolicyholderPartyId, Billing, Currency, AnnualPremium, PeriodStart, PeriodEnd,
            RenewalOfId, SumInsuredOn(PeriodStart), now));
        if (PeriodStart <= today && CoverMayStart)
        {
            Fire(PolicyTrigger.PeriodStarts);
        }

        MarkChanged();
        return this;
    }

    private Result Fire(PolicyTrigger trigger)
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
