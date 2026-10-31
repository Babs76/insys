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
using Insys.Health.Domain;
using Insys.Health.Domain.Adjudication;
using Insys.Health.Domain.Care;

namespace Insys.Health.Application;

// Wave 5b §4.4 (F086): disease programme enrolment. The ordinary views leave out the programme, which names a condition (HLT-008).

public sealed record EnrolInProgramme(Guid MembershipId, Guid PatientPartyId, string ProgrammeCode, Guid? ComplexCaseId) : ICommand<ProgrammeEnrolmentView>;

public sealed record RecordProgrammeReview(Guid EnrolmentId) : ICommand<ProgrammeEnrolmentView>;

public sealed record EndProgrammeEnrolment(Guid EnrolmentId, DateOnly LastDay) : ICommand<ProgrammeEnrolmentView>;

public sealed record GetProgrammeEnrolment(Guid Id) : IQuery<ProgrammeEnrolmentView>;

public sealed record ListProgrammeEnrolments(Guid? MembershipId, [property: OneOf(ProgrammeStatus.Active, ProgrammeStatus.Ended)] string? Status, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<ProgrammeEnrolmentView>>;

public sealed record ReadProgrammeEnrolmentClinical(Guid Id, string? Purpose) : ICommand<ProgrammeEnrolmentClinicalView>;

/// <summary>The daily job's work (F086).</summary>
public sealed record FindProgrammeReviewsDue(DateOnly Day, int Limit) : IQuery<IReadOnlyList<Guid>>;

public sealed record RaiseProgrammeReview(Guid EnrolmentId, DateOnly Day) : ICommand<bool>;

public sealed record ProgrammeEnrolmentView(Guid Id, string EnrolmentNumber, Guid MembershipId, Guid PatientPartyId, Guid? ComplexCaseId, [property: OneOf(ProgrammeStatus.Active, ProgrammeStatus.Ended)] string Status,
    DateOnly EnrolledOn, DateOnly? EndedOn, DateOnly NextReviewOn, long RowVersion)
{
    public static ProgrammeEnrolmentView From(ProgrammeEnrolment e) => new(e.Id, e.EnrolmentNumber, e.MembershipId, e.PatientPartyId, e.ComplexCaseId, e.Status, e.EnrolledOn, e.EndedOn, e.NextReviewOn, e.RowVersion);
}

public sealed record ProgrammeEnrolmentClinicalView(Guid Id, string ProgrammeCode);

public sealed class EnrolInProgrammeValidator : AbstractValidator<EnrolInProgramme>
{
    public EnrolInProgrammeValidator() => RuleFor(c => c.ProgrammeCode).NotEmpty().MaximumLength(15);
}

internal sealed class ProgrammeEnrolmentHandlers(IHealthRepository health, IHealthCodes codes, IExecutionContext execution, IBusinessDate businessDate, IClock clock, IAuditTrail audit) :
    ICommandHandler<EnrolInProgramme, ProgrammeEnrolmentView>,
    ICommandHandler<RecordProgrammeReview, ProgrammeEnrolmentView>,
    ICommandHandler<EndProgrammeEnrolment, ProgrammeEnrolmentView>,
    ICommandHandler<ReadProgrammeEnrolmentClinical, ProgrammeEnrolmentClinicalView>,
    ICommandHandler<RaiseProgrammeReview, bool>,
    IQueryHandler<GetProgrammeEnrolment, ProgrammeEnrolmentView>,
    IQueryHandler<ListProgrammeEnrolments, IReadOnlyList<ProgrammeEnrolmentView>>,
    IQueryHandler<FindProgrammeReviewsDue, IReadOnlyList<Guid>>
{
    private const int PurposeMaxLength = 200;

    private static Error NotFound => Error.NotFound(ErrorCodes.NotFound, "The enrolment was not found.");

    public async Task<Result<ProgrammeEnrolmentView>> Handle(EnrolInProgramme command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        var today = businessDate.Today;
        if (await health.Membership(command.MembershipId, cancellationToken) is not { } membership || !membership.Covers(command.PatientPartyId, today))
        {
            return Error.Conflict(HealthErrorCodes.MemberNotCovered, "The patient is not covered by the membership today.");
        }

        if (!(await codes.DiseaseProgrammes([command.ProgrammeCode], today, cancellationToken)).TryGetValue(command.ProgrammeCode, out var programme)
            || IsoDuration.Add(today, programme.ReviewInterval) is not { } firstReview)
        {
            return Error.Validation(HealthErrorCodes.CodeNotInForce, "That is not a disease programme in force.");
        }

        // A member with a covered condition: a registration in force for one of its conditions.
        if (!(await health.ChronicRegistrationsInForce(command.PatientPartyId, today, cancellationToken)).Any(r => programme.ConditionCodes.Contains(r.ConditionCode)))
        {
            return Error.Conflict(HealthErrorCodes.MemberNotCovered, "The patient has no registered condition this programme covers.");
        }

        var id = Guid.CreateVersion7();
        var enrolment = ProgrammeEnrolment.Enrol(id, Numbers.Of("PE", id, 25), command.MembershipId, command.PatientPartyId, command.ProgrammeCode, command.ComplexCaseId, today, firstReview, user.Value);
        health.Add(enrolment);
        audit.Record(new AuditEntry("HEALTH", "EnrolInProgramme", nameof(ProgrammeEnrolment), id.ToString()));
        return ProgrammeEnrolmentView.From(enrolment);
    }

    public async Task<Result<ProgrammeEnrolmentView>> Handle(RecordProgrammeReview command, CancellationToken cancellationToken)
    {
        if (await health.ProgrammeEnrolment(command.EnrolmentId, cancellationToken) is not { } enrolment)
        {
            return NotFound;
        }

        var today = businessDate.Today;
        var programme = (await codes.DiseaseProgrammes([enrolment.ProgrammeCode], today, cancellationToken)).GetValueOrDefault(enrolment.ProgrammeCode);
        if (programme is null || IsoDuration.Add(today, programme.ReviewInterval) is not { } next)
        {
            return Error.Validation(HealthErrorCodes.CodeNotInForce, "The enrolment's programme is not in force.");
        }

        var reviewed = enrolment.RecordReview(next, today);
        if (reviewed.IsFailure)
        {
            return reviewed.Error;
        }

        audit.Record(new AuditEntry("HEALTH", "RecordProgrammeReview", nameof(ProgrammeEnrolment), enrolment.Id.ToString()));
        return ProgrammeEnrolmentView.From(enrolment);
    }

    public async Task<Result<ProgrammeEnrolmentView>> Handle(EndProgrammeEnrolment command, CancellationToken cancellationToken)
    {
        if (await health.ProgrammeEnrolment(command.EnrolmentId, cancellationToken) is not { } enrolment)
        {
            return NotFound;
        }

        var ended = enrolment.End(command.LastDay);
        if (ended.IsFailure)
        {
            return ended.Error;
        }

        audit.Record(new AuditEntry("HEALTH", "EndProgrammeEnrolment", nameof(ProgrammeEnrolment), enrolment.Id.ToString()));
        return ProgrammeEnrolmentView.From(enrolment);
    }

    public async Task<Result<ProgrammeEnrolmentClinicalView>> Handle(ReadProgrammeEnrolmentClinical command, CancellationToken cancellationToken)
    {
        var target = command.Id.ToString();
        if (string.IsNullOrWhiteSpace(command.Purpose) || command.Purpose.Length > PurposeMaxLength)
        {
            audit.Record(Denied(target, "PURPOSE_REQUIRED", null));
            return Error.Validation(HealthErrorCodes.PurposeRequired, $"Reading clinical information needs a purpose of up to {PurposeMaxLength} characters, which is recorded (HLT-008).");
        }

        if (await health.ProgrammeEnrolment(command.Id, cancellationToken) is not { } enrolment)
        {
            audit.Record(Denied(target, "NOT_FOUND", command.Purpose));
            return NotFound;
        }

        audit.Record(new AuditEntry("HEALTH", "ReadClinical", nameof(ProgrammeEnrolment), target, new Dictionary<string, string?> { ["purpose"] = command.Purpose }));
        return new ProgrammeEnrolmentClinicalView(enrolment.Id, enrolment.ProgrammeCode);
    }

    public async Task<Result<bool>> Handle(RaiseProgrammeReview command, CancellationToken cancellationToken) =>
        await health.ProgrammeEnrolment(command.EnrolmentId, cancellationToken) is { } enrolment && enrolment.RaiseReviewDue(command.Day, clock.UtcNow);

    public async Task<Result<ProgrammeEnrolmentView>> Handle(GetProgrammeEnrolment query, CancellationToken cancellationToken) =>
        await health.ProgrammeEnrolment(query.Id, cancellationToken) is { } enrolment ? ProgrammeEnrolmentView.From(enrolment) : NotFound;

    public async Task<Result<IReadOnlyList<ProgrammeEnrolmentView>>> Handle(ListProgrammeEnrolments query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<ProgrammeEnrolmentView>>((await health.ProgrammeEnrolments(query.MembershipId, query.Status, query.AfterId, query.Limit, cancellationToken)).Select(ProgrammeEnrolmentView.From).ToList());

    public async Task<Result<IReadOnlyList<Guid>>> Handle(FindProgrammeReviewsDue query, CancellationToken cancellationToken) =>
        Result.Success(await health.ProgrammeReviewsDue(query.Day, query.Limit, cancellationToken));

    private static AuditEntry Denied(string target, string reason, string? purpose) =>
        new("HEALTH", "ReadClinical", nameof(ProgrammeEnrolment), target, new Dictionary<string, string?> { ["refused"] = reason, ["purpose"] = purpose })
        {
            Outcome = AuditOutcome.Denied,
        };
}
