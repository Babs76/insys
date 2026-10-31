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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.Approvals;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Health.Application;
using Insys.Health.Contracts;
using Insys.Health.Domain.Adjudication;
using Insys.Health.Domain.Benefits;
using Insys.Health.Domain.Care;
using Insys.Health.Domain.Members;
using Insys.Health.Domain.Payments;
using Insys.Health.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Health.Infrastructure.Persistence;

/// <summary>
/// Health's records, each checked against the caller's scope for the endpoint's permission as it is loaded (F103,
/// addenda/access-scopes.md §4): outside it, a read finds nothing (404) and an action is refused (403 OUT_OF_SCOPE).
/// Memberships carry the product and node; preauthorisations, medical claims, utilisation cases and fraud cases follow
/// theirs. A membership on a plan with no product yet has none, and is outside every product scope (OQ-080). Schemes are
/// not scoped; provider payment runs are scoped by their claims (Runs). Jobs and consumers are unrestricted.
/// </summary>
internal sealed class HealthRepository(HealthDbContext db, IExecutionContext execution) : IHealthRepository
{
    public Task<HealthScheme?> Scheme(Guid id, CancellationToken cancellationToken) => db.Schemes.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<HealthMembership?> Membership(Guid id, CancellationToken cancellationToken) =>
        Scoped(await Memberships().SingleOrDefaultAsync(m => m.Id == id, cancellationToken));

    public async Task<HealthMembership?> MembershipByNumber(string memberNumber, CancellationToken cancellationToken) =>
        Scoped(await Memberships().SingleOrDefaultAsync(m => m.MemberNumber == memberNumber, cancellationToken));

    public async Task<IReadOnlyDictionary<Guid, MembershipScope>> MembershipScopes(IReadOnlyCollection<Guid> membershipIds, CancellationToken cancellationToken) =>
        await db.Memberships.AsNoTracking().Where(m => membershipIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => new MembershipScope(m.MemberNumber, m.ProductCode, m.OrgNodeCode), cancellationToken);

    public async Task<int> AssignPlanProducts(DateOnly day, Func<string, CancellationToken, Task<int?>> versionOnSale, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var plans = (await db.Set<PlansAndOptions>().AsNoTracking()
                .InForce(HealthStaticData.Descriptors.Single(d => d.EntityType == typeof(PlansAndOptions)).Lifecycle, day)
                .Where(p => p.ProductCode != null).Select(p => new { p.PlanCode, p.OptionCode, p.ProductCode, p.Version }).ToListAsync(cancellationToken))
            .GroupBy(p => (p.PlanCode, p.OptionCode)).Select(g => g.MaxBy(p => p.Version)!).ToList();
        // Through the aggregate, so each membership announces its product and Billing follows (MembershipProductAssigned).
        var assigned = 0;
        foreach (var plan in plans)
        {
            var version = await versionOnSale(plan.ProductCode!, cancellationToken);
            foreach (var membership in await db.Memberships.Where(m => m.ProductCode == null && m.PlanCode == plan.PlanCode && m.OptionCode == plan.OptionCode)
                         .ToListAsync(cancellationToken))
            {
                membership.UseProduct(plan.ProductCode, version, now);
                assigned++;
            }
        }

        return assigned;
    }

