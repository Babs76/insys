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
using Insys.BuildingBlocks.Domain;
using Insys.Health.Domain;
using Insys.Health.Domain.Care;
using Insys.Health.Domain.Members;
using Insys.Health.Domain.Payments;

namespace Insys.Health.Application;

// External-user access (docs/specs/addenda/external-user-access.md) in Health. A member sees a membership where they are
// the principal or a dependant; a dependant sees only their own cover on it, and only the claims where they are the patient. A provider sees its own preauthorisations, medical claims and remittances, and submits
// only as itself: the provider is the link's, whatever a request says (§3). The views leave out adjudication internals,
// fraud, clinical review, case management and benefit reservations (§4).

public sealed record PortalDependantView(Guid PartyId, string DependantType, string Suffix, DateOnly StartDate, DateOnly? EndDate);

/// <param name="ViewedAs">How the caller relates to the membership, which decides what the view shows.</param>
/// <param name="PrincipalPartyId">Null for a dependant.</param>
/// <param name="Dependants">For the principal, every dependant; for a dependant, only their own entry.</param>
public sealed record PortalMembershipView(Guid Id, string MemberNumber, string? ProductCode, string PlanCode, string OptionCode, MembershipStatus Status, DateOnly StartDate,
    DateOnly? EndDate, [property: BuildingBlocks.Domain.OneOf(PortalMembershipView.Principal, PortalMembershipView.Dependant)] string ViewedAs, Guid? PrincipalPartyId,
    IReadOnlyList<PortalDependantView> Dependants)
{
    public const string Principal = "PRINCIPAL";

    public const string Dependant = "DEPENDANT";

    /// <summary>
    /// The view for a member of the membership: the principal sees it whole; a dependant sees the plan and option they are
    /// covered by and their own entry, nothing of the principal or the other dependants (privacy check, 28 October 2026).
    /// </summary>
    public static PortalMembershipView For(HealthMembership m, Guid partyId)
    {
        var principal = m.PrincipalPartyId == partyId;
        return new(m.Id, m.MemberNumber, m.ProductCode, m.PlanCode, m.OptionCode, m.Status, m.StartDate, m.EndDate, principal ? Principal : Dependant,
            principal ? m.PrincipalPartyId : null,
            m.Dependants.Where(d => principal || d.PartyId == partyId)
                .Select(d => new PortalDependantView(d.PartyId, d.DependantType, d.Suffix, d.StartDate, d.EndDate)).ToList());
    }
}

/// <param name="TariffCode">Null when the viewer may not see what was treated: OQ-161, a principal member looking at an adult dependant's claim.</param>
public sealed record PortalClaimLineView(int LineNo, [property: BuildingBlocks.Contracts.AlwaysPresent] string? TariffCode, int Quantity, decimal ClaimedAmount, decimal PaidAmount,
    [property: BuildingBlocks.Domain.OneOf("PAY", "PEND", "REJECT")] string? Outcome);

public sealed record PortalMedicalClaimView(Guid Id, string ClaimNumber, string? ProviderId, DateOnly ServiceDate, MedicalClaimStatus Status, decimal TotalClaimed,
    decimal? TotalPaid, string Currency, IReadOnlyList<PortalClaimLineView> Lines,
    [property: BuildingBlocks.Contracts.AlwaysPresent] Guid? ReplacesClaimId = null, [property: BuildingBlocks.Contracts.AlwaysPresent] Guid? ReplacedByClaimId = null)
{
    /// <param name="showCodes">False hides what was treated (OQ-161).</param>
    public static PortalMedicalClaimView From(MedicalClaim c, bool showCodes = true) => new(c.Id, c.ClaimNumber, c.ProviderId, c.ServiceDate, c.Status, c.TotalClaimed, c.TotalPaid, c.Currency,
        c.Lines.OrderBy(l => l.LineNo).Select(l => new PortalClaimLineView(l.LineNo, showCodes ? l.TariffCode : null, l.Quantity, l.ClaimedAmount, l.PaidAmount, l.Outcome)).ToList(), c.ReplacesClaimId, c.ReplacedByClaimId);
}

public sealed record PortalPreauthLineView(string ProcedureCode, int Quantity, decimal AuthorisedAmount);

