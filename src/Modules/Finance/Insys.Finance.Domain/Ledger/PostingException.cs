/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Finance
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

namespace Insys.Finance.Domain.Ledger;

public sealed record PostingFailed(Guid ExceptionId, Guid SourceEventId, string EventType, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// A business event that could not be posted (finance.posting_exception): no rule, a closed period, a missing rate.
/// It is visible to operators (FIN-007) and posted again once the cause is fixed; nothing is lost.
/// </summary>
public sealed class PostingException : AggregateRoot<Guid>
{
    private PostingException()
    {
    }

    public Guid SourceEventId { get; private set; }

    public string EventType { get; private set; } = "";

    public string Error { get; private set; } = "";

    /// <summary>OPEN or RESOLVED.</summary>
    public string Status { get; private set; } = "OPEN";

    public Guid? ResolvedBy { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    /// <summary>The journal posted when it was resolved.</summary>
    public Guid? JournalId { get; private set; }

    /// <summary>The business event as JSON, to post again.</summary>
    public string BusinessEvent { get; private set; } = "{}";

    public static PostingException Record(Guid id, Guid sourceEventId, string eventType, string error, string businessEventJson, DateTimeOffset now)
    {
        var exception = new PostingException
        {
            Id = Guard.NotEmpty(id),
            SourceEventId = sourceEventId,
            EventType = Guard.MaxLength(eventType, 80),
            Error = error,
            BusinessEvent = businessEventJson,
        };
        exception.Raise(new PostingFailed(id, sourceEventId, exception.EventType, error, now));
        exception.MarkChanged();
        return exception;
    }

    public Result Resolve(Guid resolvedBy, Guid journalId, DateTimeOffset now)
    {
        if (Status != "OPEN")
        {
            return Insys.BuildingBlocks.Domain.Error.Conflict(ErrorCodes.InvalidStateTransition, "The posting exception is already resolved.");
        }

        Status = "RESOLVED";
        ResolvedBy = resolvedBy;
        ResolvedAt = now;
        JournalId = journalId;
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Another attempt failed: the latest reason is kept.</summary>
    public void FailedAgain(string error)
    {
        Error = error;
        MarkChanged();
    }
}
