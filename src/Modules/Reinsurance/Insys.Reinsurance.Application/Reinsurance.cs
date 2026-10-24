/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Reinsurance
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
using FluentValidation;
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Distribution.Contracts;
using Insys.General.Contracts;
using Insys.Integration.Contracts;
using Insys.Life.Contracts;
using Insys.Reinsurance.Domain;
using Microsoft.Extensions.Logging;

namespace Insys.Reinsurance.Application;

public interface IReinsuranceRepository
{
    /// <summary>The cessions in effect for a policy: the latest per treaty (one superseded is no longer in effect).</summary>
    Task<IReadOnlyList<Cession>> CurrentCessions(string sourceType, Guid policyRef, CancellationToken cancellationToken);

    Task<IReadOnlyList<Cession>> CessionsFor(string sourceType, Guid policyRef, CancellationToken cancellationToken);

    /// <summary>Cessions under a treaty calculated in a period (bordereaux).</summary>
    Task<IReadOnlyList<Cession>> CessionsCalculated(string treatyCode, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    Task<ReinsuranceRecovery?> Recovery(Guid id, CancellationToken cancellationToken);

    Task<ReinsuranceRecovery?> Recovery(string claimNumber, string treatyCode, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReinsuranceRecovery>> Recoveries(string? claimNumber, RecoveryStatus? status, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<Bordereau?> Bordereau(Guid id, CancellationToken cancellationToken);

    Task<Bordereau?> Bordereau(string treatyCode, string periodCode, CancellationToken cancellationToken);

    void Add(Cession cession);

    void Add(ReinsuranceRecovery recovery);

    void Add(Bordereau bordereau);
}

/// <summary>The Reinsurance module's static data in force.</summary>
public interface IReinsuranceCodes
{
    /// <summary>The cession rules naming the product, each with its treaty version in force on the day.</summary>
    Task<IReadOnlyList<(CessionRule Rule, TreatyTerms Treaty)>> ArrangementsFor(string productCode, DateOnly day, CancellationToken cancellationToken);

    Task<TreatyTerms?> Treaty(string treatyCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>A facultative arrangement in force for a risk reference (the policy number).</summary>
    Task<FacultativeArrangement?> Facultative(string riskReference, DateOnly day, CancellationToken cancellationToken);

    Task<BordereauLayout?> Layout(string layoutCode, DateOnly day, CancellationToken cancellationToken);
}

public sealed record CessionView(Guid Id, string TreatyCode, int? TreatyVersion, string? ArrangementCode, string SourceType, Guid PolicyRef, string ProductCode,
    DateOnly PeriodStart, DateOnly PeriodEnd, decimal GrossSumInsured, decimal GrossPremium, decimal CededShare, decimal CededSumInsured, decimal CededPremium,
    string Currency, string RuleCode, int RuleVersion, DateTimeOffset CalculatedAt, Guid? SupersedesId)
{
    public static CessionView From(Cession c) => new(c.Id, c.TreatyCode, c.TreatyVersion, c.ArrangementCode, c.SourceType, c.SourceId, c.ProductCode, c.PeriodStart,
        c.PeriodEnd, c.GrossSumInsured, c.GrossPremium, c.CededShare, c.CededSumInsured, c.CededPremium, c.Currency, c.RuleCode, c.RuleVersion, c.CalculatedAt,
        c.SupersedesId);
}

public sealed record RecoveryView(Guid Id, string ClaimNumber, Guid ClaimId, string SourceDomain, Guid PolicyRef, string TreatyCode, Guid? CessionId, decimal PaidAmount,
    decimal Recoverable, string Currency, RecoveryStatus Status, DateOnly? BilledOn, decimal? SettledAmount, DateOnly? SettledOn, decimal? Difference, long RowVersion)
{
    public static RecoveryView From(ReinsuranceRecovery r) => new(r.Id, r.ClaimNumber, r.ClaimId, r.SourceDomain, r.PolicyRef, r.TreatyCode, r.CessionId, r.PaidAmount,
        r.Recoverable, r.Currency, r.Status, r.BilledOn, r.SettledAmount, r.SettledOn, r.Difference, r.RowVersion);
}

public sealed record BordereauView(Guid Id, string TreatyCode, string PeriodCode, string LayoutCode, int RowCount, BordereauStatus Status, string Content, string? PartnerRef,
    long RowVersion)
{
    public static BordereauView From(Bordereau b) => new(b.Id, b.TreatyCode, b.PeriodCode, b.LayoutCode, b.RowCount, b.Status, b.Content, b.PartnerRef,
        b.RowVersion);
}

public sealed record ListCessions(Guid PolicyRef, string SourceDomain) : IQuery<IReadOnlyList<CessionView>>;

public sealed record GetRecovery(Guid RecoveryId) : IQuery<RecoveryView>;

public sealed record ListRecoveries(string? ClaimNumber, RecoveryStatus? Status, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<RecoveryView>>;

public sealed record GetBordereau(Guid BordereauId) : IQuery<BordereauView>;

/// <summary>Command BillRecovery, by a person, for a recovery the reinsurer adapter did not accept when it was identified.</summary>
public sealed record BillRecovery(Guid RecoveryId) : ICommand<RecoveryView>;

/// <summary>Command SettleRecovery: what the reinsurer paid (REI-003).</summary>
public sealed record SettleRecovery(Guid RecoveryId, decimal Amount, DateOnly SettledOn) : ICommand<RecoveryView>;

/// <summary>Command PrepareBordereau for a layout and a period (yyyy-MM, or yyyy-Qn for a quarterly layout); one per treaty and period.</summary>
public sealed record PrepareBordereau(string LayoutCode, string PeriodCode) : ICommand<BordereauView>;

public sealed record SendBordereau(Guid BordereauId) : ICommand<BordereauView>;

public sealed record AgreeBordereau(Guid BordereauId) : ICommand<BordereauView>;

public sealed class SettleRecoveryValidator : AbstractValidator<SettleRecovery>
{
    public SettleRecoveryValidator() => RuleFor(c => c.Amount).GreaterThanOrEqualTo(0).PrecisionScale(18, 2, true);
}

public sealed class PrepareBordereauValidator : AbstractValidator<PrepareBordereau>
{
    public PrepareBordereauValidator()
    {
        RuleFor(c => c.LayoutCode).NotEmpty().MaximumLength(15);
        RuleFor(c => c.PeriodCode).Length(7);
    }
}

internal sealed class ReinsuranceQueries(IReinsuranceRepository repository) :
    IQueryHandler<ListCessions, IReadOnlyList<CessionView>>,
    IQueryHandler<GetRecovery, RecoveryView>,
    IQueryHandler<ListRecoveries, IReadOnlyList<RecoveryView>>,
    IQueryHandler<GetBordereau, BordereauView>
{
    public async Task<Result<IReadOnlyList<CessionView>>> Handle(ListCessions query, CancellationToken cancellationToken) =>
        (await repository.CessionsFor(Cession.SourceTypeOf(query.SourceDomain), query.PolicyRef, cancellationToken)).Select(CessionView.From).ToList();

    public async Task<Result<RecoveryView>> Handle(GetRecovery query, CancellationToken cancellationToken) =>
        await repository.Recovery(query.RecoveryId, cancellationToken) is { } r ? RecoveryView.From(r) : Error.NotFound(ErrorCodes.NotFound, "The recovery was not found.");

    public async Task<Result<IReadOnlyList<RecoveryView>>> Handle(ListRecoveries query, CancellationToken cancellationToken) =>
        (await repository.Recoveries(query.ClaimNumber, query.Status, query.AfterId, Math.Clamp(query.Limit, 1, 200), cancellationToken)).Select(RecoveryView.From).ToList();

    public async Task<Result<BordereauView>> Handle(GetBordereau query, CancellationToken cancellationToken) =>
        await repository.Bordereau(query.BordereauId, cancellationToken) is { } b ? BordereauView.From(b) : Error.NotFound(ErrorCodes.NotFound, "The bordereau was not found.");
}

internal sealed class RecoveryHandlers(IReinsuranceRepository repository, IReinsuranceCodes codes, IReinsurerAdapter reinsurers, ITenantContext tenant, IBusinessDate businessDate, IClock clock,
    IAuditTrail audit) :
    ICommandHandler<BillRecovery, RecoveryView>,
    ICommandHandler<SettleRecovery, RecoveryView>
{
    public async Task<Result<RecoveryView>> Handle(BillRecovery command, CancellationToken cancellationToken)
    {
        if (await repository.Recovery(command.RecoveryId, cancellationToken) is not { } recovery)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The recovery was not found.");
        }

        audit.Record(new AuditEntry("REINSURANCE", "BillRecovery", nameof(ReinsuranceRecovery), recovery.Id.ToString()));
        var billed = await Recoveries.Bill(recovery, codes, reinsurers, tenant, clock, businessDate, cancellationToken);
        return billed.IsFailure ? billed.Error : RecoveryView.From(recovery);
    }

    public async Task<Result<RecoveryView>> Handle(SettleRecovery command, CancellationToken cancellationToken)
    {
        if (await repository.Recovery(command.RecoveryId, cancellationToken) is not { } recovery)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The recovery was not found.");
        }

        audit.Record(new AuditEntry("REINSURANCE", "SettleRecovery", nameof(ReinsuranceRecovery), recovery.Id.ToString(),
            new Dictionary<string, string?> { ["amount"] = command.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        var settled = recovery.SettleRecovery(command.Amount, command.SettledOn, clock.UtcNow);
        return settled.IsFailure ? settled.Error : RecoveryView.From(recovery);
    }
}

internal sealed class BordereauHandlers(IReinsuranceRepository repository, IReinsuranceCodes codes, IReinsurerAdapter reinsurers, ITenantContext tenant, IBusinessDate businessDate, IClock clock,
    IAuditTrail audit) :
    ICommandHandler<PrepareBordereau, BordereauView>,
    ICommandHandler<SendBordereau, BordereauView>,
    ICommandHandler<AgreeBordereau, BordereauView>
{
    public async Task<Result<BordereauView>> Handle(PrepareBordereau command, CancellationToken cancellationToken)
    {
        if (await codes.Layout(command.LayoutCode, businessDate.Today, cancellationToken) is not { } layout)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"{command.LayoutCode} is not a bordereaux layout in force.");
        }

        var period = Bordereau.Period(command.PeriodCode, layout.Frequency);
        if (period.IsFailure)
        {
            return period.Error;
        }

        if (await repository.Bordereau(layout.TreatyCode, command.PeriodCode, cancellationToken) is not null)
        {
            return Error.Conflict(ReinsuranceErrorCodes.BordereauExists, $"Treaty {layout.TreatyCode} has a bordereau for {command.PeriodCode} already.");
        }

        var cessions = await repository.CessionsCalculated(layout.TreatyCode, period.Value.From, period.Value.To, cancellationToken);
        var bordereau = Bordereau.PrepareBordereau(Guid.CreateVersion7(), layout, command.PeriodCode, cessions);
        if (bordereau.IsFailure)
        {
            return bordereau.Error;
        }

        repository.Add(bordereau.Value);
        audit.Record(new AuditEntry("REINSURANCE", "PrepareBordereau", nameof(Bordereau), bordereau.Value.Id.ToString()));
        return BordereauView.From(bordereau.Value);
    }

    public Task<Result<BordereauView>> Handle(SendBordereau command, CancellationToken cancellationToken) =>
        Apply(command.BordereauId, "SendBordereau", async bordereau =>
        {
            var treaty = await codes.Treaty(bordereau.TreatyCode, businessDate.Today, cancellationToken);
            var sent = await reinsurers.SendBordereau(tenant.RequireTenantId(), treaty?.LeadReinsurer ?? bordereau.TreatyCode, bordereau.TreatyCode, bordereau.PeriodCode,
                bordereau.Content, cancellationToken);
            return sent.Accepted
                ? bordereau.SendBordereau(sent.PartnerRef, clock.UtcNow)
                : Error.Conflict(ErrorCodes.InvalidStateTransition, $"The reinsurer did not accept the bordereau: {sent.Error}");
        }, cancellationToken);

    public Task<Result<BordereauView>> Handle(AgreeBordereau command, CancellationToken cancellationToken) =>
        Apply(command.BordereauId, "AgreeBordereau", b => Task.FromResult(b.AgreeBordereau()), cancellationToken);

    private async Task<Result<BordereauView>> Apply(Guid id, string operation, Func<Bordereau, Task<Result>> change, CancellationToken cancellationToken)
    {
        if (await repository.Bordereau(id, cancellationToken) is not { } bordereau)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The bordereau was not found.");
        }

        audit.Record(new AuditEntry("REINSURANCE", operation, nameof(Bordereau), bordereau.Id.ToString()));
        var result = await change(bordereau);
        return result.IsFailure ? result.Error : BordereauView.From(bordereau);
    }
}

internal static class Recoveries
{
    /// <summary>BillRecovery through the reinsurer adapter (the treaty's lead reinsurer); only an accepted bill is billed.</summary>
    public static async Task<Result> Bill(ReinsuranceRecovery recovery, IReinsuranceCodes codes, IReinsurerAdapter reinsurers, ITenantContext tenant, IClock clock,
        IBusinessDate businessDate, CancellationToken cancellationToken)
    {
        if (recovery.Status != RecoveryStatus.Identified)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"The recovery on {recovery.ClaimNumber} is {recovery.Status}.");
        }

        var treaty = await codes.Treaty(recovery.TreatyCode, businessDate.Today, cancellationToken);
        var billed = await reinsurers.BillRecovery(tenant.RequireTenantId(), treaty?.LeadReinsurer ?? recovery.TreatyCode, recovery.TreatyCode, recovery.ClaimNumber,
            recovery.Recoverable, recovery.Currency, cancellationToken);
        return billed.Accepted
            ? recovery.BillRecovery(businessDate.Today, clock.UtcNow)
            : Error.Conflict(ErrorCodes.InvalidStateTransition, $"The reinsurer did not accept the recovery bill: {billed.Error}");
    }
}

/// <summary>
/// Reinsurance's policies (domain-model.json: reinsurance consumes; "General quote to cash": CalculateCession; "Life
/// proposal to policy": CalculateCession where cover exceeds retention; "General claim to settlement": IdentifyRecovery
/// and BillRecovery). A policy is ceded under the proportional treaties whose cession rules name its product, or under
/// a facultative arrangement for its policy number instead; an endorsement recalculates its cessions; a settled claim
/// recovers the ceded share of what it paid, and what an excess of loss treaty covers above its retention.
/// TODO(spec): Health claims are not ceded until a treaty names Health products; MedicalClaimSettled is not consumed (OQ-063).
/// </summary>
public sealed class ReinsurancePolicies(IReinsuranceRepository repository, IReinsuranceCodes codes, IReinsurerAdapter reinsurers, IPolicyArrears billing,
    IEnumerable<IPolicyDistribution> policies, ITenantContext tenant, IBusinessDate businessDate, IClock clock, ILogger<ReinsurancePolicies> logger) :
    IIntegrationEventHandler<GeneralPolicyIssued>,
    IIntegrationEventHandler<GeneralPolicyEndorsed>,
    IIntegrationEventHandler<LifePolicyIssued>,
    IIntegrationEventHandler<GeneralClaimSettled>,
    IIntegrationEventHandler<LifeClaimSettled>
{
    public Task Handle(GeneralPolicyIssued issued, EventEnvelope envelope, CancellationToken cancellationToken) =>
        issued.SumInsured is not { } sumInsured
            ? Task.CompletedTask
            : Cede(new PolicyRisk(GeneralRatingSubjects.SourceDomain, issued.PolicyId, issued.PolicyNumber, issued.ProductCode, sumInsured, issued.AnnualPremium,
                issued.Currency, issued.PeriodStart, issued.PeriodEnd), issued.PeriodStart, cancellationToken);

    /// <summary>RecalculateCession: each cession in effect is recalculated on the new sum insured and premium, under the arrangement in force on the effective date.</summary>
    public async Task Handle(GeneralPolicyEndorsed endorsed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (endorsed.NewSumInsured is not { } sumInsured)
        {
            return;
        }

        foreach (var current in await repository.CurrentCessions(Cession.SourceTypeOf(GeneralRatingSubjects.SourceDomain), endorsed.PolicyId, cancellationToken))
        {
            var risk = new PolicyRisk(GeneralRatingSubjects.SourceDomain, endorsed.PolicyId, endorsed.PolicyNumber, endorsed.ProductCode, sumInsured,
                endorsed.NewAnnualPremium, endorsed.Currency, current.PeriodStart, current.PeriodEnd);
            Result<Cession> recalculated;
            if (current.ArrangementCode is { } arrangementCode)
            {
                // A missing arrangement or rule is configuration: parked until it is published (ADR-022), never dead-lettered.
                recalculated = await codes.Facultative(endorsed.PolicyNumber, endorsed.EffectiveDate, cancellationToken) is { } arrangement
                               && arrangement.ArrangementCode == arrangementCode
                    ? Cession.CalculateFacultative(Guid.CreateVersion7(), risk, arrangement, current, clock.UtcNow)
                    : throw new PrerequisiteMissingException(PrerequisiteCodes.FacultativeArrangementNotInForce, arrangementCode);
            }
            else
            {
                var arrangement = (await codes.ArrangementsFor(endorsed.ProductCode, endorsed.EffectiveDate, cancellationToken))
                    .FirstOrDefault(a => a.Treaty.TreatyCode == current.TreatyCode);
                recalculated = arrangement.Rule is null
                    ? throw new PrerequisiteMissingException(PrerequisiteCodes.CessionRuleNotInForce, $"{endorsed.ProductCode}:{current.TreatyCode}")
                    : Cession.CalculateCession(Guid.CreateVersion7(), risk, arrangement.Treaty, arrangement.Rule, current, clock.UtcNow);
            }

            if (recalculated.IsFailure)
            {
                // As on issue: a treaty that cannot be applied is configuration, parked until it is fixed (ADR-022).
                throw new PrerequisiteMissingException(PrerequisiteCodes.TreatyNotApplicable, recalculated.Error.Code);
            }

            repository.Add(recalculated.Value);
        }
    }

    /// <summary>A Life policy's annual premium is its instalment × the frequency's periods per year (Billing).</summary>
    public async Task Handle(LifePolicyIssued issued, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var periods = await billing.PeriodsPerYear(tenant.RequireTenantId(), issued.BillingFrequency, issued.InceptionDate, cancellationToken)
                      ?? throw new PrerequisiteMissingException(PrerequisiteCodes.BillingFrequencyNotInForce, issued.BillingFrequency);
        await Cede(new PolicyRisk(LifeSubjects.SourceDomain, issued.PolicyId, issued.PolicyNumber, issued.ProductCode, issued.SumAssured, issued.Premium * periods,
            issued.Currency, issued.InceptionDate, issued.TermEndDate ?? issued.InceptionDate.AddYears(1).AddDays(-1)), issued.InceptionDate, cancellationToken);
    }

    public Task Handle(GeneralClaimSettled settled, EventEnvelope envelope, CancellationToken cancellationToken) =>
        settled.PolicyId is not { } policy
            ? Task.CompletedTask
            : Recover(new SettledClaim(GeneralRatingSubjects.SourceDomain, settled.ClaimId, settled.ClaimNumber, policy, settled.Amount, settled.Currency), settled.PaidOn,
                cancellationToken);

    public Task Handle(LifeClaimSettled settled, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Recover(new SettledClaim(LifeSubjects.SourceDomain, settled.ClaimId, settled.ClaimNumber, settled.PolicyId, settled.Amount, settled.Currency), settled.PaidOn,
            cancellationToken);

    /// <summary>CalculateCession under each proportional treaty in force on the start date, or the facultative arrangement for the policy.</summary>
    private async Task Cede(PolicyRisk risk, DateOnly day, CancellationToken cancellationToken)
    {
        if ((await repository.CessionsFor(Cession.SourceTypeOf(risk.SourceDomain), risk.PolicyRef, cancellationToken)).Count > 0)
        {
            return;
        }

        if (await codes.Facultative(risk.PolicyNumber, day, cancellationToken) is { } facultative)
        {
            Add(Cession.CalculateFacultative(Guid.CreateVersion7(), risk, facultative, null, clock.UtcNow), risk);
            return;
        }

        foreach (var (rule, treaty) in await codes.ArrangementsFor(risk.ProductCode, day, cancellationToken))
        {
            if (TreatyTypes.IsProportional(treaty.Type) && treaty.PeriodFrom <= day && day <= treaty.PeriodTo)
            {
                Add(Cession.CalculateCession(Guid.CreateVersion7(), risk, treaty, rule, null, clock.UtcNow), risk);
            }
        }
    }

    private void Add(Result<Cession> cession, PolicyRisk risk)
    {
        if (cession.IsSuccess)
        {
            repository.Add(cession.Value);
        }
        else if (cession.Error.Code != ReinsuranceErrorCodes.NothingCeded)
        {
            // A treaty that cannot be applied is a configuration error: parked until the treaty is fixed (ADR-022).
            throw new PrerequisiteMissingException(PrerequisiteCodes.TreatyNotApplicable, cession.Error.Code);
        }
    }

    /// <summary>IdentifyRecovery on each cession in effect and each excess of loss treaty for the product, then BillRecovery.</summary>
    private async Task Recover(SettledClaim claim, DateOnly paidOn, CancellationToken cancellationToken)
    {
        var found = new List<ReinsuranceRecovery>();
        foreach (var cession in await repository.CurrentCessions(Cession.SourceTypeOf(claim.SourceDomain), claim.PolicyRef, cancellationToken))
        {
            if (cession.CededShare > 0 && await repository.Recovery(claim.ClaimNumber, cession.TreatyCode, cancellationToken) is null)
            {
                Keep(ReinsuranceRecovery.Proportional(Guid.CreateVersion7(), claim, cession.Id, cession.TreatyCode, cession.CededShare), found);
            }
        }

        if (policies.FirstOrDefault(p => p.SourceDomain == claim.SourceDomain) is { } domain
            && await domain.For(tenant.RequireTenantId(), claim.PolicyRef, cancellationToken) is { } policy)
        {
            foreach (var (_, treaty) in await codes.ArrangementsFor(policy.ProductCode, paidOn, cancellationToken))
            {
                if (treaty.Type == TreatyTypes.ExcessOfLoss && await repository.Recovery(claim.ClaimNumber, treaty.TreatyCode, cancellationToken) is null)
                {
                    Keep(ReinsuranceRecovery.Excess(Guid.CreateVersion7(), claim, treaty), found);
                }
            }
        }

        foreach (var recovery in found)
        {
            repository.Add(recovery);
            var billed = await Recoveries.Bill(recovery, codes, reinsurers, tenant, clock, businessDate, cancellationToken);
            if (billed.IsFailure)
            {
                logger.LogWarning("Recovery on {ClaimNumber} under {TreatyCode} identified but not billed: {Code}", recovery.ClaimNumber, recovery.TreatyCode, billed.Error.Code);
            }
        }
    }

    private static void Keep(Result<ReinsuranceRecovery> recovery, List<ReinsuranceRecovery> found)
    {
        if (recovery.IsSuccess)
        {
            found.Add(recovery.Value);
        }
        else if (recovery.Error.Code != ReinsuranceErrorCodes.NothingRecoverable)
        {
            throw new InvalidOperationException(recovery.Error.Message);
        }
    }
}
