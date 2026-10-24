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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Health.Domain.Adjudication;
using Insys.Health.Domain.Care;
using Insys.Health.Domain.Members;
using Insys.Health.Domain.Payments;

namespace Insys.Health.Application;

// GAP-040: Health's record lists, by party, number and status, with cursor paging by ID. Open a record for its detail.

public sealed record MembershipSummary(Guid Id, string MemberNumber, Guid PrincipalPartyId, Guid? SchemeId, string PlanCode, string OptionCode, MembershipStatus Status,
    DateOnly StartDate, DateOnly? EndDate);

public sealed record PreauthorisationSummary(Guid Id, string AuthNumber, Guid MembershipId, Guid PatientPartyId, string ProviderId, PreauthStatus Status,
    decimal RequestedAmount, decimal? AuthorisedAmount, DateOnly? ValidFrom, DateOnly? ValidTo);

public sealed record MedicalClaimSummary(Guid Id, string ClaimNumber, Guid MembershipId, Guid PatientPartyId, string? ProviderId, DateOnly ServiceDate,
    MedicalClaimStatus Status, decimal TotalClaimed, decimal? TotalPaid, string Currency);

public sealed record UtilisationCaseSummary(Guid Id, Guid MembershipId, string RuleCode, UtilisationStatus Status, Guid? ClaimId, DateTimeOffset OpenedAt);

/// <summary>A fraud case: <c>SubjectType</c> CLAIM with the medical claim's number as <c>SubjectRef</c>.</summary>
public sealed record FraudCaseSummary(Guid Id, string SubjectType, string SubjectRef, IReadOnlyList<string> IndicatorCodes, FraudStatus Status, DateTimeOffset OpenedAt);

public sealed record PaymentRunSummary(Guid Id, string RunCode, DateTimeOffset CutOff, int ClaimCount, decimal TotalAmount, RunStatus Status, string Currency);

public sealed record ListMemberships(Guid? PrincipalPartyId, string? MemberNumber, Guid? SchemeId, MembershipStatus? Status, Guid? AfterId, int Limit)
    : IQuery<IReadOnlyList<MembershipSummary>>;

public sealed record ListPreauthorisations(Guid? MembershipId, Guid? PatientPartyId, string? AuthNumber, PreauthStatus? Status, Guid? AfterId, int Limit)
    : IQuery<IReadOnlyList<PreauthorisationSummary>>;

public sealed record ListMedicalClaims(Guid? MembershipId, Guid? PatientPartyId, string? ClaimNumber, MedicalClaimStatus? Status, Guid? AfterId, int Limit)
    : IQuery<IReadOnlyList<MedicalClaimSummary>>;

public sealed record ListUtilisationCases(Guid? MembershipId, Guid? ClaimId, UtilisationStatus? Status, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<UtilisationCaseSummary>>;

/// <summary>Find fraud cases: by the claim number they are about, or by status.</summary>
public sealed record ListFraudCases(string? ClaimNumber, FraudStatus? Status, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<FraudCaseSummary>>;

public sealed record ListPaymentRuns(string? RunCode, RunStatus? Status, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PaymentRunSummary>>;

public interface IHealthLists
{
    Task<IReadOnlyList<MembershipSummary>> Memberships(ListMemberships query, CancellationToken cancellationToken);

    Task<IReadOnlyList<PreauthorisationSummary>> Preauthorisations(ListPreauthorisations query, CancellationToken cancellationToken);

    Task<IReadOnlyList<MedicalClaimSummary>> Claims(ListMedicalClaims query, CancellationToken cancellationToken);

    Task<IReadOnlyList<UtilisationCaseSummary>> UtilisationCases(ListUtilisationCases query, CancellationToken cancellationToken);

    Task<IReadOnlyList<FraudCaseSummary>> FraudCases(ListFraudCases query, CancellationToken cancellationToken);

    Task<IReadOnlyList<PaymentRunSummary>> Runs(ListPaymentRuns query, CancellationToken cancellationToken);
}

internal sealed class HealthListHandlers(IHealthLists lists) :
    IQueryHandler<ListMemberships, IReadOnlyList<MembershipSummary>>,
    IQueryHandler<ListPreauthorisations, IReadOnlyList<PreauthorisationSummary>>,
    IQueryHandler<ListMedicalClaims, IReadOnlyList<MedicalClaimSummary>>,
    IQueryHandler<ListUtilisationCases, IReadOnlyList<UtilisationCaseSummary>>,
    IQueryHandler<ListFraudCases, IReadOnlyList<FraudCaseSummary>>,
    IQueryHandler<ListPaymentRuns, IReadOnlyList<PaymentRunSummary>>
{
    public async Task<Result<IReadOnlyList<MembershipSummary>>> Handle(ListMemberships query, CancellationToken cancellationToken) =>
        Result.Success(await lists.Memberships(query, cancellationToken));

    public async Task<Result<IReadOnlyList<PreauthorisationSummary>>> Handle(ListPreauthorisations query, CancellationToken cancellationToken) =>
        Result.Success(await lists.Preauthorisations(query, cancellationToken));

    public async Task<Result<IReadOnlyList<MedicalClaimSummary>>> Handle(ListMedicalClaims query, CancellationToken cancellationToken) =>
        Result.Success(await lists.Claims(query, cancellationToken));

    public async Task<Result<IReadOnlyList<UtilisationCaseSummary>>> Handle(ListUtilisationCases query, CancellationToken cancellationToken) =>
        Result.Success(await lists.UtilisationCases(query, cancellationToken));

    public async Task<Result<IReadOnlyList<FraudCaseSummary>>> Handle(ListFraudCases query, CancellationToken cancellationToken) =>
        Result.Success(await lists.FraudCases(query, cancellationToken));

    public async Task<Result<IReadOnlyList<PaymentRunSummary>>> Handle(ListPaymentRuns query, CancellationToken cancellationToken) =>
        Result.Success(await lists.Runs(query, cancellationToken));
}
