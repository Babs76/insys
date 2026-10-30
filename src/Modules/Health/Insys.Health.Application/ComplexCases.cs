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
using Insys.Health.Domain.Care;
using Insys.Identity.Contracts;

namespace Insys.Health.Application;

// Wave 5a batch 5a.2 (addenda/providers-and-care.md §4.3 to §4.5; F034 to F038, OQ-140, OQ-143, OQ-144). A case's view holds no
// clinical detail. The clinical route needs a stated purpose, and the current case manager or the clinical function; every read,
// and every refusal, is audited with no clinical content (HLT-008).

public sealed record OpenComplexCase(Guid MembershipId, Guid PatientPartyId,
    [property: OneOf(ComplexCaseOrigin.Membership, ComplexCaseOrigin.Utilisation, ComplexCaseOrigin.HighCost)] string OpenedFrom, Guid? SourceId, string ConditionCategory)
    : ICommand<ComplexCaseView>;

public sealed record AssignCaseManager(Guid CaseId, Guid CaseManagerId, string? Reason) : ICommand<ComplexCaseView>;

public sealed record ChangeComplexCaseStatus(Guid CaseId, [property: OneOf("HOLD", "RESUME")] string Action) : ICommand<ComplexCaseView>;

public sealed record CloseComplexCase(Guid CaseId, string Reason) : ICommand<ComplexCaseView>;

public sealed record SetCarePlan(Guid CaseId, IReadOnlyList<CarePlanItemInput> Items, string? Reason) : ICommand<ComplexCaseView>;

public sealed record RecordCareProgress(Guid CaseId, Guid ItemId, [property: OneOf(CarePlanItemStatus.Done, CarePlanItemStatus.Missed, CarePlanItemStatus.Changed)] string Status,
    string? Note) : ICommand<ComplexCaseView>;

public sealed record AddCareNote(Guid CaseId, [property: OneOf("PROVIDER", "MEMBER", "INTERNAL")] string ContactType, string? ContactRef, string Note, string? SharedWithProviderId,
    string? ConsentReference) : ICommand<ComplexCaseView>;

public sealed record GetComplexCase(Guid Id) : IQuery<ComplexCaseView>;

public sealed record ListComplexCases(Guid? MembershipId, Guid? CaseManagerId, [property: OneOf(ComplexCaseStatus.Open, ComplexCaseStatus.Active, ComplexCaseStatus.OnHold, ComplexCaseStatus.Closed)] string? Status,
    Guid? AfterId, int Limit) : IQuery<IReadOnlyList<ComplexCaseView>>;

/// <summary>Reads a case's clinical detail (BR-072's pattern): a command, since each read is audited with its purpose.</summary>
public sealed record ReadComplexCaseClinical(Guid CaseId, string? Purpose) : ICommand<ComplexCaseClinicalView>;

public sealed record ComplexCaseAssignmentView(Guid CaseManagerId, DateTimeOffset AssignedAt, Guid AssignedBy, string? Reason, DateTimeOffset? EndedAt);

/// <summary>A case without clinical detail: who, in what state, and who manages it.</summary>
public sealed record ComplexCaseView(Guid Id, string CaseNumber, Guid MembershipId, Guid PatientPartyId, string OpenedFrom, Guid? SourceId,
    [property: OneOf(ComplexCaseStatus.Open, ComplexCaseStatus.Active, ComplexCaseStatus.OnHold, ComplexCaseStatus.Closed)] string Status, Guid? CaseManagerId, string? CloseReason,
    IReadOnlyList<ComplexCaseAssignmentView> Assignments, int? CurrentPlanVersion, long RowVersion)
{
    public static ComplexCaseView From(ComplexCase c) => new(c.Id, c.CaseNumber, c.MembershipId, c.PatientPartyId, c.OpenedFrom, c.SourceId, c.Status, c.CaseManagerId, c.CloseReason,
        c.Assignments.OrderBy(a => a.AssignedAt).Select(a => new ComplexCaseAssignmentView(a.CaseManagerId, a.AssignedAt, a.AssignedBy, a.Reason, a.EndedAt)).ToList(),
        c.CurrentPlan?.PlanVersion, c.RowVersion);
}