    public async Task LockBenefits(Guid membershipId, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM health.membership WHERE id = {membershipId} FOR UPDATE", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM health.benefit_accumulator WHERE membership_id = {membershipId} ORDER BY id FOR UPDATE",
            cancellationToken);
    }

    public async Task<IReadOnlyList<BenefitAccumulator>> Accumulators(Guid membershipId, IReadOnlyCollection<AccumulatorKey> keys, CancellationToken cancellationToken)
    {
        var categories = keys.Select(k => k.Category).Distinct().ToList();
        var stored = await db.Accumulators.Where(a => a.MembershipId == membershipId && categories.Contains(a.BenefitCategory)).ToListAsync(cancellationToken);
        var added = db.ChangeTracker.Entries<BenefitAccumulator>().Where(e => e.State == EntityState.Added && e.Entity.MembershipId == membershipId).Select(e => e.Entity);
        var wanted = keys.ToHashSet();
        return stored.Concat(added).Distinct().Where(a => wanted.Contains(a.Key)).ToList();
    }

    public async Task<IReadOnlyList<BenefitAccumulator>> AccumulatorsCovering(Guid membershipId, string category, DateOnly day, CancellationToken cancellationToken) =>
        await db.Accumulators.Where(a => a.MembershipId == membershipId && a.BenefitCategory == category && a.PeriodStart <= day && a.PeriodEnd >= day)
            .ToListAsync(cancellationToken);

    public async Task<string?> MembershipCurrency(Guid id, CancellationToken cancellationToken) =>
        await db.Memberships.AsNoTracking().Where(m => m.Id == id).Select(m => m.Currency).SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<BenefitAccumulator>> AccumulatorsOf(Guid membershipId, CancellationToken cancellationToken) =>
        await db.Accumulators.AsNoTracking().Where(a => a.MembershipId == membershipId).ToListAsync(cancellationToken);

    public async Task<Preauthorisation?> Preauthorisation(Guid id, CancellationToken cancellationToken)
    {
        var preauthorisation = await db.Preauthorisations.Include(p => p.Lines).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        return await OfMembership(preauthorisation, preauthorisation?.MembershipId, cancellationToken);
    }

    public async Task<IReadOnlyList<Preauthorisation>> MatchingPreauthorisations(Guid membershipId, Guid patientPartyId, string providerId, DateOnly serviceDate,
        CancellationToken cancellationToken) =>
        await db.Preauthorisations.Include(p => p.Lines)
            .Where(p => p.MembershipId == membershipId && p.PatientPartyId == patientPartyId && p.ProviderId == providerId
                        && (p.Status == PreauthStatus.Approved || p.Status == PreauthStatus.ConditionallyApproved)
                        && p.ValidFrom <= serviceDate && p.ValidTo >= serviceDate)
            .OrderBy(p => p.Id).ToListAsync(cancellationToken);

    public async Task<MedicalClaim?> Claim(Guid id, CancellationToken cancellationToken)
    {
        var claim = await db.Claims.Include(c => c.Lines).SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        return await OfMembership(claim, claim?.MembershipId, cancellationToken);
    }

    public async Task<IReadOnlyList<PriorService>> PriorServices(Guid patientPartyId, DateOnly from, Guid exceptClaimId, CancellationToken cancellationToken) =>
        (await db.Claims.AsNoTracking().Include(c => c.Lines)
            .Where(c => c.PatientPartyId == patientPartyId && c.ServiceDate >= from && c.Id != exceptClaimId
                        && c.Status != MedicalClaimStatus.Rejected && c.Status != MedicalClaimStatus.Reversed)
            .ToListAsync(cancellationToken))
        .SelectMany(c => c.Lines.Where(l => l.Outcome != "REJECT").Select(l => new PriorService(c.Id, l.TariffCode, l.BenefitCategory, c.ServiceDate, c.ProviderId)))
        .ToList();

    public async Task<IReadOnlyList<PriorMedicine>> PriorMedicines(Guid patientPartyId, DateOnly from, Guid exceptClaimId, CancellationToken cancellationToken) =>
        (await db.Claims.AsNoTracking().Include(c => c.Lines)
            .Where(c => c.PatientPartyId == patientPartyId && c.ServiceDate >= from && c.Id != exceptClaimId
                        && (c.Status == MedicalClaimStatus.Adjudicated || c.Status == MedicalClaimStatus.ApprovedForPayment || c.Status == MedicalClaimStatus.Paid))
            .ToListAsync(cancellationToken))
        .SelectMany(c => c.Lines.Where(l => l.IsPharmacy && l.Outcome == "PAY")
            .Select(l => new PriorMedicine(c.Id, l.TariffCode, l.ActiveIngredient, l.FormularyTier, c.ServiceDate, l.PaidQuantity ?? l.Quantity, l.DaysSupply, l.PaidAmount)))
        .ToList();

    public async Task<IReadOnlyList<MedicalClaim>> ClaimsForRun(DateTimeOffset cutOff, string currency, CancellationToken cancellationToken) =>
        await db.Claims.Where(c => c.Status == MedicalClaimStatus.ApprovedForPayment && c.PaymentRunId == null && c.ApprovedAt <= cutOff && c.Currency == currency)
            .OrderBy(c => c.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<MedicalClaim>> ClaimsInRun(Guid runId, CancellationToken cancellationToken) =>
        await db.Claims.Where(c => c.PaymentRunId == runId).OrderBy(c => c.Id).ToListAsync(cancellationToken);

    public async Task<UtilisationCase?> UtilisationCase(Guid id, CancellationToken cancellationToken)
    {
        var utilisationCase = await db.UtilisationCases.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        return await OfMembership(utilisationCase, utilisationCase?.MembershipId, cancellationToken);
    }

    /// <summary>A fraud case about a medical claim follows the claim's membership; one about anything else is not scoped.</summary>
    public async Task<HealthFraudCase?> FraudCase(Guid id, CancellationToken cancellationToken)
    {
        var fraudCase = await db.FraudCases.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (fraudCase is not { SubjectType: "CLAIM" })
        {
            return fraudCase;
        }

        var membership = await db.Claims.AsNoTracking().Where(c => c.ClaimNumber == fraudCase.SubjectRef).Select(c => (Guid?)c.MembershipId).FirstOrDefaultAsync(cancellationToken);
        return await OfMembership(fraudCase, membership, cancellationToken);
    }

    public Task<ProviderPaymentRun?> Run(Guid id, CancellationToken cancellationToken) =>
        db.Runs.Include(r => r.Remittances).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<ProviderPaymentRun?> RunForRemittance(Guid remittanceId, CancellationToken cancellationToken) =>
        db.Runs.Include(r => r.Remittances).SingleOrDefaultAsync(r => r.Remittances.Any(x => x.Id == remittanceId), cancellationToken);

    public async Task<IReadOnlyList<Guid>> PreauthorisationsToExpire(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.Preauthorisations.AsNoTracking().Where(p => (p.Status == PreauthStatus.Approved || p.Status == PreauthStatus.ConditionallyApproved) && p.ValidTo < day).OrderBy(p => p.Id).Select(p => p.Id).Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> MembershipsOnAccount(Guid billingAccountRef, CancellationToken cancellationToken) =>
        await db.Memberships.AsNoTracking().Where(m => m.BillingAccountRef == billingAccountRef && (m.Status == MembershipStatus.Active || m.Status == MembershipStatus.Suspended))
            .Select(m => m.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> MembershipsToSuspendForArrears(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.Memberships.AsNoTracking().Where(m => m.Status == MembershipStatus.Active && m.ArrearsSince != null && m.SuspendOn <= day)
            .OrderBy(m => m.Id).Select(m => m.Id).Take(limit).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> MembershipsTerminationDue(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.Memberships.AsNoTracking().Where(m => m.Status == MembershipStatus.Suspended && m.SuspendedForArrearsOn != null && m.TerminationNoticeEnds < day
                                                       && !m.TerminationDueRaised)
            .OrderBy(m => m.Id).Select(m => m.Id).Take(limit).ToListAsync(cancellationToken);

    public async Task<(IReadOnlyList<Guid> Release, IReadOnlyList<Guid> Decline)> HeldClaims(int limit, CancellationToken cancellationToken)
    {
        var held = await (from c in db.Claims.AsNoTracking()
                          join m in db.Memberships.AsNoTracking() on c.MembershipId equals m.Id
                          where c.Status == MedicalClaimStatus.Pended && c.PendReason == AdjudicationEngine.HeldForArrearsReason
                          orderby c.Id
                          select new { c.Id, c.ServiceDate, m.ArrearsSince, m.SuspendedForArrearsOn }).Take(limit).ToListAsync(cancellationToken);
        return (held.Where(h => h.ArrearsSince is null || h.ServiceDate < h.ArrearsSince).Select(h => h.Id).ToList(),
            held.Where(h => h.SuspendedForArrearsOn is not null && h.ArrearsSince is not null && h.ServiceDate >= h.ArrearsSince).Select(h => h.Id).ToList());
    }

    public async Task<IReadOnlyList<Guid>> MembershipsToActivate(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.Memberships.AsNoTracking().Where(m => m.Status == MembershipStatus.Pending && m.StartDate <= day).OrderBy(m => m.Id).Select(m => m.Id).Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> SchemesToNotice(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.Schemes.AsNoTracking().Where(s => s.Status == SchemeStatus.Active && s.RenewalDate <= day.AddYears(1)
                                                   && (s.RenewalNoticeSentFor == null || s.RenewalNoticeSentFor != s.RenewalDate))
            .OrderBy(s => s.Id).Select(s => s.Id).Take(limit).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> SchemesToActivate(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.Schemes.AsNoTracking().Where(s => s.Status == SchemeStatus.Registered && s.StartDate <= day).OrderBy(s => s.Id).Select(s => s.Id).Take(limit)
            .ToListAsync(cancellationToken);

    public void Add(HealthScheme scheme) => db.Schemes.Add(scheme);

    public void Add(HealthMembership membership) => db.Memberships.Add(membership);

    public void Add(BenefitAccumulator accumulator) => db.Accumulators.Add(accumulator);

    public void Add(EligibilityDecision decision) => db.EligibilityDecisions.Add(decision);

    public void Add(Preauthorisation preauthorisation) => db.Preauthorisations.Add(preauthorisation);

    public void Add(MedicalClaim claim) => db.Claims.Add(claim);

    public async Task<IReadOnlyList<string>> ProvidersToMeasure(string monthKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        await db.Claims.AsNoTracking().Where(c => c.ProviderId != null && c.SubmittedAt >= from && c.SubmittedAt < to)
            .Select(c => c.ProviderId!).Distinct()
            .Where(p => !db.ProviderMeasureResults.Any(r => r.ProviderId == p && r.MonthKey == monthKey)).OrderBy(p => p).Take(500).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ProviderClaimFact>> ProviderClaimFacts(string providerId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        await db.Claims.AsNoTracking().Where(c => c.ProviderId == providerId && c.SubmittedAt >= from && c.SubmittedAt < to)
            .Select(c => new ProviderClaimFact(c.ProviderId!, c.Status, c.TotalPaid, c.SubmittedAt, c.AdjudicatedAt, c.Lines.Count,
                c.Lines.Count(l => l.AdjustmentReason == Domain.Adjudication.AdjudicationReasons.RepeatService))).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Domain.Providers.ProviderMeasureResult>> ProviderMeasures(string providerId, string? monthKey, Guid? afterId, int limit, CancellationToken cancellationToken) =>
        await db.ProviderMeasureResults.AsNoTracking().Where(r => r.ProviderId == providerId && (monthKey == null || r.MonthKey == monthKey)
                && (afterId == null || r.Id.CompareTo(afterId.Value) > 0)).OrderBy(r => r.Id).Take(limit).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Domain.Providers.ProviderQualityReview>> ProviderQualityReviews(string providerId, Guid? afterId, int limit, CancellationToken cancellationToken) =>
        await db.ProviderQualityReviews.AsNoTracking().Where(r => r.ProviderId == providerId && (afterId == null || r.Id.CompareTo(afterId.Value) > 0))
            .OrderBy(r => r.Id).Take(limit).ToListAsync(cancellationToken);

    public Task<bool> ProviderStatementExists(string providerId, string statementRef, CancellationToken cancellationToken) =>
        db.ProviderStatements.AnyAsync(s => s.ProviderId == providerId && s.StatementRef == statementRef, cancellationToken);

    public async Task<IReadOnlyList<(string ClaimNumber, decimal Paid, bool InPeriod)>> ProviderPaidClaims(string providerId, DateOnly from, DateOnly to, IReadOnlyCollection<string> references,
        CancellationToken cancellationToken)
    {
        var numbers = references.ToList();
        var rows = await db.Claims.AsNoTracking()
            .Where(c => c.ProviderId == providerId && c.Status == MedicalClaimStatus.Paid && ((c.ServiceDate >= from && c.ServiceDate <= to) || numbers.Contains(c.ClaimNumber)))
            .Select(c => new { c.ClaimNumber, c.TotalPaid, c.ServiceDate }).ToListAsync(cancellationToken);
        return rows.Select(c => (c.ClaimNumber, c.TotalPaid ?? 0, c.ServiceDate >= from && c.ServiceDate <= to)).ToList();
    }

    public async Task<IReadOnlyList<(string ClaimNumber, decimal Paid, DateTimeOffset ReversedAt)>> ProviderReversedClaims(string providerId, IReadOnlyCollection<string> references,
        CancellationToken cancellationToken)
    {
        var numbers = references.ToList();
        var rows = await db.Claims.AsNoTracking().Where(c => c.ProviderId == providerId && c.Status == MedicalClaimStatus.Reversed && numbers.Contains(c.ClaimNumber))
            .Select(c => new { c.ClaimNumber, c.TotalPaid, c.ReversedAt }).ToListAsync(cancellationToken);
        return rows.Select(c => (c.ClaimNumber, c.TotalPaid ?? 0, c.ReversedAt!.Value)).ToList();
    }

    public Task<Domain.Providers.ProviderStatement?> ProviderStatement(Guid id, CancellationToken cancellationToken) =>
        db.ProviderStatements.Include(s => s.Lines).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ProviderStatementSummary>> ProviderStatements(string? providerId, string? status, Guid? afterId, int limit, CancellationToken cancellationToken) =>
        await db.ProviderStatements.AsNoTracking().Where(s => (providerId == null || s.ProviderId == providerId) && (status == null || s.Status == status)
                && (afterId == null || s.Id.CompareTo(afterId.Value) > 0)).OrderBy(s => s.Id).Take(limit)
            .Select(s => new ProviderStatementSummary(s.Id, s.ProviderId, s.StatementRef, s.PeriodFrom, s.PeriodTo, s.Status)).ToListAsync(cancellationToken);

    private IQueryable<Guid> MembershipsInScope => db.Memberships.AsNoTracking().InScope(execution.CurrentScope, m => m.ProductCode, m => m.OrgNodeCode).Select(m => m.Id);

    private IQueryable<ComplexCase> ComplexCaseGraph => db.ComplexCases.Include(c => c.Assignments).Include(c => c.Notes).Include(c => c.Plans).ThenInclude(p => p.Items).AsSplitQuery();

    public async Task<ComplexCase?> ComplexCase(Guid id, CancellationToken cancellationToken)
    {
        var complex = await ComplexCaseGraph.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        return await OfMembership(complex, complex?.MembershipId, cancellationToken);
    }

    public async Task<IReadOnlyList<ComplexCase>> ComplexCases(Guid? membershipId, Guid? caseManagerId, string? status, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var rows = db.ComplexCases.AsNoTracking().Include(c => c.Assignments).Include(c => c.Plans).AsSplitQuery();
        if (!execution.CurrentScope.IsUnrestricted)
        {
            rows = rows.Where(c => MembershipsInScope.Contains(c.MembershipId));
        }

        return await rows.Where(c => (membershipId == null || c.MembershipId == membershipId) && (caseManagerId == null || c.CaseManagerId == caseManagerId)
                && (status == null || c.Status == status) && (afterId == null || c.Id.CompareTo(afterId.Value) > 0)).OrderBy(c => c.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> CasesWithOverdueActivities(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.ComplexCases.AsNoTracking().Where(c => c.Status == ComplexCaseStatus.Active && c.Plans.Any(p => p.IsCurrent && p.Items.Any(i =>
                i.Kind == CarePlanItemKind.Activity && i.Status == CarePlanItemStatus.Open && !i.OverdueRaised && i.DueOn < day)))
            .OrderBy(c => c.Id).Select(c => c.Id).Take(limit).ToListAsync(cancellationToken);

    public async Task<ClinicalReferral?> ClinicalReferral(Guid id, CancellationToken cancellationToken)
    {
        var referral = await db.ClinicalReferrals.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        return await OfMembership(referral, referral?.MembershipId, cancellationToken);
    }

    public async Task<ChronicRegistration?> ChronicRegistration(Guid id, CancellationToken cancellationToken)
    {
        var registration = await db.ChronicRegistrations.Include(r => r.Entitlements).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        return await OfMembership(registration, registration?.MembershipId, cancellationToken);
    }

    public async Task<IReadOnlyList<ChronicRegistration>> ChronicRegistrationsInForce(Guid patientPartyId, DateOnly day, CancellationToken cancellationToken) =>
        await db.ChronicRegistrations.AsNoTracking().Include(r => r.Entitlements)
            .Where(r => r.PatientPartyId == patientPartyId && r.Status == ChronicStatus.Approved && r.ValidFrom <= day && (r.ValidTo == null || day <= r.ValidTo))
            .OrderBy(r => r.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ChronicRegistration>> ChronicRegistrations(Guid? membershipId, string? status, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var rows = db.ChronicRegistrations.AsNoTracking();
        if (!execution.CurrentScope.IsUnrestricted)
        {
            rows = rows.Where(r => MembershipsInScope.Contains(r.MembershipId));
        }

        return await rows.Where(r => (membershipId == null || r.MembershipId == membershipId) && (status == null || r.Status == status) && (afterId == null || r.Id.CompareTo(afterId.Value) > 0))
            .OrderBy(r => r.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChronicRegistration>> MemberChronicRegistrations(Guid partyId, CancellationToken cancellationToken) =>
        await db.ChronicRegistrations.AsNoTracking().Include(r => r.Entitlements)
            .Where(r => r.PatientPartyId == partyId || db.Memberships.Any(m => m.Id == r.MembershipId && m.PrincipalPartyId == partyId)).OrderBy(r => r.Id).ToListAsync(cancellationToken);

    public void Add(ChronicRegistration registration) => db.ChronicRegistrations.Add(registration);

    public async Task<ProgrammeEnrolment?> ProgrammeEnrolment(Guid id, CancellationToken cancellationToken)
    {
        var enrolment = await db.ProgrammeEnrolments.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        return await OfMembership(enrolment, enrolment?.MembershipId, cancellationToken);
    }

    public async Task<IReadOnlyList<ProgrammeEnrolment>> ProgrammeEnrolmentsInForce(Guid patientPartyId, DateOnly day, CancellationToken cancellationToken) =>
        await db.ProgrammeEnrolments.AsNoTracking().Where(e => e.PatientPartyId == patientPartyId && e.EnrolledOn <= day && (e.EndedOn == null || day <= e.EndedOn)).OrderBy(e => e.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ProgrammeEnrolment>> ProgrammeEnrolments(Guid? membershipId, string? status, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var rows = db.ProgrammeEnrolments.AsNoTracking();
        if (!execution.CurrentScope.IsUnrestricted)
        {
            rows = rows.Where(e => MembershipsInScope.Contains(e.MembershipId));
        }

        return await rows.Where(e => (membershipId == null || e.MembershipId == membershipId) && (status == null || e.Status == status) && (afterId == null || e.Id.CompareTo(afterId.Value) > 0))
            .OrderBy(e => e.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ProgrammeReviewsDue(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.ProgrammeEnrolments.AsNoTracking().Where(e => e.Status == ProgrammeStatus.Active && e.NextReviewOn <= day && (e.ReviewRaisedFor == null || e.ReviewRaisedFor != e.NextReviewOn))
            .OrderBy(e => e.Id).Select(e => e.Id).Take(limit).ToListAsync(cancellationToken);

    public void Add(ProgrammeEnrolment enrolment) => db.ProgrammeEnrolments.Add(enrolment);

    public async Task<IReadOnlyList<ClinicalReferral>> ClinicalReferrals(Guid? membershipId, string? providerId, string? status, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var rows = db.ClinicalReferrals.AsNoTracking();
        if (!execution.CurrentScope.IsUnrestricted)
        {
            rows = rows.Where(r => MembershipsInScope.Contains(r.MembershipId));
        }

        return await rows.Where(r => (membershipId == null || r.MembershipId == membershipId) && (providerId == null || r.ReferringProviderId == providerId || r.ReferredProviderId == providerId)
                && (status == null || r.Status == status) && (afterId == null || r.Id.CompareTo(afterId.Value) > 0)).OrderBy(r => r.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ReferralsToExpire(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.ClinicalReferrals.AsNoTracking().Where(r => (r.Status == ReferralStatus.Requested || r.Status == ReferralStatus.Approved) && r.ValidTo < day)
            .OrderBy(r => r.Id).Select(r => r.Id).Take(limit).ToListAsync(cancellationToken);

    public void Add(ComplexCase complexCase) => db.ComplexCases.Add(complexCase);

    public void Add(ClinicalReferral referral) => db.ClinicalReferrals.Add(referral);

    public Task<ProviderRecovery?> Recovery(Guid id, CancellationToken cancellationToken) =>
        db.ProviderRecoveries.Include(r => r.Offsets).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<ProviderRecovery?> RecoveryForSource(string source, Guid sourceId, CancellationToken cancellationToken) =>
        db.ProviderRecoveries.Include(r => r.Offsets).SingleOrDefaultAsync(r => r.Source == source && r.SourceId == sourceId, cancellationToken);

    public async Task<IReadOnlyList<ProviderRecovery>> Recoveries(string? providerId, string? status, Guid? afterId, int limit, CancellationToken cancellationToken) =>
        await db.ProviderRecoveries.AsNoTracking().Include(r => r.Offsets)
            .Where(r => (providerId == null || r.ProviderId == providerId) && (status == null || r.Status == status) && (afterId == null || r.Id.CompareTo(afterId.Value) > 0))
            .OrderBy(r => r.Id).Take(limit).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ProviderRecovery>> OffsettableRecoveries(string providerId, string currency, CancellationToken cancellationToken) =>
        await db.ProviderRecoveries.Include(r => r.Offsets)
            .Where(r => r.ProviderId == providerId && r.Currency == currency && r.Status == RecoveryStatuses.Open && r.Basis == RecoveryBases.Offset && r.HeldByCaseId == null
                && r.WriteOffRequestedBy == null && r.Balance > 0)
            .OrderBy(r => r.RaisedOn).ThenBy(r => r.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ProviderRecovery>> RecoveriesToRepay(string currency, CancellationToken cancellationToken) =>
        await db.ProviderRecoveries.Include(r => r.Offsets).Where(r => r.Currency == currency && r.RepayAmount > 0 && r.RepaidInRunId == null).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ProviderRecovery>> RecoveriesInRun(Guid runId, CancellationToken cancellationToken) =>
        await db.ProviderRecoveries.Include(r => r.Offsets).Where(r => r.RepaidInRunId == runId || r.Offsets.Any(o => o.RunId == runId)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ProviderRecovery>> RecoveriesOnRemittance(Guid remittanceId, CancellationToken cancellationToken) =>
        await db.ProviderRecoveries.Include(r => r.Offsets).Where(r => r.Offsets.Any(o => o.RemittanceId == remittanceId)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ProviderRecovery>> RecoveriesOfClaim(Guid claimId, CancellationToken cancellationToken) =>
        await db.ProviderRecoveries.Include(r => r.Offsets).Where(r => r.ClaimId == claimId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ProviderRecovery>> RecoveriesHeldBy(Guid caseId, CancellationToken cancellationToken) =>
        await db.ProviderRecoveries.Include(r => r.Offsets).Where(r => r.HeldByCaseId == caseId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> RecoveriesDueForReview(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.ProviderRecoveries.AsNoTracking().Where(r => r.Status == RecoveryStatuses.Open && !r.InReview && r.HeldByCaseId == null && r.ReviewDueOn != null && r.ReviewDueOn <= day)
            .OrderBy(r => r.ReviewDueOn).Select(r => r.Id).Take(limit).ToListAsync(cancellationToken);

    public Task<ProviderCreditNote?> CreditNote(Guid id, CancellationToken cancellationToken) => db.ProviderCreditNotes.SingleOrDefaultAsync(n => n.Id == id, cancellationToken);

    public Task<bool> CreditNoteExists(string providerId, string reference, CancellationToken cancellationToken) =>
        db.ProviderCreditNotes.AnyAsync(n => n.ProviderId == providerId && n.ProviderReference == reference, cancellationToken);

    public async Task<IReadOnlyList<ProviderCreditNote>> CreditNotes(string? providerId, string? status, Guid? afterId, int limit, CancellationToken cancellationToken) =>
        await db.ProviderCreditNotes.AsNoTracking().Where(n => (providerId == null || n.ProviderId == providerId) && (status == null || n.Status == status) && (afterId == null || n.Id.CompareTo(afterId.Value) > 0))
            .OrderBy(n => n.Id).Take(limit).ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<string, string>> RecoveryStatusesByClaim(string providerId, CancellationToken cancellationToken)
    {
        var recoveries = await db.ProviderRecoveries.AsNoTracking().Include(r => r.Offsets).Where(r => r.ProviderId == providerId && r.ClaimNumber != null && r.Source == RecoverySources.Reversal).ToListAsync(cancellationToken);
        return recoveries.ToDictionary(r => r.ClaimNumber!, r => r.Status switch
        {
            RecoveryStatuses.Invoiced => Domain.Providers.ReversalRecovery.Invoiced,
            RecoveryStatuses.Recovered => Domain.Providers.ReversalRecovery.Recovered,
            RecoveryStatuses.Open when r.Offsets.Any(o => o.Status != OffsetStatuses.Released) => Domain.Providers.ReversalRecovery.Offset,
            _ => Domain.Providers.ReversalRecovery.Owed,
        }, StringComparer.Ordinal);
    }

    public Task<Preauthorisation?> PreauthorisationWithLine(Guid lineId, CancellationToken cancellationToken) =>
        db.Preauthorisations.Include(p => p.Lines).SingleOrDefaultAsync(p => p.Lines.Any(l => l.Id == lineId), cancellationToken);

    public void Add(ProviderRecovery recovery) => db.ProviderRecoveries.Add(recovery);

    public void Add(ProviderCreditNote note) => db.ProviderCreditNotes.Add(note);

    public void Add(Domain.Providers.ProviderMeasureResult result) => db.ProviderMeasureResults.Add(result);

    public void Add(Domain.Providers.ProviderQualityReview review) => db.ProviderQualityReviews.Add(review);

    public void Add(Domain.Providers.ProviderStatement statement) => db.ProviderStatements.Add(statement);

    public void Add(UtilisationCase utilisationCase) => db.UtilisationCases.Add(utilisationCase);

    public void Add(HealthFraudCase fraudCase) => db.FraudCases.Add(fraudCase);

    public void Add(ProviderPaymentRun run) => db.Runs.Add(run);

    private HealthMembership? Scoped(HealthMembership? membership) => execution.InScope(membership, membership?.ProductCode, membership?.OrgNodeCode);

    private async Task<T?> OfMembership<T>(T? record, Guid? membershipId, CancellationToken cancellationToken)
        where T : class
    {
        if (record is null || membershipId is not { } id || execution.CurrentScope.IsUnrestricted)
        {
            return record;
        }

        var owner = await db.Memberships.AsNoTracking().Where(m => m.Id == id).Select(m => new { m.ProductCode, m.OrgNodeCode }).SingleOrDefaultAsync(cancellationToken);
        return execution.InScope(record, owner?.ProductCode, owner?.OrgNodeCode);
    }

    private IQueryable<HealthMembership> Memberships() => db.Memberships.Include(m => m.Dependants).Include(m => m.Events).AsSplitQuery();
}

internal sealed class HealthCodes(HealthDbContext db) : IHealthCodes
{
    public async Task<RecoverySettings?> RecoverySettings(DateOnly day, CancellationToken cancellationToken) =>
        await InForce<StaticData.ProviderRecoverySettings>(day).OrderBy(s => s.Code).Select(s => new RecoverySettings(s.MedicalScheme, s.ReviewAfterDays, s.InvoiceCollectionMethod)).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<bool?>> ProviderOffsetFlags(string providerId, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<ProviderContracts>(day).Where(c => c.Provider == providerId).Select(c => c.OffsetAllowed).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Domain.Providers.ProviderMeasure>> ProviderMeasures(DateOnly day, CancellationToken cancellationToken) =>
        await InForce<StaticData.ProviderMeasures>(day).Select(m => new Domain.Providers.ProviderMeasure(m.MeasureCode, m.ProviderType, m.Threshold, m.Direction, m.MinClaims))
            .ToListAsync(cancellationToken);

    public async Task<string?> ProviderType(string providerId, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<ProviderRegister>(day).Where(p => p.ProviderId == providerId).OrderByDescending(p => p.Version).Select(p => p.ProviderType).FirstOrDefaultAsync(cancellationToken);

    public async Task<(int NoticeDays, string ReRatingBasis)?> SchemeRenewalRule(string schemeNumber, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<SchemeRenewalRules>(day).Where(r => r.Scheme == schemeNumber).Select(r => new { r.NoticeDays, r.ReRatingBasis }).FirstOrDefaultAsync(cancellationToken)
            is { } rule ? (rule.NoticeDays, rule.ReRatingBasis) : null;

    public async Task<ContributionGrace?> ContributionGrace(string? schemeNumber, DateOnly day, CancellationToken cancellationToken)
    {
        var rules = await InForce<ContributionGraceRules>(day).Where(r => r.Scheme == null || r.Scheme == schemeNumber).ToListAsync(cancellationToken);
        return rules.OrderBy(r => r.Scheme is null).Select(r => new ContributionGrace(r.GraceDays, r.SuspendAfterDays, r.TerminateNoticeDays, r.ReinstateOnPayment ?? true)).FirstOrDefault();
    }

    public async Task<string?> MemberNumberPrefix(DateOnly day, CancellationToken cancellationToken) =>
        await InForce<MemberNumberingSchemes>(day).OrderBy(s => s.Code).Select(s => s.Prefix).FirstOrDefaultAsync(cancellationToken);

    public async Task<PlanDefinition?> Plan(string planCode, string optionCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<PlansAndOptions>(day).Where(p => p.PlanCode == planCode && p.OptionCode == optionCode).OrderByDescending(p => p.Version)
            .Select(p => new PlanDefinition(new PlanTerms(p.PlanCode, p.OptionCode, p.BenefitSchedule, p.Network, p.ReferenceTariffScheduleCode, p.BenefitYear, p.MemberClaimBasis), p.Version,
                p.ProductCode))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<SchemeCategory?> SchemeCategory(string categoryCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<SchemeMemberCategories>(day).Where(c => c.CategoryCode == categoryCode).Select(c => new SchemeCategory(c.CategoryCode, c.Plan, c.EligibilityRule))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<ContributionRule?> Contribution(string schemeNumber, string categoryCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<ContributionRules>(day).Where(r => r.Scheme == schemeNumber && r.MemberCategory == categoryCode)
            .Select(r => new ContributionRule(r.Rate, r.EmployerShare)).FirstOrDefaultAsync(cancellationToken);

    public Task<bool> ServiceTypeInForce(string serviceType, DateOnly day, CancellationToken cancellationToken) =>
        InForce<ServiceTypes>(day).AnyAsync(t => t.ServiceType == serviceType, cancellationToken);

    public async Task<string?> EligibilityResponseCode(string outcome, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<EligibilityResponseCodes>(day).Where(c => c.Outcome == outcome).OrderBy(c => c.TenantId == null).ThenBy(c => c.Code).Select(c => c.Code)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<string?> RemittanceTemplate(DateOnly day, CancellationToken cancellationToken) =>
        await InForce<RemittanceAdviceFormats>(day).OrderBy(f => f.SortOrder ?? int.MaxValue).ThenBy(f => f.FormatCode).Select(f => f.Template).FirstOrDefaultAsync(cancellationToken);

    public async Task<Guid?> SponsorParty(string sponsorCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<SponsorRegister>(day).Where(s => s.SponsorCode == sponsorCode).Select(s => s.Party).FirstOrDefaultAsync(cancellationToken) is { } party
        && Guid.TryParse(party, out var id) ? id : null;

    public Task<bool> SponsorExists(string sponsorCode, DateOnly day, CancellationToken cancellationToken) =>
        InForce<SponsorRegister>(day).AnyAsync(s => s.SponsorCode == sponsorCode, cancellationToken);

    public async Task<IReadOnlyList<WaitingPeriodRule>> WaitingPeriodRules(DateOnly day, CancellationToken cancellationToken) =>
        (await InForce<HealthWaitingPeriodRules>(day).ToListAsync(cancellationToken))
        .Select(r => new WaitingPeriodRule(r.RuleCode, r.Type, r.Duration, r.Exceptions ?? [], r.DiagnosisCodes ?? [], r.ProcedureCodes ?? [])).ToList();

    public async Task<DependantRule?> DependantType(string typeCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<DependantTypesAndAgeLimits>(day).Where(t => t.TypeCode == typeCode).Select(t => new DependantRule(t.TypeCode, t.MaxAge, t.StudentExtensionAge))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<int?> ValidityDays(string serviceType, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<AuthorisationValidityPeriods>(day).Where(v => v.ServiceType == serviceType).Select(v => (int?)v.DaysValid).FirstOrDefaultAsync(cancellationToken);

    public async Task<ProviderInfo?> Provider(string providerId, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<ProviderRegister>(day).Where(p => p.ProviderId == providerId).Select(p => new ProviderInfo(p.ProviderId, p.ProviderType, p.Status, p.PartyId))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<BenefitRules> Rules(PlanTerms plan, DateOnly day, CancellationToken cancellationToken)
    {
        var schedule = (await InForce<BenefitSchedules>(day).Where(s => s.ScheduleCode == plan.BenefitScheduleCode).ToListAsync(cancellationToken))
            .Select(s => new ScheduleLimit(s.Benefit, s.LimitValue, s.Frequency)).ToList();
        var limits = (await InForce<LimitDefinitions>(day).ToListAsync(cancellationToken))
            .Select(l => new LimitDefinition(l.Benefit, l.Period, l.Amount, l.Basis, l.PlanCode)).ToList();
        var costShares = (await InForce<CoPaymentAndDeductibleRules>(day).ToListAsync(cancellationToken))
            .Select(r => new CostShareRule(r.RuleKind, r.Benefit, r.Type, r.Value, r.NetworkStatus, r.Cap, r.CapBasis, r.PlanCode)).ToList();
        var mappings = (await InForce<BenefitCategoryMappings>(day).ToListAsync(cancellationToken))
            .Select(m => new CategoryMapping(m.CodeSystem, m.CodeFrom, m.CodeTo, m.ProviderType, m.BenefitCategory)).ToList();
        var modifiers = (await InForce<TariffModifiers>(day).ToListAsync(cancellationToken))
            .Select(m => new ModifierRule(m.ModifierCode, m.Type, m.Value, m.AppliesTo, m.SortOrder ?? 0)).ToList();
        var preauth = (await InForce<PreauthorisationRequiredLists>(day).Where(p => p.Plan == plan.PlanCode).ToListAsync(cancellationToken))
            .Select(p => new PreauthRequirement(p.Service, p.Threshold)).ToList();
        var repeats = (await InForce<RepeatServiceIntervals>(day).ToListAsync(cancellationToken)).Select(r => new RepeatRule(r.Service, r.MinInterval)).ToList();
        var highCost = (await InForce<HighCostThresholds>(day).ToListAsync(cancellationToken)).Select(h => new HighCostRule(h.Code, h.Service, h.Amount)).ToList();
        var edits = (await InForce<AdjudicationEditRules>(day).ToListAsync(cancellationToken)).Select(e => (e.EditCode, e.Condition, e.Outcome, e.ReasonCode, e.WindowDays)).ToList();
        var treatment = await InForce<NetworkRestrictionRules>(day).Where(r => r.Plan == plan.PlanCode && r.Network == plan.NetworkCode).Select(r => r.OutOfNetworkTreatment)
            .FirstOrDefaultAsync(cancellationToken);
        return new BenefitRules(schedule, limits, costShares, mappings, modifiers, preauth, repeats, highCost, edits, treatment);
    }

    public async Task<ProviderTerms> Network(PlanTerms plan, ProviderInfo? provider, DateOnly day, CancellationToken cancellationToken)
    {
        var network = await InForce<NetworkDefinitions>(day).Where(n => n.NetworkCode == plan.NetworkCode).FirstOrDefaultAsync(cancellationToken);
        if (provider is null)
        {
            return new ProviderTerms(null, null, false, null, network?.TariffScheduleCode);
        }

        var contract = await InForce<ProviderContracts>(day).Where(c => c.Provider == provider.ProviderId && c.Network == plan.NetworkCode).FirstOrDefaultAsync(cancellationToken);
        var inNetwork = contract is not null || (network?.Providers.Contains(provider.ProviderId) ?? false);
        return new ProviderTerms(provider.ProviderId, provider.ProviderType, inNetwork, contract?.TariffScheduleCode, network?.TariffScheduleCode);
    }

    public async Task<IReadOnlyDictionary<(string Schedule, string TariffCode), TariffPrice>> Tariffs(IReadOnlyCollection<string> schedules,
        IReadOnlyCollection<string> tariffCodes, DateOnly day, CancellationToken cancellationToken) =>
        (await InForce<TariffSchedules>(day).Where(t => t.ScheduleCode != null && schedules.Contains(t.ScheduleCode) && tariffCodes.Contains(t.TariffCode))
            .ToListAsync(cancellationToken))
        .GroupBy(t => (t.ScheduleCode!, t.TariffCode)).ToDictionary(g => g.Key, g => g.Select(t => new TariffPrice(t.Price, t.Modifiers)).First());

    public async Task<IReadOnlyDictionary<string, string>> CodeSystems(IReadOnlyCollection<string> codes, DateOnly day, CancellationToken cancellationToken) =>
        (await InForce<ProcedureAndServiceCodes>(day).Where(c => codes.Contains(c.Code) && c.Active).ToListAsync(cancellationToken))
        .GroupBy(c => c.Code).ToDictionary(g => g.Key, g => g.First().CodeSystem);

    public async Task<IReadOnlyCollection<string>> KnownDiagnoses(IReadOnlyCollection<string> codes, DateOnly day, CancellationToken cancellationToken) =>
        (await InForce<DiagnosisCodes>(day).Where(c => codes.Contains(c.Code) && c.Active).Select(c => c.Code).ToListAsync(cancellationToken)).Distinct().ToList();

    public Task<bool> ClaimFormatExists(string formatCode, DateOnly day, CancellationToken cancellationToken) =>
        InForce<ClaimFormatDefinitions>(day).AnyAsync(f => f.FormatCode == formatCode, cancellationToken);

    public Task<bool> RunCalendarExists(string runCode, DateOnly day, CancellationToken cancellationToken) =>
        InForce<PaymentRunCalendars>(day).AnyAsync(c => c.RunCode == runCode, cancellationToken);

    public async Task<ClaimFormatTerms?> ClaimFormat(string formatCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<ClaimFormatDefinitions>(day).Where(f => f.FormatCode == formatCode).Select(f => new ClaimFormatTerms(f.LineKind, f.RequiredLineFields)).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<string, FormularyItem>> Formulary(IReadOnlyCollection<string> productCodes, DateOnly day, CancellationToken cancellationToken) =>
        (await InForce<MedicineFormulary>(day).Where(f => productCodes.Contains(f.ProductCode)).OrderBy(f => f.TenantId == null).ToListAsync(cancellationToken))
        .GroupBy(f => f.ProductCode).ToDictionary(g => g.Key, g => g.Select(f => new FormularyItem(f.ProductCode, f.ActiveIngredient, f.Tier, f.ReferencePrice, f.BenefitCategory,
            f.PriorAuthRequired ?? false, f.MaxQuantity, f.MaxDaysSupply)).First());

    public async Task<IReadOnlyDictionary<string, ChronicConditionTerms>> ChronicConditions(IReadOnlyCollection<string> conditionCodes, DateOnly day, CancellationToken cancellationToken) =>
        (await InForce<ChronicConditionList>(day).Where(c => conditionCodes.Contains(c.ConditionCode)).ToListAsync(cancellationToken))
        .GroupBy(c => c.ConditionCode).ToDictionary(g => g.Key, g => new ChronicConditionTerms(g.First().BenefitCategory, g.First().DiagnosisCodes));

    public async Task<IReadOnlyDictionary<string, DiseaseProgrammeTerms>> DiseaseProgrammes(IReadOnlyCollection<string> programmeCodes, DateOnly day, CancellationToken cancellationToken) =>
        (await InForce<DiseaseProgrammes>(day).Where(p => programmeCodes.Contains(p.ProgrammeCode)).ToListAsync(cancellationToken))
        .GroupBy(p => p.ProgrammeCode).ToDictionary(g => g.Key, g => new DiseaseProgrammeTerms(g.First().BenefitCategory, g.First().ConditionCodes, g.First().ReviewInterval));

    public async Task<IReadOnlyList<PreventiveRule>> PreventiveRules(string planCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<PreventiveSchedules>(day).Where(s => s.PlanCode == null || s.PlanCode == planCode)
            .Select(s => new PreventiveRule(s.PlanCode, s.ServiceCode, s.BenefitCategory, s.MinAge, s.MaxAge, s.Sex, s.ServiceInterval, s.NoCopay)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<MedicationLimit>> MedicationLimits(string planCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<StaticData.MedicationLimits>(day).Where(l => l.PlanCode == null || l.PlanCode == planCode)
            .Select(l => new MedicationLimit(l.PlanCode, l.ProductCode, l.ActiveIngredient, l.MaxQuantity, l.MaxAmount, l.Period)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PrescriptionBenefit>> PrescriptionBenefits(string planCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<PrescriptionBenefits>(day).Where(b => b.PlanCode == null || b.PlanCode == planCode)
            .Select(b => new PrescriptionBenefit(b.PlanCode, b.Tier, b.Covered, b.BenefitCategory, b.CopayType, b.CopayValue, b.LimitAmount, b.PrescriptionValidityDays, b.RefillTolerancePercent))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<FraudIndicator>> FraudIndicators(DateOnly day, CancellationToken cancellationToken) =>
        (await InForce<HealthFraudIndicatorDefinitions>(day).ToListAsync(cancellationToken)).Select(f => new FraudIndicator(f.IndicatorCode, f.Logic, f.Score, f.Threshold))
        .ToList();

    private IQueryable<T> InForce<T>(DateOnly day) where T : StaticRecord =>
        db.Set<T>().AsNoTracking().InForce(HealthStaticData.Descriptors.Single(d => d.EntityType == typeof(T)).Lifecycle, day);
}

internal sealed class HealthEventMapper : IDomainEventMapper
{
    /// <summary>The Health rights notice: the claim number, the date, the total paid, and the reason's customer wording when it has one (never clinical).</summary>
    private static Dictionary<string, string> RightsMerge(Domain.Care.MedicalClaimDecisionRightsDue e)
    {
        var merge = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["claim_number"] = e.ClaimNumber ?? "",
            ["decision_date"] = e.DecidedOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["amount"] = e.TotalPaid.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            ["currency"] = e.Currency,
        };
        if (e.DecisionReasons is { Length: > 0 } reasons)
        {
            merge["decision_reasons"] = reasons;
        }

        return merge;
    }

    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        Domain.Members.HealthSchemeRenewed e => new Contracts.HealthSchemeRenewed(e.SchemeId, e.SchemeNumber, e.RenewalDate),
        Domain.Members.HealthMemberEnrolled e => new Contracts.HealthMemberEnrolled(e.MembershipId, e.MemberNumber, e.PrincipalPartyId, e.SchemeId, e.PlanCode, e.OptionCode,
            e.StartDate, e.PayerPartyId, e.BillingAccountRef, e.CollectionMethod, e.Currency, e.MonthlyContribution, e.EmployerContribution, e.SponsorPartyId,
            e.OrgNodeCode, e.ProductCode),
        Domain.Members.MembershipProductAssigned e => new Contracts.MembershipProductAssigned(e.MembershipId, e.MemberNumber, e.ProductCode),
        // Wave 4 §4.5: the sponsor is told of the scheme's renewal.
        Domain.Members.HealthSchemeRenewalNoticeDue e => new Communications.Contracts.NotificationRequested(Communications.Contracts.NotificationTypes.SchemeRenewalNotice,
            e.SponsorPartyId, "HEALTH_SCHEME", e.SchemeId, new Dictionary<string, string>
            {
                ["scheme_number"] = e.SchemeNumber,
                ["renewal_date"] = e.RenewalDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            }),
        // F006: a person decides termination; the member is told of the suspension.
        Domain.Members.MembershipTerminationDue e => new Contracts.MembershipTerminationDue(e.MembershipId, e.MemberNumber, e.ArrearsSince),
        Domain.Members.MembershipArrearsCleared e => new Contracts.MembershipArrearsCleared(e.MembershipId, e.MemberNumber),
        Domain.Members.MembershipSuspendedForArrears e => new Communications.Contracts.NotificationRequested(Communications.Contracts.NotificationTypes.ContributionSuspension,
            e.PrincipalPartyId, "HEALTH_MEMBERSHIP", e.MembershipId, new Dictionary<string, string>
            {
                ["member_number"] = e.MemberNumber, ["suspended_from"] = e.SuspendedFrom.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ["notice_ends"] = e.TerminationNoticeEnds.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            }),
        Domain.Members.HealthMemberStatusChanged e => new Contracts.HealthMemberStatusChanged(e.MembershipId, e.MemberNumber, e.Status, e.EffectiveDate,
            e.MonthlyContribution, e.EmployerContribution),
        Domain.Members.DependantAdded e => new Contracts.DependantAdded(e.MembershipId, e.MemberNumber, e.DependantId, e.PartyId, e.Suffix, e.StartDate, e.MonthlyContribution,
            e.EmployerContribution),
        Domain.Benefits.EligibilityDecisionRecorded e => new Contracts.EligibilityDecisionRecorded(e.DecisionId, e.MemberNumber, e.DependantSuffix, e.ProviderId,
            e.ServiceType, e.ResponseCode),
        Domain.Benefits.BenefitLimitReached e => new Contracts.BenefitLimitReached(e.AccumulatorId, e.MembershipId, e.BenefitCategory, e.PeriodEnd),
        Domain.Care.PreauthorisationApproved e => new Contracts.PreauthorisationApproved(e.PreauthorisationId, e.AuthNumber, e.MembershipId, e.PatientPartyId, e.ValidFrom,
            e.ValidTo, e.Reservations.Select(r => new Contracts.ReservationChange(r.LineId, r.Category, r.Amount)).ToList()),
        Domain.Care.PreauthorisationRejected e => new Contracts.PreauthorisationRejected(e.PreauthorisationId, e.AuthNumber, e.Reason),
        Domain.Care.PreauthorisationExpired e => new Contracts.PreauthorisationExpired(e.PreauthorisationId, e.AuthNumber, e.ValidTo),
        Domain.Care.PreauthorisationReservationsChanged e => new Contracts.PreauthorisationReservationsChanged(e.PreauthorisationId, e.AuthNumber, e.MembershipId,
            e.PatientPartyId, e.ValidFrom, e.Changes.Select(r => new Contracts.ReservationChange(r.LineId, r.Category, r.Amount)).ToList(), e.Why),
        Domain.Care.MedicalClaimSubmitted e => new Contracts.MedicalClaimSubmitted(e.ClaimId, e.ClaimNumber, e.Source, e.ProviderId, e.MembershipId, e.ServiceDate,
            e.TotalClaimed, e.Currency),
        Domain.Care.MedicalClaimAdjudicated e => new Contracts.MedicalClaimAdjudicated(e.ClaimId, e.ClaimNumber, e.MembershipId, e.Pended, e.TotalPaid, e.Currency,
            e.AuthorisationUses.Select(u => new Contracts.AuthorisationUse(u.PreauthorisationId, u.LineId, u.Released, u.Consumed)).ToList(),
            e.HighCost.Select(h => new Contracts.HighCostLine(h.LineNo, h.RuleCode)).ToList()),
        Domain.Care.MedicalClaimSettled e => new Contracts.MedicalClaimSettled(e.ClaimId, e.ClaimNumber, e.ProviderId, e.PaymentRunId, e.Amount, e.Currency, e.PaidOn),
        Domain.Care.MedicalClaimRejected e => new Contracts.MedicalClaimRejected(e.ClaimId, e.ClaimNumber, e.MembershipId, e.ReasonCode),
        Domain.Care.MedicalClaimDecisionRightsDue e => new Communications.Contracts.NotificationRequested(Communications.Contracts.NotificationTypes.ClaimDecisionRights,
            e.RecipientPartyId, "HEALTH_CLAIM", e.ClaimId, RightsMerge(e), e.ProductCode, e.OrgNodeCode),
        Domain.Care.ChronicRegistrationDeclinedRightsDue e => new Communications.Contracts.NotificationRequested(Communications.Contracts.NotificationTypes.ClaimDecisionRights,
            e.RecipientPartyId, "HEALTH_CHRONIC", e.RegistrationId, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["claim_number"] = e.RegistrationNumber, ["decision_date"] = e.DecidedOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), ["amount"] = "0.00", ["currency"] = e.Currency,
            }, e.ProductCode, e.OrgNodeCode),
        Domain.Care.ProgrammeReviewDue e => new Contracts.ProgrammeReviewDue(e.ReviewId, e.EnrolmentId, e.EnrolmentNumber),
        Domain.Care.CareActivityOverdue e => new Contracts.CareActivityOverdue(e.CaseId, e.CaseNumber, e.ActivityId, e.CaseManagerId),
        Domain.Providers.ProviderMeasureBreached e => new Contracts.ProviderMeasureBreached(e.ResultId, e.ProviderId, e.MonthKey, e.MeasureCode, e.Value, e.Threshold),
        Domain.Care.MedicalClaimReversed e => new Contracts.MedicalClaimReversed(e.ClaimId, e.ClaimNumber, e.ProviderId, e.Amount, e.Currency, e.Reason),
        Domain.Care.UtilisationCaseConcluded e => new Contracts.UtilisationCaseConcluded(e.CaseId, e.MembershipId, e.RuleCode, e.Outcome),
        Domain.Care.FraudCaseConcluded e => new Contracts.FraudCaseConcluded(e.CaseId, e.SubjectType, e.SubjectRef, e.OutcomeCode),
        Domain.Payments.ProviderRecoveryRaised e => new Contracts.ProviderRecoveryRaised(e.RecoveryId, e.RecoveryNumber, e.DebtorKind, e.ProviderId, e.Source, e.Amount, e.Currency, e.RaisedOn),
        ProviderRecoveryNoticeDue e => new Communications.Contracts.NotificationRequested(Communications.Contracts.NotificationTypes.ProviderRecoveryNotice, e.PartyId, "HEALTH_RECOVERY", e.RecoveryId,
            new Dictionary<string, string> { ["recovery_number"] = e.RecoveryNumber, ["claim_number"] = e.ClaimNumber ?? "", ["amount"] = e.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), ["currency"] = e.Currency }, null, null),
        Domain.Payments.ProviderRecoveryInvoiceRequested e => new Contracts.ProviderRecoveryInvoiceRequested(e.RecoveryId, e.RecoveryNumber, e.PayerPartyId, e.Amount, e.Currency, e.DueOn, e.CollectionMethod),
        Domain.Payments.ProviderRecoveryOffsetTaken e => new Contracts.ProviderRecoveryOffsetTaken(e.OffsetId, e.RecoveryId, e.RunId, e.RemittanceId, e.Amount, e.Currency, e.TakenOn),
        Domain.Payments.ProviderRecoveryRepaid e => new Contracts.ProviderRecoveryRepaid(e.RecoveryId, e.RunId, e.Amount, e.Currency, e.RepaidOn),
        Domain.Payments.ProviderRecoveryWrittenOff e => new Contracts.ProviderRecoveryWrittenOff(e.RecoveryId, e.RecoveryNumber, e.ProviderId, e.Amount, e.Currency, e.WrittenOffOn),
        Domain.Payments.ProviderRecoveryReversed e => new Contracts.ProviderRecoveryReversed(e.RecoveryId, e.RecoveryNumber, e.ProviderId, e.Source, e.Amount, e.Currency, e.ReversedOn),
        Domain.Payments.ProviderRecoverySettled e => new Contracts.ProviderRecoverySettled(e.RecoveryId, e.Amount, e.Currency, e.SettledOn),
        Domain.Payments.ProviderRecoveryReviewDue e => new Contracts.ProviderRecoveryReviewDue(e.RecoveryId, e.RecoveryNumber, e.DebtorKind, e.ProviderId),
        ProviderRecoveryWriteOffRequested e => new ApprovalRequested(HealthApprovals.RecoveryBusinessRefType, e.RecoveryId, HealthApprovals.RecoveryWriteOffApprovalType, e.RequestedBy, e.Amount, e.Currency,
            $"Write off recovery {e.RecoveryNumber}", e.SubjectHash),
        ProviderRecoveryFindingProposed e => new ApprovalRequested(HealthApprovals.RecoveryBusinessRefType, e.RecoveryId, HealthApprovals.RecoveryFindingApprovalType, e.RequestedBy, e.Amount, e.Currency,
            $"Overpayment finding {e.RecoveryNumber}", e.SubjectHash),
        ProviderCreditNoteSubmitted e => new ApprovalRequested(HealthApprovals.CreditNoteBusinessRefType, e.CreditNoteId, HealthApprovals.CreditNoteApprovalType, e.RequestedBy, e.Amount, e.Currency,
            $"Provider credit note {e.CreditNoteNumber}", e.SubjectHash),
        PaymentRunCalculated e => new ApprovalRequested(HealthApprovals.RunBusinessRefType, e.RunId, HealthApprovals.RunApprovalType, e.RequestedBy, e.Total, e.Currency,
            $"Provider payment run {e.RunCode}", e.SubjectHash),
        Domain.Payments.ProviderPaymentRunReleased e => new Contracts.ProviderPaymentRunReleased(e.RunId, e.RunCode, e.Total, e.Currency,
            e.Remittances.Select(r => new Contracts.RemittanceReleased(r.RemittanceId, r.ProviderId, r.ProviderPartyId, r.BankAccountId, r.Amount, r.Gross, r.Offset, r.Repaid)).ToList(), e.ReleasedOn,
            e.RemittanceTemplate),
        _ => null,
    };
}
