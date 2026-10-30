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
    CancelFromInception,
    ReinstateBeforeStart,
}

/// <summary>How the policy is billed: the account, payer, frequency and collection method.</summary>
public sealed record BillingArrangement(Guid BillingAccountRef, Guid PayerPartyId, string Frequency, string CollectionMethod);

/// <summary>
/// A cancellation reason and its refund rule (general.cfg_cancellation_reasons_and_refund_rules, GEN-004). A SHORT_PERIOD
/// reason carries its scale's bands (general.cfg_short_period_scales, OQ-133); none means the scale is not configured.
/// </summary>
public sealed record CancellationRule(string ReasonCode, string Initiator, string RefundBasis, decimal Fee, IReadOnlyList<ShortPeriodBand>? Scale = null);

/// <summary>A band of a short-period scale: for a policy in force up to <paramref name="DaysInForceTo"/> days, the insurer keeps this per cent of the annual premium.</summary>
public sealed record ShortPeriodBand(int DaysInForceTo, decimal PercentRetained);

/// <summary>
/// A product's grace for a billing frequency (general.cfg_grace_period_rules, F006): its days, whether cover continues, the
/// reason it cancels with when unpaid, the claim decision a loss in grace is declined with then, and whether a claim payment
/// may be made less the arrears instead of waiting for them.
/// </summary>
public sealed record GraceTerms(int GraceDays, bool CoverDuringGrace, string CancellationReason, string ClaimDeclineReason = "NON_PAYMENT", bool DeductArrearsFromClaims = false);