public sealed record CarePlanItemView(Guid Id, string Kind, string Description, string? Responsible, DateOnly? DueOn, string Status, string? ProgressNote);

public sealed record CarePlanView(int PlanVersion, bool IsCurrent, string? ChangeReason, IReadOnlyList<CarePlanItemView> Items);

public sealed record CareNoteView(Guid Id, [property: OneOf("PROVIDER", "MEMBER", "INTERNAL")] string ContactType, string? ContactRef, string Note, string? SharedWithProviderId, string? ConsentReference, Guid WrittenBy, DateTimeOffset WrittenAt);

public sealed record ComplexCaseClinicalView(Guid CaseId, string CaseNumber, string ConditionCategory, IReadOnlyList<CarePlanView> Plans, IReadOnlyList<CareNoteView> Notes);

public sealed class OpenComplexCaseValidator : AbstractValidator<OpenComplexCase>
{
    public OpenComplexCaseValidator() => RuleFor(c => c.ConditionCategory).NotEmpty().MaximumLength(120);
}

public sealed class CloseComplexCaseValidator : AbstractValidator<CloseComplexCase>
{
    public CloseComplexCaseValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(500);
}

public sealed class AddCareNoteValidator : AbstractValidator<AddCareNote>
{
    public AddCareNoteValidator() => RuleFor(c => c.Note).NotEmpty().MaximumLength(2000);
}

public sealed record RequestClinicalReferral(Guid MembershipId, Guid PatientPartyId, string ReferringProviderId, string? ReferredProviderId, string? ReferredSpecialty,
    string ClinicalReason, DateOnly ValidFrom, DateOnly ValidTo, int VisitsAllowed, Guid? ComplexCaseId, Guid? PreauthorisationId, string? ConsentReference = null) : ICommand<ClinicalReferralView>;

public sealed record DecideClinicalReferral(Guid ReferralId, bool Approved, string? Reason) : ICommand<ClinicalReferralView>;

public sealed record RecordReferralVisit(Guid ReferralId) : ICommand<ClinicalReferralView>;

/// <summary>The member's consent, recorded after the referral was made; audited with a reason.</summary>
public sealed record RecordReferralConsent(Guid ReferralId, string ConsentReference, string Reason) : ICommand<ClinicalReferralView>;

/// <summary>The member withdrew consent; audited with a reason. The receiving provider's access to the reason ends at once.</summary>
public sealed record WithdrawReferralConsent(Guid ReferralId, string Reason) : ICommand<ClinicalReferralView>;

public sealed record GetClinicalReferral(Guid Id) : IQuery<ClinicalReferralView>;

public sealed record ListClinicalReferrals(Guid? MembershipId, string? ProviderId,
    [property: OneOf(ReferralStatus.Requested, ReferralStatus.Approved, ReferralStatus.Declined, ReferralStatus.Used, ReferralStatus.Expired)] string? Status, Guid? AfterId, int Limit)
    : IQuery<IReadOnlyList<ClinicalReferralView>>;

public sealed record ReadReferralClinical(Guid ReferralId, string? Purpose) : ICommand<ReferralClinicalView>;

public sealed record ReferralClinicalView(Guid ReferralId, string ClinicalReason);

/// <summary>A provider reads a referral's reason through the portal: the referring provider, or the receiving one with the member's consent; another gets not found.</summary>
public sealed record ReadPortalReferralClinical(Guid ReferralId, string? Purpose) : ICommand<ReferralClinicalView>;

