/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Party
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

namespace Insys.Party.Domain.Parties;

public static class DataSubjectRequestTypes
{
    public const string Access = "ACCESS";
    public const string Correction = "CORRECTION";
    public const string Deletion = "DELETION";
}

public static class DataSubjectRequestStatus
{
    public const string Received = "RECEIVED";
    public const string IdentityConfirmed = "IDENTITY_CONFIRMED";
    public const string Approved = "APPROVED";
    public const string Completed = "COMPLETED";
    public const string Refused = "REFUSED";
}

public sealed record DataSubjectRequestReceived(Guid RequestId, string RequestNumber, DateOnly DueOn, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>A party's retention has ended; a person decides whether to anonymise it (F064). Anonymising stays a person's act (OQ-054).</summary>
public sealed record PartyRetentionEnded(Guid PartyId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The DataSubjectRequest aggregate (F059; POPIA ss 23, 24): received by staff or the portal, the identity confirmed by a person, then a second person approves before any export
/// leaves or any deletion runs. A deletion refused within retention or under hold is itself the answer to the data subject, with its reason. Who approves isn't who confirmed.
/// </summary>
public sealed class DataSubjectRequest : AggregateRoot<Guid>
{
    private DataSubjectRequest()
    {
    }

    public string RequestNumber { get; private set; } = "";

    public Guid PartyId { get; private set; }

    public string RequestType { get; private set; } = "";

    public DateOnly ReceivedOn { get; private set; }

    public DateOnly DueOn { get; private set; }

    public Guid? ReceivedBy { get; private set; }

    public string Status { get; private set; } = DataSubjectRequestStatus.Received;

    public Guid? IdentityConfirmedBy { get; private set; }

    public Guid? ApprovedBy { get; private set; }

    public string? Outcome { get; private set; }

    public string? OutcomeReason { get; private set; }

    public static DataSubjectRequest Receive(Guid id, string number, Guid partyId, string requestType, DateOnly receivedOn, int responseDays, Guid? receivedBy, DateTimeOffset now)
    {
        var request = new DataSubjectRequest
        {
            Id = Guard.NotEmpty(id), RequestNumber = Guard.MaxLength(number, 25), PartyId = Guard.NotEmpty(partyId), RequestType = requestType, ReceivedOn = receivedOn,
            DueOn = receivedOn.AddDays(responseDays), ReceivedBy = receivedBy,
        };
        request.Raise(new DataSubjectRequestReceived(id, request.RequestNumber, request.DueOn, now));
        return request;
    }

    public Result ConfirmIdentity(Guid by)
    {
        if (Status != DataSubjectRequestStatus.Received)
        {
            return Wrong();
        }

        Status = DataSubjectRequestStatus.IdentityConfirmed;
        IdentityConfirmedBy = Guard.NotEmpty(by);
        MarkChanged();
        return Result.Success();
    }

    public Result Approve(Guid by)
    {
        if (Status != DataSubjectRequestStatus.IdentityConfirmed)
        {
            return Wrong();
        }

        if (by == IdentityConfirmedBy)
        {
            return Error.Conflict(ErrorCodes.MakerCheckerSamePerson, "A second person approves a data-subject request (SRS-002).");
        }

        Status = DataSubjectRequestStatus.Approved;
        ApprovedBy = by;
        MarkChanged();
        return Result.Success();
    }

    public Result Complete(string? reason)
    {
        if (Status != DataSubjectRequestStatus.Approved)
        {
            return Wrong();
        }

        Status = DataSubjectRequestStatus.Completed;
        Outcome = "DONE";
        OutcomeReason = reason;
        MarkChanged();
        return Result.Success();
    }

    /// <summary>The refusal is the answer. A person states it, or a deletion's check gives it.</summary>
    public Result Refuse(string reason)
    {
        if (Status is DataSubjectRequestStatus.Completed or DataSubjectRequestStatus.Refused)
        {
            return Wrong();
        }

        Status = DataSubjectRequestStatus.Refused;
        Outcome = "REFUSED";
        OutcomeReason = Guard.MaxLength(Guard.NotNullOrWhiteSpace(reason), 1000);
        MarkChanged();
        return Result.Success();
    }

    private Result Wrong() => Error.Conflict(ErrorCodes.InvalidStateTransition, $"Request {RequestNumber} is {Status}.");
}