/// <summary>A policy's arrears were paid (F006): claim payments held for them are released.</summary>
public sealed record GeneralPolicyArrearsCleared(Guid PolicyId, string PolicyNumber, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Grace ended unpaid (F006): the policy's claims for losses from the cancellation date are declined with the grace rule's claim decision.</summary>
public sealed record GeneralPolicyLapsedUnpaid(Guid PolicyId, string PolicyNumber, DateOnly CancelledFrom, string ClaimDeclineReason, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>A missed instalment started grace (F006): the policyholder is told cover ends on <paramref name="CoverEndsOn"/> unless paid (Wave 4 addition 1).</summary>
public sealed record GeneralPolicyEnteredGrace(Guid PolicyId, string PolicyNumber, string ProductCode, Guid PolicyholderPartyId, DateOnly ArrearsSince, DateOnly GraceUntil,
    DateOnly CoverEndsOn, bool CoverDuringGrace, string? OrgNodeCode, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Grace ended unpaid and the policy was cancelled for non-payment (F006): the policyholder is told (Wave 4 addition 1).</summary>
public sealed record GeneralPolicyCancelledForNonPayment(Guid PolicyId, string PolicyNumber, string ProductCode, Guid PolicyholderPartyId, DateOnly EffectiveDate,
    decimal RefundAmount, string Currency, string? OrgNodeCode, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record GeneralPolicyIssued(Guid PolicyId, string PolicyNumber, string ProductCode, int ProductVersion, Guid PolicyholderPartyId, BillingArrangement Billing,
    string Currency, decimal AnnualPremium, DateOnly PeriodStart, DateOnly PeriodEnd, Guid? RenewalOfId, decimal SumInsured, DateTimeOffset OccurredAt, string? OrgNodeCode = null,
    decimal? InstalmentLoadingPct = null) : IDomainEvent;

public sealed record GeneralPolicyEndorsed(Guid PolicyId, string PolicyNumber, Guid EndorsementId, string EndorsementNumber, DateOnly EffectiveDate,
    decimal NewAnnualPremium, decimal PremiumDelta, string Currency, string ProductCode, int ProductVersion, IReadOnlyList<string> DocumentTypes, decimal NewSumInsured,
    DateTimeOffset OccurredAt, Guid? PolicyholderPartyId = null) : IDomainEvent;

public sealed record GeneralRenewalReminderDue(Guid PolicyId, string PolicyNumber, string ProductCode, DateOnly PeriodEnd, int DaysBefore, Guid PolicyholderPartyId,
    string? OrgNodeCode, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record GeneralPolicyRenewed(Guid PolicyId, string PolicyNumber, Guid RenewalPolicyId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record GeneralRenewalInvited(Guid PolicyId, string PolicyNumber, string ProductCode, int ProductVersion, DateOnly PeriodEnd, string TemplateCode,
    DateTimeOffset OccurredAt, Guid? PolicyholderPartyId = null, decimal? OfferedPremium = null, string? Currency = null) : IDomainEvent;

public sealed record GeneralPolicyCancelled(Guid PolicyId, string PolicyNumber, string ReasonCode, DateOnly EffectiveDate, decimal RefundAmount, string Currency,
    string ProductCode, int ProductVersion, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record GeneralPolicyReinstated(Guid PolicyId, string PolicyNumber, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>OQ-132 (b): cover runs on, pro rata, to <paramref name="NewPeriodEnd"/> for a non-renewal's notice; Billing bills the extra days.</summary>
public sealed record GeneralCoverExtended(Guid PolicyId, string PolicyNumber, DateOnly PreviousPeriodEnd, DateOnly NewPeriodEnd, decimal ExtraPremium, string Currency,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// B-001: the policy moved to another intermediary from <paramref name="EffectiveDate"/>: access moves with it (external-user
/// access §2), and commission splits pro rata at the date (OQ-135).
/// </summary>
public sealed record GeneralPolicyIntermediaryChanged(Guid PolicyId, string PolicyNumber, string ProductCode, string? PreviousIntermediaryCode, string IntermediaryCode,
    DateOnly EffectiveDate, DateOnly PeriodEnd, string? BillingFrequency, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Why a policy expired without renewal (Wave 4 §4.3, §4.4; general.policy.expiry_reason).</summary>
public static class PolicyExpiryReasons
{
    public const string NotRenewedByInsurer = "NOT_RENEWED_BY_INSURER";
    public const string DeclinedByCustomer = "DECLINED_BY_CUSTOMER";
    public const string Lapsed = "LAPSED";
}

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
/// <summary>A record another module made for a policy (BR-038): an invoice, a document or a reinsurance cession, from its event.</summary>
public sealed class PolicyEffect : Entity<Guid>, ITenantScoped
{
    public const string Invoice = "INVOICE";

    public const string Document = "DOCUMENT";

    public const string Cession = "CESSION";

    private PolicyEffect()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid PolicyId { get; private set; }

    public string EffectType { get; private set; } = "";

    public Guid RecordId { get; private set; }

    public static PolicyEffect Record(Guid policyId, string effectType, Guid recordId) =>
        new() { Id = Guid.CreateVersion7(), PolicyId = Guard.NotEmpty(policyId), EffectType = effectType, RecordId = Guard.NotEmpty(recordId) };
}

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

    internal static Cancellation Create(Guid policyId, CancellationRule rule, DateOnly effectiveDate, decimal refund, Guid? by, DateTimeOffset now) => new()
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
        .Allow(PolicyStatus.InForce, PolicyTrigger.RenewPolicy, PolicyStatus.Renewed)
        // BR-037 (decided by Ade, 28 October 2026): an Issued policy not yet in force is cancelled only from inception, and
        // never renewed; reinstated before its period starts, it is Issued again.
        .Allow(PolicyStatus.Issued, PolicyTrigger.CancelFromInception, PolicyStatus.Cancelled)
        .Allow(PolicyStatus.Cancelled, PolicyTrigger.ReinstateBeforeStart, PolicyStatus.Issued);

    private readonly List<PolicyRisk> _risks = [];
    private readonly List<Cancellation> _cancellations = [];

    private GeneralPolicy()
    {
    }

    public string PolicyNumber { get; private set; } = "";

    public string ProductCode { get; private set; } = "";

    /// <summary>The organisation node (normally a branch) that owns the record (F103, addenda/access-scopes.md §2); its children inherit it.</summary>
    public string? OrgNodeCode { get; private set; }

    /// <summary>The channel of the quote the policy was issued from (F005); a renewal keeps it.</summary>
    public string? ChannelCode { get; private set; }


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

    /// <summary>The first instalment still unpaid past its due date (Billing, BR-103); null when none is.</summary>
    public DateOnly? ArrearsSince { get; private set; }

    /// <summary>How many instalments are unpaid past their due date.</summary>
    public int ArrearsCount { get; private set; }

    /// <summary>The last day of grace for the unpaid instalments (F006); cancelled for non-payment after it if still unpaid.</summary>
    public DateOnly? GraceUntil { get; private set; }

    /// <summary>Whether the product keeps cover in grace for the policy's frequency; null while not in grace.</summary>
    public bool? CoverDuringGrace { get; private set; }

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

    /// <summary>
    /// The instalment loading in per cent the policy was quoted with in its billing frequency (Ade, 28 October 2026): Billing
    /// charges each instalment with it, and the year totals the loaded annual premium. Null when none.
    /// </summary>
    public decimal? InstalmentLoadingPct { get; private set; }

    /// <summary>Command IssuePolicy from the accepted quote version; in force at once when its period has started.</summary>
    public static Result<GeneralPolicy> IssuePolicy(Guid id, string policyNumber, GeneralQuote quote, BillingArrangement billing, Guid? renewalOfId, DateOnly today,
        DateTimeOffset now, string coverStartsOn = "ISSUE")
    {
        if (quote.Status != QuoteStatus.Accepted)
        {
            return Error.Conflict(GeneralErrorCodes.QuoteNotAccepted, $"A policy is issued from an accepted quote; quote {quote.QuoteNumber} v{quote.QuoteVersion} is {quote.Status}.");
        }

        // A quote priced in a frequency is paid in it, with its loading (Ade, 28 October 2026).
        if (quote.PremiumFrequency is { } quoted && quoted != billing.Frequency)
        {
            return Error.Validation(GeneralErrorCodes.FrequencyNotQuoted,
                $"Quote {quote.QuoteNumber} v{quote.QuoteVersion} is priced {quoted}; the policy is billed {quoted}, or the quote is made again in {billing.Frequency}.");
        }

        var policy = New(id, policyNumber, quote.ProductCode, quote.ProductVersion, quote.RatingVersion!.Value, quote.ProposerPartyId, quote.Currency, quote.PeriodStart,
            quote.PeriodEnd, quote.IntermediaryCode, billing, renewalOfId, quote.Id);
        policy.CoverStartsOn = coverStartsOn;
        policy.OrgNodeCode = quote.OrgNodeCode;
        policy.ChannelCode = quote.ChannelCode;
        policy._risks.AddRange(quote.Risks.Select(r => PolicyRisk.Create(id, r.ToRequest(), quote.PeriodStart)));
        policy.SetPremium(quote.Premium!.Value);
        policy.InstalmentLoadingPct = quote.PremiumLoadingPct is > 0 ? quote.PremiumLoadingPct : null;
        return policy.Issued(today, now);
    }

    /// <summary>Command RenewPolicy without re-rating: the next period, with the same risks, coverages and premium. The renewed policy becomes Renewed.</summary>
    /// <param name="premium">The renewal review's offered premium (Wave 4 §4.2), when it was reviewed; otherwise the expiring premium.</param>
    public static Result<GeneralPolicy> RenewPolicy(Guid id, string policyNumber, GeneralPolicy expiring, DateOnly today, DateTimeOffset now, decimal? premium = null)
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
        renewal.ChannelCode = expiring.ChannelCode;
        foreach (var risk in expiring._risks.Where(r => r.DetachedOn is null))
        {
            renewal._risks.Add(PolicyRisk.Create(id, new RiskRequest(risk.RiskObjectId, risk.SumInsured,
                risk.Coverages.Select(c => new CoverageRequest(c.CoverageCode, c.SumInsured, c.ExcessScheduleCode)).ToList()), start));
        }

        renewal.SetPremium(premium ?? expiring.AnnualPremium);
        renewal.InstalmentLoadingPct = expiring.InstalmentLoadingPct;
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
    /// <param name="offeredPremium">The reviewed renewal's offered premium (Wave 4 §4.2), which the invitation states.</param>
    public Result InviteRenewal(int noticeDays, string templateCode, DateOnly today, DateTimeOffset now, decimal? offeredPremium = null)
    {
        if (Status != PolicyStatus.InForce || RenewalInvitedOn is not null || today < PeriodEnd.AddDays(-noticeDays))
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Policy {PolicyNumber} is not due a renewal invitation.");
        }

        RenewalInvitedOn = today;
        Raise(new GeneralRenewalInvited(Id, PolicyNumber, ProductCode, ProductVersion, PeriodEnd, templateCode, now, PolicyholderPartyId, offeredPremium, Currency));
        return Result.Success();
    }

    /// <summary>
    /// A renewal reminder (OQ-089, communications §6.2): for a policy in force, invited and not renewed, on one of the
    /// rule's reminder days before its period ends. The reminder is announced; the policy itself does not change.
    /// </summary>
    public Result RemindRenewal(int daysBefore, DateOnly today, DateTimeOffset now)
    {
        if (Status != PolicyStatus.InForce || RenewalInvitedOn is null || PeriodEnd.AddDays(-daysBefore) != today)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Policy {PolicyNumber} is not due a renewal reminder.");
        }

        Raise(new GeneralRenewalReminderDue(Id, PolicyNumber, ProductCode, PeriodEnd, daysBefore, PolicyholderPartyId, OrgNodeCode, now));
        return Result.Success();
    }

    /// <param name="reason">Why it was not renewed (PolicyExpiryReasons), when the renewal says.</param>
    public Result ExpirePolicy(DateOnly today, string? reason = null)
    {
        if (Status != PolicyStatus.InForce || PeriodEnd >= today)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Policy {PolicyNumber} is {Status}, to the end of {PeriodEnd:yyyy-MM-dd}.");
        }

        ExpiryReason = reason;
        return Fire(PolicyTrigger.PeriodEndsWithoutRenewal);
    }

    /// <summary>Why the policy expired without renewal (Wave 4 §4.3, §4.4), when known.</summary>
    public string? ExpiryReason { get; private set; }

    /// <summary>
    /// OQ-132 (b): a non-renewal decided too late for its notice keeps the policy covered until the notice runs out, at the
    /// annual premium pro rata for the extra days (a 365-day year, to the cent).
    /// </summary>
    public Result ExtendForNotice(DateOnly coverEndsOn, DateTimeOffset now)
    {
        if (Status != PolicyStatus.InForce || coverEndsOn <= PeriodEnd)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Policy {PolicyNumber} is not extended past {PeriodEnd:yyyy-MM-dd}.");
        }

        var previous = PeriodEnd;
        var extra = Math.Round(AnnualPremium * (coverEndsOn.DayNumber - previous.DayNumber) / 365m, 2, MidpointRounding.AwayFromZero);
        PeriodEnd = coverEndsOn;
        Raise(new GeneralCoverExtended(Id, PolicyNumber, previous, coverEndsOn, extra, Currency, now));
        MarkChanged();
        return Result.Success();
    }

    /// <summary>
    /// Command CancelPolicy by the reason's refund rule (GEN-004): pro rata returns the unexpired part of the annual
    /// premium less the fee; none returns nothing. There is no cover from the effective date, so the unexpired days are
    /// the effective date to the end of the period, both included. TODO(spec): the short-period scale is not specified
    /// (OQ-059).
    /// </summary>
    public Result CancelPolicy(CancellationRule rule, DateOnly effectiveDate, Guid? cancelledBy, DateTimeOffset now)
    {
        if (effectiveDate < PeriodStart || effectiveDate > PeriodEnd)
        {
            return Error.Validation(GeneralErrorCodes.OutsidePolicyPeriod, $"{effectiveDate:yyyy-MM-dd} is outside the policy period.");
        }

        // BR-037: a policy not yet in force has given no cover, so it is cancelled from inception or not at all.
        if (Status == PolicyStatus.Issued && effectiveDate != PeriodStart)
        {
            return Error.Validation(GeneralErrorCodes.OutsidePolicyPeriod,
                $"Policy {PolicyNumber} is not yet in force: it is cancelled from inception ({PeriodStart:yyyy-MM-dd}) or not at all.");
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
            case "SHORT_PERIOD" when rule.Scale is { Count: > 0 } scale:
                // F007, OQ-133: the insurer keeps the scale's per cent of the annual premium for the days in force (the
                // period start to the day before the effective date); the first band covering them applies, and past the
                // last band it keeps it all. Never below zero, less the reason's fee.
                var inForce = effectiveDate.DayNumber - PeriodStart.DayNumber;
                var retainedPct = scale.OrderBy(b => b.DaysInForceTo).FirstOrDefault(b => inForce <= b.DaysInForceTo)?.PercentRetained ?? 100m;
                var retained = Math.Round(AnnualPremium * retainedPct / 100m, 2, MidpointRounding.AwayFromZero);
                refund = Math.Max(0, AnnualPremium - retained - rule.Fee);
                break;
            default:
                return Error.Validation(GeneralErrorCodes.RuleNotSpecified, $"The {rule.RefundBasis} refund basis has no scale configured.");
        }

        var result = Fire(Status == PolicyStatus.Issued ? PolicyTrigger.CancelFromInception : PolicyTrigger.CancelPolicy);
        if (result.IsSuccess)
        {
            _cancellations.Add(Cancellation.Create(Id, rule, effectiveDate, refund, cancelledBy, now));
            Raise(new GeneralPolicyCancelled(Id, PolicyNumber, rule.ReasonCode, effectiveDate, refund, Currency, ProductCode, ProductVersion, now));
            ClearArrears();
        }

        return result;
    }

    /// <summary>
    /// Command RecordMissedPremium (F006): the instalments Billing finds unpaid past their due date, as of its detection date
    /// (BR-103). The first starts grace for the product's days, from the oldest unpaid due date, and tells the policyholder
    /// when cover ends unless paid; a later instalment missed in the same grace changes nothing but the count. None left (a
    /// payment in grace) clears it. Only a policy in force has grace.
    /// </summary>
    public Result RecordArrears(int overdueInstalments, DateOnly? oldestDue, GraceTerms grace, DateTimeOffset now)
    {
        if (Status != PolicyStatus.InForce)
        {
            return Result.Success();
        }

        if (overdueInstalments <= 0 || oldestDue is not { } since)
        {
            if (ArrearsSince is not null)
            {
                ClearArrears();
                MarkChanged();
                Raise(new GeneralPolicyArrearsCleared(Id, PolicyNumber, now));
            }

            return Result.Success();
        }

        if (ArrearsSince == since)
        {
            if (ArrearsCount != overdueInstalments)
            {
                ArrearsCount = overdueInstalments;
                MarkChanged();
            }

            return Result.Success();
        }

        // A new grace: the first missed instalment, or an earlier one paid and a later one now the oldest unpaid.
        ArrearsSince = since;
        ArrearsCount = overdueInstalments;
        GraceUntil = since.AddDays(grace.GraceDays);
        CoverDuringGrace = grace.CoverDuringGrace;
        MarkChanged();
        var coverEnds = grace.CoverDuringGrace ? GraceUntil.Value : since.AddDays(-1);
        Raise(new GeneralPolicyEnteredGrace(Id, PolicyNumber, ProductCode, PolicyholderPartyId, since, GraceUntil.Value, coverEnds, grace.CoverDuringGrace, OrgNodeCode, now));
        return Result.Success();
    }

    /// <summary>
    /// Command CancelForNonPayment (F006): grace has ended with instalments still unpaid, so the policy is cancelled with the
    /// grace rule's reason and its refund rule, and the policyholder is told. Without cover in grace, from the first unpaid
    /// instalment's due date (the day after the last paid period); with it, from the day after grace ended, as the grace
    /// notice said. Claims for losses from then are declined with the rule's claim decision.
    /// </summary>
    public Result CancelForNonPayment(CancellationRule rule, GraceTerms grace, DateOnly today, DateTimeOffset now)
    {
        if (Status != PolicyStatus.InForce || GraceUntil is not { } graceUntil || graceUntil >= today || ArrearsSince is not { } since)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Policy {PolicyNumber} is not past its grace ({GraceUntil:yyyy-MM-dd}) with instalments unpaid.");
        }

        var from = CoverDuringGrace == true ? graceUntil.AddDays(1) : since;
        var effective = from < PeriodStart ? PeriodStart : from > PeriodEnd ? PeriodEnd : from;
        var cancelled = CancelPolicy(rule, effective, null, now); // by the daily job, not a person
        if (cancelled.IsSuccess)
        {
            var refund = CancellationInEffect!.RefundAmount;
            Raise(new GeneralPolicyCancelledForNonPayment(Id, PolicyNumber, ProductCode, PolicyholderPartyId, effective, refund, Currency, OrgNodeCode, now));
            Raise(new GeneralPolicyLapsedUnpaid(Id, PolicyNumber, effective, grace.ClaimDeclineReason, now));
        }

        return cancelled;
    }

    /// <summary>
    /// Whether a loss on the day falls in grace with no cover in it (F006, addendum §5): the claim is accepted, but its
    /// payment waits until the arrears are paid, and it is declined if grace ends unpaid.
    /// </summary>
    public bool LossAwaitsArrears(DateOnly lossDate) => ArrearsSince is { } since && CoverDuringGrace == false && lossDate >= since;

    private void ClearArrears()
    {
        ArrearsSince = null;
        ArrearsCount = 0;
        GraceUntil = null;
        CoverDuringGrace = null;
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

        var result = Fire(today < PeriodStart ? PolicyTrigger.ReinstateBeforeStart : PolicyTrigger.ReinstatePolicy); // BR-037
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

    /// <summary>Whether an endorsement can still be applied: the policy is issued or in force (not cancelled, expired or renewed).</summary>
    public bool TakesEndorsements => Status is PolicyStatus.Issued or PolicyStatus.InForce;

    /// <summary>
    /// Command ApplyEndorsement: an approved endorsement's changes, effective from its date, and the new annual premium
    /// (transition "Policy updated" of the endorsement). Coverages stay within the product's limits.
    /// </summary>
    /// <summary>B-001: a broker of record change approved for a later date, waiting for it (OQ-136: until then only the current broker sees the policy).</summary>
    public string? PendingIntermediaryCode { get; private set; }

    public DateOnly? PendingIntermediaryFrom { get; private set; }

    /// <summary>B-001: the policy moves to the intermediary today, or waits for a later effective date (the daily job then moves it).</summary>
    public Result ChangeIntermediary(string intermediaryCode, DateOnly effectiveDate, DateOnly today, DateTimeOffset now)
    {
        if (!TakesEndorsements)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Policy {PolicyNumber} is {Status}.");
        }

        if (effectiveDate > today)
        {
            (PendingIntermediaryCode, PendingIntermediaryFrom) = (intermediaryCode, effectiveDate);
            MarkChanged();
            return Result.Success();
        }

        var previous = IntermediaryCode;
        IntermediaryCode = intermediaryCode;
        (PendingIntermediaryCode, PendingIntermediaryFrom) = (null, null);
        Raise(new GeneralPolicyIntermediaryChanged(Id, PolicyNumber, ProductCode, previous, intermediaryCode, effectiveDate, PeriodEnd, BillingFrequency, now));
        MarkChanged();
        return Result.Success();
    }

    /// <summary>B-001: a pending broker of record change whose effective date has come (the daily job).</summary>
    public Result ApplyPendingIntermediary(DateOnly today, DateTimeOffset now) =>
        PendingIntermediaryCode is { } code && PendingIntermediaryFrom is { } from && from <= today
            ? ChangeIntermediary(code, from, today, now)
            : Error.Conflict(ErrorCodes.InvalidStateTransition, $"Policy {PolicyNumber} has no broker of record change due.");

    public Result ApplyEndorsement(GeneralEndorsement endorsement, ProductTerms product, IReadOnlyList<string> documentTypes, DateTimeOffset now, DateOnly today)
    {
        if (endorsement.PolicyId != Id || endorsement.Status != EndorsementStatus.Approved)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Only an approved endorsement of this policy is applied.");
        }

        if (!TakesEndorsements)
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

        if (endorsement.NewIntermediaryCode is { } intermediary)
        {
            var moved = ChangeIntermediary(intermediary, endorsement.EffectiveDate, today, now);
            if (moved.IsFailure)
            {
                return moved;
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
            RenewalOfId, SumInsuredOn(PeriodStart), now, OrgNodeCode, InstalmentLoadingPct));
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
