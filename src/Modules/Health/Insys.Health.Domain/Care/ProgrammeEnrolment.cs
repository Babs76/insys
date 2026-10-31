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
using Insys.BuildingBlocks.Domain;

namespace Insys.Health.Domain.Care;

public static class ProgrammeStatus
{
    public const string Active = "ACTIVE";
    public const string Ended = "ENDED";
}

public sealed record ProgrammeReviewDue(Guid ReviewId, Guid EnrolmentId, string EnrolmentNumber, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The ProgrammeEnrolment aggregate (F086): a person enrols a member with a covered condition. The programme's benefits apply while enrolled, and a review falls due at its
/// interval, as a task. It can link to a complex case but doesn't need one. A programme names a condition, so the code appears only in the audited clinical read.
/// </summary>
public sealed class ProgrammeEnrolment : AggregateRoot<Guid>
{
    private ProgrammeEnrolment()
    {
    }

    public string EnrolmentNumber { get; private set; } = "";

    public Guid MembershipId { get; private set; }

    public Guid PatientPartyId { get; private set; }

    public string ProgrammeCode { get; private set; } = "";

    public Guid? ComplexCaseId { get; private set; }

    public string Status { get; private set; } = ProgrammeStatus.Active;

    public DateOnly EnrolledOn { get; private set; }

    public DateOnly? EndedOn { get; private set; }

    public DateOnly NextReviewOn { get; private set; }

    /// <summary>The review date a task was last raised for, so one date raises one task.</summary>
    public DateOnly? ReviewRaisedFor { get; private set; }

    public Guid EnrolledBy { get; private set; }

    public static ProgrammeEnrolment Enrol(Guid id, string number, Guid membershipId, Guid patientPartyId, string programmeCode, Guid? complexCaseId, DateOnly enrolledOn, DateOnly nextReviewOn, Guid enrolledBy)
    {
        var enrolment = new ProgrammeEnrolment
        {
            Id = Guard.NotEmpty(id), EnrolmentNumber = Guard.MaxLength(number, 25), MembershipId = Guard.NotEmpty(membershipId), PatientPartyId = Guard.NotEmpty(patientPartyId),
            ProgrammeCode = Guard.MaxLength(programmeCode, 15), ComplexCaseId = complexCaseId, EnrolledOn = enrolledOn, NextReviewOn = nextReviewOn, EnrolledBy = Guard.NotEmpty(enrolledBy),
        };
        enrolment.MarkChanged();
        return enrolment;
    }

    /// <summary>Raises the review's task once for its date.</summary>
    public bool RaiseReviewDue(DateOnly day, DateTimeOffset now)
    {
        if (Status != ProgrammeStatus.Active || NextReviewOn > day || ReviewRaisedFor == NextReviewOn)
        {
            return false;
        }

        ReviewRaisedFor = NextReviewOn;
        Raise(new ProgrammeReviewDue(Guid.CreateVersion7(), Id, EnrolmentNumber, now));
        return true;
    }

    public Result RecordReview(DateOnly nextReviewOn, DateOnly day)
    {
        if (Status != ProgrammeStatus.Active || nextReviewOn <= day)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "An active enrolment's next review is after today.");
        }

        NextReviewOn = nextReviewOn;
        MarkChanged();
        return Result.Success();
    }

    public Result End(DateOnly lastDay)
    {
        if (Status != ProgrammeStatus.Active || lastDay < EnrolledOn)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Enrolment {EnrolmentNumber} is {Status}.");
        }

        Status = ProgrammeStatus.Ended;
        EndedOn = lastDay;
        MarkChanged();
        return Result.Success();
    }
}