/// <summary>A referral without its clinical reason.</summary>
public sealed record ClinicalReferralView(Guid Id, string ReferralNumber, Guid MembershipId, Guid PatientPartyId, string ReferringProviderId, string? ReferredProviderId, string? ReferredSpecialty,
    DateOnly ValidFrom, DateOnly ValidTo, int VisitsAllowed, int VisitsUsed, Guid? ComplexCaseId, Guid? PreauthorisationId,
    [property: OneOf(ReferralStatus.Requested, ReferralStatus.Approved, ReferralStatus.Declined, ReferralStatus.Used, ReferralStatus.Expired)] string Status, string? DeclineReason, long RowVersion,
    [property: BuildingBlocks.Contracts.AlwaysPresent] string? ConsentReference = null, [property: BuildingBlocks.Contracts.AlwaysPresent] DateTimeOffset? ConsentWithdrawnAt = null)
{
    public static ClinicalReferralView From(ClinicalReferral r) => new(r.Id, r.ReferralNumber, r.MembershipId, r.PatientPartyId, r.ReferringProviderId, r.ReferredProviderId, r.ReferredSpecialty,
        r.ValidFrom, r.ValidTo, r.VisitsAllowed, r.VisitsUsed, r.ComplexCaseId, r.PreauthorisationId, r.Status, r.DeclineReason, r.RowVersion, r.ConsentReference, r.ConsentWithdrawnAt);
}

public sealed class RecordReferralConsentValidator : AbstractValidator<RecordReferralConsent>
{
    public RecordReferralConsentValidator()
    {
        RuleFor(c => c.ConsentReference).NotEmpty().MaximumLength(120);
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(500);
    }
}

public sealed class WithdrawReferralConsentValidator : AbstractValidator<WithdrawReferralConsent>
{
    public WithdrawReferralConsentValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(500);
}

public sealed class RequestClinicalReferralValidator : AbstractValidator<RequestClinicalReferral>
{
    public RequestClinicalReferralValidator()
    {
        RuleFor(c => c.ReferringProviderId).NotEmpty().MaximumLength(15);
        RuleFor(c => c.ClinicalReason).NotEmpty().MaximumLength(1000);
        RuleFor(c => c.VisitsAllowed).GreaterThan(0);
    }
}

/// <summary>The daily job's work (F037, F038).</summary>
public sealed record FindCareDueWork(DateOnly Day, int Limit) : IQuery<(IReadOnlyList<Guid> Cases, IReadOnlyList<Guid> Referrals)>;

public sealed record RaiseOverdueCareActivities(Guid CaseId, DateOnly Day) : ICommand<int>;

public sealed record ExpireClinicalReferral(Guid ReferralId, DateOnly Day) : ICommand<bool>;

