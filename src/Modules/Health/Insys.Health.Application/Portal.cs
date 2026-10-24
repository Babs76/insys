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
// the principal or a dependant. A provider sees its own preauthorisations, medical claims and remittances, and submits
// only as itself: the provider is the link's, whatever a request says (§3). The views leave out adjudication internals,
// fraud, clinical review, case management and benefit reservations (§4).

public sealed record PortalDependantView(Guid PartyId, string DependantType, string Suffix, DateOnly StartDate, DateOnly? EndDate);

public sealed record PortalMembershipView(Guid Id, string MemberNumber, string? ProductCode, string PlanCode, string OptionCode, MembershipStatus Status, DateOnly StartDate,
    DateOnly? EndDate, Guid PrincipalPartyId, IReadOnlyList<PortalDependantView> Dependants)
{
    public static PortalMembershipView From(HealthMembership m) => new(m.Id, m.MemberNumber, m.ProductCode, m.PlanCode, m.OptionCode, m.Status, m.StartDate, m.EndDate,
        m.PrincipalPartyId, m.Dependants.Select(d => new PortalDependantView(d.PartyId, d.DependantType, d.Suffix, d.StartDate, d.EndDate)).ToList());
}

public sealed record PortalClaimLineView(int LineNo, string TariffCode, int Quantity, decimal ClaimedAmount, decimal PaidAmount, string? Outcome);

public sealed record PortalMedicalClaimView(Guid Id, string ClaimNumber, string? ProviderId, DateOnly ServiceDate, MedicalClaimStatus Status, decimal TotalClaimed,
    decimal? TotalPaid, string Currency, IReadOnlyList<PortalClaimLineView> Lines)
{
    public static PortalMedicalClaimView From(MedicalClaim c) => new(c.Id, c.ClaimNumber, c.ProviderId, c.ServiceDate, c.Status, c.TotalClaimed, c.TotalPaid, c.Currency,
        c.Lines.OrderBy(l => l.LineNo).Select(l => new PortalClaimLineView(l.LineNo, l.TariffCode, l.Quantity, l.ClaimedAmount, l.PaidAmount, l.Outcome)).ToList());
}

public sealed record PortalPreauthLineView(string ProcedureCode, int Quantity, decimal AuthorisedAmount);

public sealed record PortalPreauthorisationView(Guid Id, string AuthNumber, string ProviderId, PreauthStatus Status, decimal RequestedAmount, decimal? AuthorisedAmount,
    DateOnly? ValidFrom, DateOnly? ValidTo, IReadOnlyList<PortalPreauthLineView> Lines)
{
    public static PortalPreauthorisationView From(Preauthorisation p) => new(p.Id, p.AuthNumber, p.ProviderId, p.Status, p.RequestedAmount, p.AuthorisedAmount, p.ValidFrom,
        p.ValidTo, p.Lines.Select(l => new PortalPreauthLineView(l.ProcedureCode, l.Quantity, l.AuthorisedAmount)).ToList());
}

public sealed record PortalRemittanceView(Guid Id, string RunCode, decimal Amount, int ClaimCount, string Currency, Guid? PaymentId, DateOnly? PaidOn);

/// <summary>The member being treated: the membership, and the principal or the dependant with the suffix.</summary>
public sealed record PortalPatient(Guid MembershipId, Guid PatientPartyId);

public interface IHealthPortalQueries
{
    Task<IReadOnlyList<MedicalClaim>> ProviderClaims(string providerId, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<PortalRemittanceView>> ProviderRemittances(string providerId, Guid? afterId, int limit, CancellationToken cancellationToken);
}

public sealed record GetPortalMembership(Guid MembershipId) : IQuery<PortalMembershipView>;

public sealed record GetPortalMedicalClaim(Guid ClaimId) : IQuery<PortalMedicalClaimView>;

public sealed record ListPortalMedicalClaims(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PortalMedicalClaimView>>;

public sealed record GetPortalPreauthorisation(Guid PreauthorisationId) : IQuery<PortalPreauthorisationView>;

public sealed record ListPortalRemittances(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PortalRemittanceView>>;

/// <summary>Before a provider's claim or preauthorisation request: the member being treated, by member number (and dependant suffix).</summary>
public sealed record PortalPatientOf(string MemberNumber, string? DependantSuffix) : IQuery<PortalPatient>;

internal sealed class HealthPortalHandlers(IHealthRepository health, IHealthPortalQueries queries, IExecutionContext execution) :
    IQueryHandler<GetPortalMembership, PortalMembershipView>,
    IQueryHandler<GetPortalMedicalClaim, PortalMedicalClaimView>,
    IQueryHandler<ListPortalMedicalClaims, IReadOnlyList<PortalMedicalClaimView>>,
    IQueryHandler<GetPortalPreauthorisation, PortalPreauthorisationView>,
    IQueryHandler<ListPortalRemittances, IReadOnlyList<PortalRemittanceView>>,
    IQueryHandler<PortalPatientOf, PortalPatient>
{
    private ExternalSubject Who => execution.External ?? throw new InvalidOperationException("A portal endpoint is used by external users only.");

    public async Task<Result<PortalMembershipView>> Handle(GetPortalMembership query, CancellationToken cancellationToken) =>
        await health.Membership(query.MembershipId, cancellationToken) is { } m && Who.Kind == ExternalSubject.Party
        && (m.PrincipalPartyId == Who.PartyId || m.Dependants.Any(d => d.PartyId == Who.PartyId))
            ? PortalMembershipView.From(m)
            : Error.NotFound(ErrorCodes.NotFound, "The membership was not found.");

    public async Task<Result<PortalMedicalClaimView>> Handle(GetPortalMedicalClaim query, CancellationToken cancellationToken) =>
        await health.Claim(query.ClaimId, cancellationToken) is { } claim && Who.Kind == ExternalSubject.Provider && claim.ProviderId == Who.ProviderId
            ? PortalMedicalClaimView.From(claim)
            : Error.NotFound(ErrorCodes.NotFound, "The claim was not found.");

    public async Task<Result<IReadOnlyList<PortalMedicalClaimView>>> Handle(ListPortalMedicalClaims query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<PortalMedicalClaimView>>((await queries.ProviderClaims(Who.ProviderId!, query.AfterId, query.Limit, cancellationToken))
            .Select(PortalMedicalClaimView.From).ToList());

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
