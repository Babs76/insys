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
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Health.Contracts;
using Insys.Health.Domain;
using Insys.Health.Domain.Adjudication;
using Insys.Health.Domain.Care;

namespace Insys.Health.Application;

// Wave 5b §4.3 (F083 to F085): chronic registration. The ordinary views leave out the condition, diagnosis and note (HLT-008).

/// <param name="RequestingProviderId">Set from the portal link, never from a request body.</param>
public sealed record RequestChronicRegistration(Guid MembershipId, Guid PatientPartyId, string ConditionCode, string? DiagnosisCode, string SupportingNote, string? RequestingProviderId = null)
    : ICommand<ChronicRegistrationView>;

public sealed record ApproveChronicRegistration(Guid RegistrationId, DateOnly ValidFrom, DateOnly? ValidTo, DateOnly? ReviewOn, IReadOnlyList<EntitlementRequest> Entitlements) : ICommand<ChronicRegistrationView>;

public sealed record DeclineChronicRegistration(Guid RegistrationId, string Reason) : ICommand<ChronicRegistrationView>;

public sealed record EndChronicRegistration(Guid RegistrationId, DateOnly LastDay) : ICommand<ChronicRegistrationView>;

public sealed record GetChronicRegistration(Guid Id) : IQuery<ChronicRegistrationView>;