internal sealed class ComplexCaseHandlers(IHealthRepository health, IUserRoleQuery userRoles, IStaffRoles staffRoles, ITenantContext tenant, IExecutionContext execution,
    IBusinessDate businessDate, IClock clock, IAuditTrail audit) :
    ICommandHandler<OpenComplexCase, ComplexCaseView>,
    ICommandHandler<AssignCaseManager, ComplexCaseView>,
    ICommandHandler<ChangeComplexCaseStatus, ComplexCaseView>,
    ICommandHandler<CloseComplexCase, ComplexCaseView>,
    ICommandHandler<SetCarePlan, ComplexCaseView>,
    ICommandHandler<RecordCareProgress, ComplexCaseView>,
    ICommandHandler<AddCareNote, ComplexCaseView>,
    IQueryHandler<GetComplexCase, ComplexCaseView>,
    IQueryHandler<ListComplexCases, IReadOnlyList<ComplexCaseView>>,
    ICommandHandler<ReadComplexCaseClinical, ComplexCaseClinicalView>,
    IQueryHandler<FindCareDueWork, (IReadOnlyList<Guid> Cases, IReadOnlyList<Guid> Referrals)>,
    ICommandHandler<RaiseOverdueCareActivities, int>
{
    public const int PurposeMaxLength = 200;

    private static Error NotFound => Error.NotFound(ErrorCodes.NotFound, "The case was not found.");

    public async Task<Result<ComplexCaseView>> Handle(OpenComplexCase command, CancellationToken cancellationToken)
    {
        if (await health.Membership(command.MembershipId, cancellationToken) is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The membership was not found.");
        }

        var id = Guid.CreateVersion7();
        var opened = ComplexCase.Open(id, Numbers.Of("CC", id, 25), command.MembershipId, command.PatientPartyId, command.OpenedFrom, command.SourceId, command.ConditionCategory);
        health.Add(opened);
        audit.Record(new AuditEntry("HEALTH", "OpenComplexCase", nameof(ComplexCase), id.ToString(), new Dictionary<string, string?> { ["from"] = command.OpenedFrom }));
        return ComplexCaseView.From(opened);
    }

    /// <summary>F035: only someone whose roles grant the case manager function may be assigned.</summary>
    public async Task<Result<ComplexCaseView>> Handle(AssignCaseManager command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await health.ComplexCase(command.CaseId, cancellationToken) is not { } complex)
        {
            return NotFound;
        }

        var tenantId = tenant.RequireTenantId();
        var roles = await userRoles.EffectiveRoles(tenantId, command.CaseManagerId, cancellationToken);
        var granted = await staffRoles.Permissions(tenantId, businessDate.Today, cancellationToken);
        if (!roles.Any(r => granted.TryGetValue(r, out var permissions) && permissions.Contains(HealthPermissions.ComplexCaseManage)))
        {
            return Error.Validation(HealthErrorCodes.NotACaseManager, "That person does not hold the case manager function.");
        }

        var previous = complex.CaseManagerId;
        var assigned = complex.AssignManager(command.CaseManagerId, user.Value, command.Reason, clock.UtcNow);
        if (assigned.IsFailure)
        {
            return assigned.Error;
        }

        audit.Record(new AuditEntry("HEALTH", "AssignCaseManager", nameof(ComplexCase), complex.Id.ToString(), new Dictionary<string, string?>
        {
            ["manager"] = command.CaseManagerId.ToString(), ["previous"] = previous?.ToString(), ["reason"] = command.Reason,
        }));
        return ComplexCaseView.From(complex);
    }

    public Task<Result<ComplexCaseView>> Handle(ChangeComplexCaseStatus command, CancellationToken cancellationToken) =>
        Change(command.CaseId, command.Action, c => command.Action == "HOLD" ? c.Hold() : c.Resume(), false, cancellationToken);

    public Task<Result<ComplexCaseView>> Handle(CloseComplexCase command, CancellationToken cancellationToken) =>
        Change(command.CaseId, "CloseComplexCase", c => c.Close(command.Reason), false, cancellationToken);

    public Task<Result<ComplexCaseView>> Handle(SetCarePlan command, CancellationToken cancellationToken) =>
        Change(command.CaseId, "SetCarePlan", c => c.SetPlan(command.Items, command.Reason), true, cancellationToken);

    public Task<Result<ComplexCaseView>> Handle(RecordCareProgress command, CancellationToken cancellationToken) =>
        Change(command.CaseId, "RecordCareProgress", c => c.RecordProgress(command.ItemId, command.Status, command.Note), true, cancellationToken);

    public async Task<Result<ComplexCaseView>> Handle(AddCareNote command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        return user.IsFailure
            ? user.Error
            : await Change(command.CaseId, "AddCareNote", c => c.AddNote(command.ContactType, command.ContactRef, command.Note, command.SharedWithProviderId, command.ConsentReference, user.Value,
                clock.UtcNow), true, cancellationToken);
    }

    /// <param name="clinical">It writes clinical detail, so only those who may read it may write it.</param>
    private async Task<Result<ComplexCaseView>> Change(Guid id, string operation, Func<ComplexCase, Result> change, bool clinical, CancellationToken cancellationToken)
    {
        if (await health.ComplexCase(id, cancellationToken) is not { } complex)
        {
            return NotFound;
        }

        if (clinical && !MayReadClinical(complex))
        {
            audit.Record(new AuditEntry("HEALTH", operation, nameof(ComplexCase), id.ToString(), new Dictionary<string, string?> { ["refused"] = "NOT_THE_CASE_MANAGER" }) { Outcome = AuditOutcome.Denied });
            return Error.Forbidden(HealthErrorCodes.NotTheCaseManager, "Only the case's manager, or someone with the clinical function, may do this.");
        }

        var changed = change(complex);
        if (changed.IsFailure)
        {
            return changed.Error;
        }

        audit.Record(new AuditEntry("HEALTH", operation, nameof(ComplexCase), id.ToString()));
        return ComplexCaseView.From(complex);
    }

    /// <summary>OQ-143: the current case manager, by being assigned (so reassigning ends it, addition 2), or a holder of the clinical function.</summary>
    private bool MayReadClinical(ComplexCase complex) =>
        execution.HasPermission(HealthPermissions.ClinicalRead) || (Users.Require(execution) is { IsSuccess: true } user && complex.CaseManagerId == user.Value);

    public async Task<Result<ComplexCaseView>> Handle(GetComplexCase query, CancellationToken cancellationToken) =>
        await health.ComplexCase(query.Id, cancellationToken) is { } complex ? ComplexCaseView.From(complex) : NotFound;

    public async Task<Result<IReadOnlyList<ComplexCaseView>>> Handle(ListComplexCases query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<ComplexCaseView>>((await health.ComplexCases(query.MembershipId, query.CaseManagerId, query.Status, query.AfterId, query.Limit, cancellationToken))
            .Select(ComplexCaseView.From).ToList());

    public async Task<Result<ComplexCaseClinicalView>> Handle(ReadComplexCaseClinical command, CancellationToken cancellationToken)
    {
        var target = command.CaseId.ToString();
        if (string.IsNullOrWhiteSpace(command.Purpose) || command.Purpose.Length > PurposeMaxLength)
        {
            audit.Record(Denied(target, "PURPOSE_REQUIRED", null));
            return Error.Validation(HealthErrorCodes.PurposeRequired, $"Reading clinical information needs a purpose of up to {PurposeMaxLength} characters, which is recorded.");
        }

        if (await health.ComplexCase(command.CaseId, cancellationToken) is not { } complex)
        {
            audit.Record(Denied(target, "NOT_FOUND", command.Purpose));
            return NotFound;
        }

        if (!MayReadClinical(complex))
        {
            audit.Record(Denied(target, "NOT_THE_CASE_MANAGER", command.Purpose));
            return Error.Forbidden(HealthErrorCodes.NotTheCaseManager, "Only the case's manager, or someone with the clinical function, may read this.");
        }

        audit.Record(new AuditEntry("HEALTH", "ReadClinical", nameof(ComplexCase), target, new Dictionary<string, string?> { ["purpose"] = command.Purpose }));
        return new ComplexCaseClinicalView(complex.Id, complex.CaseNumber, complex.ConditionCategory,
            complex.Plans.OrderBy(p => p.PlanVersion).Select(p => new CarePlanView(p.PlanVersion, p.IsCurrent, p.ChangeReason,
                p.Items.Select(i => new CarePlanItemView(i.Id, i.Kind, i.Description, i.Responsible, i.DueOn, i.Status, i.ProgressNote)).ToList())).ToList(),
            complex.Notes.OrderBy(n => n.WrittenAt).Select(n => new CareNoteView(n.Id, n.ContactType, n.ContactRef, n.Note, n.SharedWithProviderId, n.ConsentReference, n.WrittenBy, n.WrittenAt)).ToList());
    }

    private static AuditEntry Denied(string target, string why, string? purpose) =>
        new("HEALTH", "ReadClinical", nameof(ComplexCase), target, new Dictionary<string, string?> { ["refused"] = why, ["purpose"] = purpose }) { Outcome = AuditOutcome.Denied };

    public async Task<Result<(IReadOnlyList<Guid> Cases, IReadOnlyList<Guid> Referrals)>> Handle(FindCareDueWork query, CancellationToken cancellationToken) =>
        (await health.CasesWithOverdueActivities(query.Day, query.Limit, cancellationToken), await health.ReferralsToExpire(query.Day, query.Limit, cancellationToken));

    public async Task<Result<int>> Handle(RaiseOverdueCareActivities command, CancellationToken cancellationToken) =>
        await health.ComplexCase(command.CaseId, cancellationToken) is { } complex ? complex.RaiseOverdue(command.Day, clock.UtcNow) : 0;
}