public sealed record PortalPreauthorisationView(Guid Id, string AuthNumber, string ProviderId, PreauthStatus Status, decimal RequestedAmount, decimal? AuthorisedAmount,
    DateOnly? ValidFrom, DateOnly? ValidTo, IReadOnlyList<PortalPreauthLineView> Lines)
{
    public static PortalPreauthorisationView From(Preauthorisation p) => new(p.Id, p.AuthNumber, p.ProviderId, p.Status, p.RequestedAmount, p.AuthorisedAmount, p.ValidFrom,
        p.ValidTo, p.Lines.Select(l => new PortalPreauthLineView(l.ProcedureCode, l.Quantity, l.AuthorisedAmount)).ToList());
}

public sealed record PortalRemittanceView(Guid Id, string RunCode, decimal Amount, int ClaimCount, string Currency, Guid? PaymentId, DateOnly? PaidOn, decimal GrossAmount = 0, decimal OffsetAmount = 0, decimal RepaidAmount = 0);

/// <summary>The member being treated: the membership, and the principal or the dependant with the suffix.</summary>
public sealed record PortalPatient(Guid MembershipId, Guid PatientPartyId);

public interface IHealthPortalQueries
{
    /// <summary>A member's memberships: those where they are the principal or a dependant (BR-091), in the caller's F103 scope.</summary>
    Task<IReadOnlyList<HealthMembership>> MemberMemberships(Guid partyId, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<MedicalClaim>> ProviderClaims(string providerId, Guid? afterId, int limit, CancellationToken cancellationToken);

    /// <summary>A member's claims: those where they are the patient, and every claim of a membership where they are the principal.</summary>
    Task<IReadOnlyList<MedicalClaim>> MemberClaims(Guid partyId, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<PortalRemittanceView>> ProviderRemittances(string providerId, Guid? afterId, int limit, CancellationToken cancellationToken);
}

public sealed record GetPortalMembership(Guid MembershipId) : IQuery<PortalMembershipView>;

public sealed record ListPortalMemberships(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PortalMembershipView>>;

public sealed record GetPortalMedicalClaim(Guid ClaimId) : IQuery<PortalMedicalClaimView>;

public sealed record ListPortalMedicalClaims(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PortalMedicalClaimView>>;

public sealed record GetPortalPreauthorisation(Guid PreauthorisationId) : IQuery<PortalPreauthorisationView>;

public sealed record ListPortalRemittances(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PortalRemittanceView>>;

/// <summary>Before a provider's claim or preauthorisation request: the member being treated, by member number (and dependant suffix).</summary>
public sealed record PortalPatientOf(string MemberNumber, string? DependantSuffix) : IQuery<PortalPatient>;

/// <summary>
/// OQ-161: a principal member does not see an adult dependant's medicines, chronic conditions or diagnoses; a minor's stay visible. Anyone sees their own.
/// A dependant whose birth date can't be read counts as an adult: the record stays hidden.
/// </summary>
public sealed class DependantPrivacy(Party.Contracts.IPersonFacts persons, ITenantContext tenant, IBusinessDate businessDate)
{
    public const int AgeOfMajority = 18;

    public async Task<bool> MaySeeClinical(Guid viewerPartyId, Guid patientPartyId, CancellationToken cancellationToken)
    {
        if (viewerPartyId == patientPartyId)
        {
            return true;
        }

        return await persons.BirthDate(tenant.RequireTenantId(), patientPartyId, cancellationToken) is { } born && IsMinor(born, businessDate.Today);
    }

    public static bool IsMinor(DateOnly born, DateOnly day) => born.AddYears(AgeOfMajority) > day;
}

internal sealed class HealthPortalHandlers(IHealthRepository health, IHealthPortalQueries queries, IExecutionContext execution, DependantPrivacy privacy) :
    IQueryHandler<GetPortalMembership, PortalMembershipView>,
    IQueryHandler<ListPortalMemberships, IReadOnlyList<PortalMembershipView>>,
    IQueryHandler<GetPortalMedicalClaim, PortalMedicalClaimView>,
    IQueryHandler<ListPortalMedicalClaims, IReadOnlyList<PortalMedicalClaimView>>,
    IQueryHandler<GetPortalPreauthorisation, PortalPreauthorisationView>,
    IQueryHandler<ListPortalRemittances, IReadOnlyList<PortalRemittanceView>>,
    IQueryHandler<PortalPatientOf, PortalPatient>
{
    private ExternalSubject Who => execution.External ?? throw new InvalidOperationException("A portal endpoint is used by external users only.");

    public async Task<Result<PortalMembershipView>> Handle(GetPortalMembership query, CancellationToken cancellationToken) =>
        await health.Membership(query.MembershipId, cancellationToken) is { } m && Who is { Kind: ExternalSubject.Party, PartyId: { } party }
        && (m.PrincipalPartyId == party || m.Dependants.Any(d => d.PartyId == party))
            ? PortalMembershipView.For(m, party)
            : Error.NotFound(ErrorCodes.NotFound, "The membership was not found.");

    public async Task<Result<IReadOnlyList<PortalMembershipView>>> Handle(ListPortalMemberships query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<PortalMembershipView>>(Who is { Kind: ExternalSubject.Party, PartyId: { } party }
            ? (await queries.MemberMemberships(party, query.AfterId, query.Limit, cancellationToken)).Select(m => PortalMembershipView.For(m, party)).ToList()
            : []);

    /// <summary>
    /// A provider sees its own claims. A member sees the claims where they are the patient and, as principal, every claim
    /// of their membership; a dependant does not see the principal's or another dependant's (OQ-084).
    /// </summary>
    public async Task<Result<PortalMedicalClaimView>> Handle(GetPortalMedicalClaim query, CancellationToken cancellationToken)
    {
        var who = Who;
        if (await health.Claim(query.ClaimId, cancellationToken) is { } claim)
        {
            var related = who.Kind switch
            {
                ExternalSubject.Provider => claim.ProviderId == who.ProviderId,
                ExternalSubject.Party => claim.PatientPartyId == who.PartyId
                    || (await health.Membership(claim.MembershipId, cancellationToken))?.PrincipalPartyId == who.PartyId,
                _ => false,
            };
            if (related)
            {
                return PortalMedicalClaimView.From(claim, who.Kind != ExternalSubject.Party || await privacy.MaySeeClinical(who.PartyId!.Value, claim.PatientPartyId, cancellationToken));
            }
        }

        return Error.NotFound(ErrorCodes.NotFound, "The claim was not found.");
    }

    public async Task<Result<IReadOnlyList<PortalMedicalClaimView>>> Handle(ListPortalMedicalClaims query, CancellationToken cancellationToken)
    {
        var who = Who;
        var claims = who.Kind == ExternalSubject.Provider
            ? await queries.ProviderClaims(who.ProviderId!, query.AfterId, query.Limit, cancellationToken)
            : await queries.MemberClaims(who.PartyId!.Value, query.AfterId, query.Limit, cancellationToken);
        var shown = new Dictionary<Guid, bool>();
        var views = new List<PortalMedicalClaimView>();
        foreach (var claim in claims)
        {
            if (who.Kind == ExternalSubject.Party && !shown.ContainsKey(claim.PatientPartyId))
            {
                shown[claim.PatientPartyId] = await privacy.MaySeeClinical(who.PartyId!.Value, claim.PatientPartyId, cancellationToken);
            }

            views.Add(PortalMedicalClaimView.From(claim, who.Kind != ExternalSubject.Party || shown[claim.PatientPartyId]));
        }

        return Result.Success<IReadOnlyList<PortalMedicalClaimView>>(views);
    }

    public async Task<Result<PortalPreauthorisationView>> Handle(GetPortalPreauthorisation query, CancellationToken cancellationToken) =>
        await health.Preauthorisation(query.PreauthorisationId, cancellationToken) is { } preauth && Who.Kind == ExternalSubject.Provider && preauth.ProviderId == Who.ProviderId
            ? PortalPreauthorisationView.From(preauth)
            : Error.NotFound(ErrorCodes.NotFound, "The preauthorisation was not found.");

    public async Task<Result<IReadOnlyList<PortalRemittanceView>>> Handle(ListPortalRemittances query, CancellationToken cancellationToken) =>
        Result.Success(await queries.ProviderRemittances(Who.ProviderId!, query.AfterId, query.Limit, cancellationToken));

    public async Task<Result<PortalPatient>> Handle(PortalPatientOf query, CancellationToken cancellationToken)
    {
        if (await health.MembershipByNumber(query.MemberNumber, cancellationToken) is not { } membership)
        {
            return Error.Validation(HealthErrorCodes.MemberNotFound, "No membership has that member number.");
        }

        if (query.DependantSuffix is not { Length: > 0 } suffix)
        {
            return new PortalPatient(membership.Id, membership.PrincipalPartyId);
        }

        return membership.Dependants.FirstOrDefault(d => d.Suffix == suffix && d.EndDate is null) is { } dependant
            ? new PortalPatient(membership.Id, dependant.PartyId)
            : Error.Validation(HealthErrorCodes.MemberNotFound, "The membership has no current dependant with that suffix.");
    }
}