public sealed record ListChronicRegistrations(Guid? MembershipId,
    [property: OneOf(ChronicStatus.Requested, ChronicStatus.Approved, ChronicStatus.Declined, ChronicStatus.Ended)] string? Status, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<ChronicRegistrationView>>;

public sealed record ReadChronicRegistrationClinical(Guid Id, string? Purpose) : ICommand<ChronicRegistrationClinicalView>;

/// <summary>A member's own registrations and those of minors on their membership (OQ-161): the condition, and what remains of each entitlement this month.</summary>
public sealed record ListPortalChronicRegistrations : IQuery<IReadOnlyList<PortalChronicRegistrationView>>;

public sealed record ChronicRegistrationView(Guid Id, string RegistrationNumber, Guid MembershipId, Guid PatientPartyId,
    [property: OneOf(ChronicStatus.Requested, ChronicStatus.Approved, ChronicStatus.Declined, ChronicStatus.Ended)] string Status, DateOnly? ValidFrom, DateOnly? ValidTo, DateOnly? ReviewOn,
    Guid? DecidedBy, DateTimeOffset? DecidedAt, long RowVersion)
{
    public static ChronicRegistrationView From(ChronicRegistration r) => new(r.Id, r.RegistrationNumber, r.MembershipId, r.PatientPartyId, r.Status, r.ValidFrom, r.ValidTo, r.ReviewOn, r.DecidedBy, r.DecidedAt, r.RowVersion);
}

public sealed record ChronicRegistrationClinicalView(Guid Id, string ConditionCode, string? DiagnosisCode, string SupportingNote, string? DeclineReason, IReadOnlyList<EntitlementRequest> Entitlements);

public sealed record PortalEntitlementView(string? ProductCode, string? ActiveIngredient, int MonthlyQuantity, int RemainingThisMonth);

public sealed record PortalChronicRegistrationView(Guid Id, string RegistrationNumber, Guid PatientPartyId, string ConditionCode, string Status, DateOnly? ValidFrom, DateOnly? ValidTo,
    IReadOnlyList<PortalEntitlementView> Entitlements);

public sealed class RequestChronicRegistrationValidator : AbstractValidator<RequestChronicRegistration>
{
    public RequestChronicRegistrationValidator()
    {
        RuleFor(c => c.ConditionCode).NotEmpty().MaximumLength(15);
        RuleFor(c => c.DiagnosisCode).MaximumLength(10);
        RuleFor(c => c.SupportingNote).NotEmpty().MaximumLength(2000);
    }
}

public sealed class DeclineChronicRegistrationValidator : AbstractValidator<DeclineChronicRegistration>
{
    public DeclineChronicRegistrationValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(500);
}

internal sealed class ChronicRegistrationHandlers(IHealthRepository health, IHealthCodes codes, DependantPrivacy privacy, IExecutionContext execution, IBusinessDate businessDate,
    IClock clock, IAuditTrail audit) :
    ICommandHandler<RequestChronicRegistration, ChronicRegistrationView>,
    ICommandHandler<ApproveChronicRegistration, ChronicRegistrationView>,
    ICommandHandler<DeclineChronicRegistration, ChronicRegistrationView>,
    ICommandHandler<EndChronicRegistration, ChronicRegistrationView>,
    ICommandHandler<ReadChronicRegistrationClinical, ChronicRegistrationClinicalView>,
    IQueryHandler<GetChronicRegistration, ChronicRegistrationView>,
    IQueryHandler<ListChronicRegistrations, IReadOnlyList<ChronicRegistrationView>>,
    IQueryHandler<ListPortalChronicRegistrations, IReadOnlyList<PortalChronicRegistrationView>>
{
    private const int PurposeMaxLength = 200;

    private static Error NotFound => Error.NotFound(ErrorCodes.NotFound, "The registration was not found.");

    private static Error NotClinical => Error.Forbidden(ErrorCodes.Forbidden, "A chronic registration is decided by someone with the clinical function (OQ-150).");

    public async Task<Result<ChronicRegistrationView>> Handle(RequestChronicRegistration command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await health.Membership(command.MembershipId, cancellationToken) is not { } membership || !membership.Covers(command.PatientPartyId, businessDate.Today))
        {
            return Error.Conflict(HealthErrorCodes.MemberNotCovered, "The patient is not covered by the membership today.");
        }

        if (!(await codes.ChronicConditions([command.ConditionCode], businessDate.Today, cancellationToken)).ContainsKey(command.ConditionCode))
        {
            return Error.Validation(HealthErrorCodes.CodeNotInForce, "That is not a chronic condition in force.");
        }

        var id = Guid.CreateVersion7();
        var registration = ChronicRegistration.Request(id, Numbers.Of("CR", id, 25), command.MembershipId, command.PatientPartyId, command.ConditionCode, command.DiagnosisCode, command.SupportingNote, user.Value,
            command.RequestingProviderId);
        health.Add(registration);
        audit.Record(new AuditEntry("HEALTH", "RequestChronicRegistration", nameof(ChronicRegistration), id.ToString()));
        return ChronicRegistrationView.From(registration);
    }

    public async Task<Result<ChronicRegistrationView>> Handle(ApproveChronicRegistration command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (!execution.HasPermission(HealthPermissions.ClinicalRead))
        {
            return NotClinical;
        }

        if (await health.ChronicRegistration(command.RegistrationId, cancellationToken) is not { } registration)
        {
            return NotFound;
        }

        var approved = registration.Approve(command.ValidFrom, command.ValidTo, command.ReviewOn, command.Entitlements, user.Value, clock.UtcNow);
        if (approved.IsFailure)
        {
            return approved.Error;
        }

        audit.Record(new AuditEntry("HEALTH", "ApproveChronicRegistration", nameof(ChronicRegistration), registration.Id.ToString()));
        return ChronicRegistrationView.From(registration) with { RowVersion = registration.RowVersion + 1 };
    }

    public async Task<Result<ChronicRegistrationView>> Handle(DeclineChronicRegistration command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (!execution.HasPermission(HealthPermissions.ClinicalRead))
        {
            return NotClinical;
        }

        if (await health.ChronicRegistration(command.RegistrationId, cancellationToken) is not { } registration || await health.Membership(registration.MembershipId, cancellationToken) is not { } membership)
        {
            return NotFound;
        }

        // The notice goes to the patient, or to the principal for a minor.
        var recipient = await privacy.IsMinor(registration.PatientPartyId, cancellationToken) ? membership.PrincipalPartyId : registration.PatientPartyId;
        var declined = registration.Decline(command.Reason, user.Value, recipient, membership.ProductCode, membership.OrgNodeCode, membership.Currency ?? "", businessDate.Today, clock.UtcNow);
        if (declined.IsFailure)
        {
            return declined.Error;
        }

        audit.Record(new AuditEntry("HEALTH", "DeclineChronicRegistration", nameof(ChronicRegistration), registration.Id.ToString()));
        return ChronicRegistrationView.From(registration) with { RowVersion = registration.RowVersion + 1 };
    }

    public async Task<Result<ChronicRegistrationView>> Handle(EndChronicRegistration command, CancellationToken cancellationToken)
    {
        if (await health.ChronicRegistration(command.RegistrationId, cancellationToken) is not { } registration)
        {
            return NotFound;
        }

        var ended = registration.End(command.LastDay);
        if (ended.IsFailure)
        {
            return ended.Error;
        }

        audit.Record(new AuditEntry("HEALTH", "EndChronicRegistration", nameof(ChronicRegistration), registration.Id.ToString()));
        return ChronicRegistrationView.From(registration) with { RowVersion = registration.RowVersion + 1 };
    }

    public async Task<Result<ChronicRegistrationClinicalView>> Handle(ReadChronicRegistrationClinical command, CancellationToken cancellationToken)
    {
        var target = command.Id.ToString();
        if (string.IsNullOrWhiteSpace(command.Purpose) || command.Purpose.Length > PurposeMaxLength)
        {
            audit.Record(Denied(target, "PURPOSE_REQUIRED", null));
            return Error.Validation(HealthErrorCodes.PurposeRequired, $"Reading clinical information needs a purpose of up to {PurposeMaxLength} characters, which is recorded (HLT-008).");
        }

        if (await health.ChronicRegistration(command.Id, cancellationToken) is not { } registration)
        {
            audit.Record(Denied(target, "NOT_FOUND", command.Purpose));
            return NotFound;
        }

        audit.Record(new AuditEntry("HEALTH", "ReadClinical", nameof(ChronicRegistration), target, new Dictionary<string, string?> { ["purpose"] = command.Purpose }));
        return new ChronicRegistrationClinicalView(registration.Id, registration.ConditionCode, registration.DiagnosisCode, registration.SupportingNote, registration.DeclineReason,
            registration.Entitlements.Select(e => new EntitlementRequest(e.ProductCode, e.ActiveIngredient, e.MonthlyQuantity)).ToList());
    }

    public async Task<Result<ChronicRegistrationView>> Handle(GetChronicRegistration query, CancellationToken cancellationToken) =>
        await health.ChronicRegistration(query.Id, cancellationToken) is { } registration ? ChronicRegistrationView.From(registration) : NotFound;

    public async Task<Result<IReadOnlyList<ChronicRegistrationView>>> Handle(ListChronicRegistrations query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<ChronicRegistrationView>>((await health.ChronicRegistrations(query.MembershipId, query.Status, query.AfterId, query.Limit, cancellationToken))
            .Select(ChronicRegistrationView.From).ToList());

    public async Task<Result<IReadOnlyList<PortalChronicRegistrationView>>> Handle(ListPortalChronicRegistrations query, CancellationToken cancellationToken)
    {
        if (execution.External is not { Kind: ExternalSubject.Party, PartyId: { } party })
        {
            return Result.Success<IReadOnlyList<PortalChronicRegistrationView>>([]);
        }

        var today = businessDate.Today;
        var views = new List<PortalChronicRegistrationView>();
        foreach (var registration in await health.MemberChronicRegistrations(party, cancellationToken))
        {
            if (!await privacy.MaySeeClinical(party, registration.PatientPartyId, cancellationToken))
            {
                continue;
            }

            var taken = await health.PriorMedicines(registration.PatientPartyId, new DateOnly(today.Year, today.Month, 1), Guid.Empty, cancellationToken);
            views.Add(new PortalChronicRegistrationView(registration.Id, registration.RegistrationNumber, registration.PatientPartyId, registration.ConditionCode, registration.Status, registration.ValidFrom,
                registration.ValidTo, registration.Entitlements.Select(e => new PortalEntitlementView(e.ProductCode, e.ActiveIngredient, e.MonthlyQuantity,
                    Math.Max(0, e.MonthlyQuantity - taken.Where(m => e.ToTerms().Covers(m.ProductCode, m.ActiveIngredient)).Sum(m => m.Quantity)))).ToList()));
        }

        return Result.Success<IReadOnlyList<PortalChronicRegistrationView>>(views);
    }

    private static AuditEntry Denied(string target, string reason, string? purpose) =>
        new("HEALTH", "ReadClinical", nameof(ChronicRegistration), target, new Dictionary<string, string?> { ["refused"] = reason, ["purpose"] = purpose })
        {
            Outcome = AuditOutcome.Denied,
        };
}