internal sealed class ClinicalReferralHandlers(IHealthRepository health, IExecutionContext execution, IClock clock, IBusinessDate businessDate, IAuditTrail audit) :
    ICommandHandler<RequestClinicalReferral, ClinicalReferralView>,
    ICommandHandler<DecideClinicalReferral, ClinicalReferralView>,
    ICommandHandler<RecordReferralVisit, ClinicalReferralView>,
    ICommandHandler<RecordReferralConsent, ClinicalReferralView>,
    ICommandHandler<WithdrawReferralConsent, ClinicalReferralView>,
    IQueryHandler<GetClinicalReferral, ClinicalReferralView>,
    IQueryHandler<ListClinicalReferrals, IReadOnlyList<ClinicalReferralView>>,
    ICommandHandler<ReadReferralClinical, ReferralClinicalView>,
    ICommandHandler<ReadPortalReferralClinical, ReferralClinicalView>,
    ICommandHandler<ExpireClinicalReferral, bool>
{
    private static Error NotFound => Error.NotFound(ErrorCodes.NotFound, "The referral was not found.");

    public async Task<Result<ClinicalReferralView>> Handle(RequestClinicalReferral command, CancellationToken cancellationToken)
    {
        if (await health.Membership(command.MembershipId, cancellationToken) is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The membership was not found.");
        }

        var id = Guid.CreateVersion7();
        var referral = ClinicalReferral.Request(id, Numbers.Of("RF", id, 25), command.MembershipId, command.PatientPartyId, command.ReferringProviderId, command.ReferredProviderId,
            command.ReferredSpecialty, command.ClinicalReason, command.ValidFrom, command.ValidTo, command.VisitsAllowed, command.ComplexCaseId, command.PreauthorisationId, command.ConsentReference);
        if (referral.IsFailure)
        {
            return referral.Error;
        }

        health.Add(referral.Value);
        audit.Record(new AuditEntry("HEALTH", "RequestClinicalReferral", nameof(ClinicalReferral), id.ToString()));
        return ClinicalReferralView.From(referral.Value);
    }

    public async Task<Result<ClinicalReferralView>> Handle(DecideClinicalReferral command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await health.ClinicalReferral(command.ReferralId, cancellationToken) is not { } referral)
        {
            return NotFound;
        }

        var decided = referral.Decide(command.Approved, command.Reason, user.Value, clock.UtcNow);
        if (decided.IsFailure)
        {
            return decided.Error;
        }

        audit.Record(new AuditEntry("HEALTH", "DecideClinicalReferral", nameof(ClinicalReferral), referral.Id.ToString(), new Dictionary<string, string?> { ["approved"] = command.Approved.ToString() }));
        return ClinicalReferralView.From(referral);
    }

    public async Task<Result<ClinicalReferralView>> Handle(RecordReferralVisit command, CancellationToken cancellationToken)
    {
        if (await health.ClinicalReferral(command.ReferralId, cancellationToken) is not { } referral)
        {
            return NotFound;
        }

        var visited = referral.RecordVisit(businessDate.Today);
        return visited.IsFailure ? visited.Error : ClinicalReferralView.From(referral);
    }

    public Task<Result<ClinicalReferralView>> Handle(RecordReferralConsent command, CancellationToken cancellationToken) =>
        Consent(command.ReferralId, "RecordReferralConsent", r => r.RecordConsent(command.ConsentReference), command.Reason, command.ConsentReference, cancellationToken);

    public Task<Result<ClinicalReferralView>> Handle(WithdrawReferralConsent command, CancellationToken cancellationToken) =>
        Consent(command.ReferralId, "WithdrawReferralConsent", r => r.WithdrawConsent(clock.UtcNow), command.Reason, null, cancellationToken);

    private async Task<Result<ClinicalReferralView>> Consent(Guid id, string operation, Func<ClinicalReferral, Result> change, string reason, string? reference, CancellationToken cancellationToken)
    {
        if (await health.ClinicalReferral(id, cancellationToken) is not { } referral)
        {
            return NotFound;
        }

        var changed = change(referral);
        if (changed.IsFailure)
        {
            return changed.Error;
        }

        audit.Record(new AuditEntry("HEALTH", operation, nameof(ClinicalReferral), id.ToString(), new Dictionary<string, string?> { ["reason"] = reason, ["consent"] = reference }));
        return ClinicalReferralView.From(referral);
    }

    public async Task<Result<ClinicalReferralView>> Handle(GetClinicalReferral query, CancellationToken cancellationToken) =>
        await health.ClinicalReferral(query.Id, cancellationToken) is { } referral ? ClinicalReferralView.From(referral) : NotFound;

    public async Task<Result<IReadOnlyList<ClinicalReferralView>>> Handle(ListClinicalReferrals query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<ClinicalReferralView>>((await health.ClinicalReferrals(query.MembershipId, query.ProviderId, query.Status, query.AfterId, query.Limit, cancellationToken))
            .Select(ClinicalReferralView.From).ToList());

    public async Task<Result<ReferralClinicalView>> Handle(ReadReferralClinical command, CancellationToken cancellationToken)
    {
        var target = command.ReferralId.ToString();
        if (string.IsNullOrWhiteSpace(command.Purpose) || command.Purpose.Length > ComplexCaseHandlers.PurposeMaxLength)
        {
            audit.Record(new AuditEntry("HEALTH", "ReadClinical", nameof(ClinicalReferral), target, new Dictionary<string, string?> { ["refused"] = "PURPOSE_REQUIRED" }) { Outcome = AuditOutcome.Denied });
            return Error.Validation(HealthErrorCodes.PurposeRequired, "Reading clinical information needs a purpose, which is recorded.");
        }

        if (await health.ClinicalReferral(command.ReferralId, cancellationToken) is not { } referral)
        {
            return NotFound;
        }

        audit.Record(new AuditEntry("HEALTH", "ReadClinical", nameof(ClinicalReferral), target, new Dictionary<string, string?> { ["purpose"] = command.Purpose }));
        return new ReferralClinicalView(referral.Id, referral.ClinicalReason);
    }

    public async Task<Result<ReferralClinicalView>> Handle(ReadPortalReferralClinical command, CancellationToken cancellationToken)
    {
        var target = command.ReferralId.ToString();
        var provider = execution.External?.ProviderId;
        if (string.IsNullOrWhiteSpace(command.Purpose) || command.Purpose.Length > ComplexCaseHandlers.PurposeMaxLength)
        {
            audit.Record(Denied(target, "PURPOSE_REQUIRED", null));
            return Error.Validation(HealthErrorCodes.PurposeRequired, "Reading clinical information needs a purpose, which is recorded.");
        }

        if (provider is null || await health.ClinicalReferral(command.ReferralId, cancellationToken) is not { } referral || !referral.Concerns(provider))
        {
            audit.Record(Denied(target, "NOT_THIS_PROVIDERS", command.Purpose));
            return NotFound;
        }

        if (!referral.ReasonOpenTo(provider))
        {
            audit.Record(Denied(target, "NO_CONSENT", command.Purpose));
            return Error.Forbidden(HealthErrorCodes.CareConsentRequired, "The referral does not name the member's consent to your reading its reason.");
        }

        audit.Record(new AuditEntry("HEALTH", "ReadClinical", nameof(ClinicalReferral), target, new Dictionary<string, string?> { ["purpose"] = command.Purpose, ["provider"] = provider }));
        return new ReferralClinicalView(referral.Id, referral.ClinicalReason);
    }

    private static AuditEntry Denied(string target, string why, string? purpose) =>
        new("HEALTH", "ReadClinical", nameof(ClinicalReferral), target, new Dictionary<string, string?> { ["refused"] = why, ["purpose"] = purpose }) { Outcome = AuditOutcome.Denied };

    public async Task<Result<bool>> Handle(ExpireClinicalReferral command, CancellationToken cancellationToken)
    {
        if (await health.ClinicalReferral(command.ReferralId, cancellationToken) is not { } referral)
        {
            return false;
        }

        var expired = referral.Expire(command.Day);
        return expired.IsFailure ? expired.Error : true;
    }
}
