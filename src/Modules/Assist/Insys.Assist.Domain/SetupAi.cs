/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist
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

namespace Insys.Assist.Domain;

public static class SetupAiStatuses
{
    public const string Queued = "QUEUED";
    public const string Running = "RUNNING";

    /// <summary>The pass finished: its tailored and mapped values are in the draft, undecided, and what it couldn't map is listed.</summary>
    public const string Drafted = "DRAFTED";

    /// <summary>A document was refused for the pass (ai-assist.md §5.5): its kind, its identifiers or its changed content. Nothing reached a model.</summary>
    public const string Refused = "REFUSED";
    public const string Failed = "FAILED";
}

/// <summary>Why a proposed value or a source line is on the unmapped list (AIA-083).</summary>
public static class UnmappedReasons
{
    /// <summary>The model listed it as a line it couldn't map.</summary>
    public const string NotMapped = "NOT_MAPPED";
}

/// <summary>
/// One AI pass over a setup run (AI Assist SRS §5A.3, step 3 of §8.3): the run's answers and documents, read by a job acting for the user who asked, as AI-1's
/// (AIA-017). It tailors the pack rows marked tailorable and maps the documents onto INSYS's tables; INSYS's checks decide what is kept. Nothing it does takes
/// effect: each value is undecided until a person accepts it, and the SETUP change set needs a second person's approval (Ade, 19 November 2026).
/// </summary>
public sealed class SetupAiRun : AggregateRoot<Guid>
{
    private SetupAiRun()
    {
    }

    public Guid SetupRunId { get; private set; }

    /// <summary>The answers version the pass read: a pass finished after the answers changed records nothing.</summary>
    public int AnswersVersion { get; private set; }

    public string Status { get; private set; } = SetupAiStatuses.Queued;

    public string RequestedBy { get; private set; } = "";

    /// <summary>The token subject they asked with: the job resolves their permissions from it again at each step.</summary>
    public string RequestedSubject { get; private set; } = "";

    public DateTimeOffset RequestedAt { get; private set; }

    public Guid? CorrelationId { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>Why it was refused or failed: an error code, never document text.</summary>
    public string? OutcomeCode { get; private set; }

    public Guid? AssistRunId { get; private set; }

    public int? Tailored { get; private set; }

    public int? Mapped { get; private set; }

    public int? Unmapped { get; private set; }

    public static SetupAiRun Queue(Guid setupRunId, int answersVersion, string requestedBy, string requestedSubject, DateTimeOffset at, Guid? correlationId) => new()
    {
        Id = Guid.CreateVersion7(), SetupRunId = setupRunId, AnswersVersion = answersVersion, RequestedBy = requestedBy,
        RequestedSubject = Guard.MaxLength(Guard.NotNullOrWhiteSpace(requestedSubject), 255), RequestedAt = at, CorrelationId = correlationId,
    };

    public bool Open => Status is SetupAiStatuses.Queued or SetupAiStatuses.Running;

    /// <summary>The job claims the pass; a pass already claimed is refused, so it runs once.</summary>
    public Result Start(DateTimeOffset at)
    {
        if (Status != SetupAiStatuses.Queued)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"The pass is {Status}.");
        }

        (Status, StartedAt) = (SetupAiStatuses.Running, at);
        MarkChanged();
        return Result.Success();
    }

    public Result Drafted(int tailored, int mapped, int unmapped, Guid? assistRunId, DateTimeOffset at)
    {
        if (Status != SetupAiStatuses.Running)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"The pass is {Status}.");
        }

        (Status, Tailored, Mapped, Unmapped, AssistRunId, FinishedAt) = (SetupAiStatuses.Drafted, tailored, mapped, unmapped, assistRunId, at);
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Ends the pass with nothing recorded in the draft, with the refusal's code.</summary>
    public Result End(string status, string code, DateTimeOffset at, Guid? assistRunId = null)
    {
        if (status is not (SetupAiStatuses.Refused or SetupAiStatuses.Failed))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (!Open)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"The pass is {Status}.");
        }

        (Status, OutcomeCode, AssistRunId, FinishedAt) = (status, Guard.MaxLength(Guard.NotNullOrWhiteSpace(code), 60), assistRunId, at);
        StartedAt ??= at;
        MarkChanged();
        return Result.Success();
    }
}

/// <summary>A document a setup run reads, with its uploader's confirmation (D-16): the person, the document (id, file name, content hash) and the date.</summary>
public sealed class SetupDocument : AggregateRoot<Guid>
{
    private SetupDocument()
    {
    }

    public Guid SetupRunId { get; private set; }

    public Guid DocumentId { get; private set; }

    public string? FileName { get; private set; }

    /// <summary>The content confirmed: a document whose content then differs is refused, and needs a new confirmation.</summary>
    public string Sha256 { get; private set; } = "";

    public long SizeBytes { get; private set; }

    public string ConfirmedBy { get; private set; } = "";

    public DateTimeOffset ConfirmedAt { get; private set; }

    public string? Kind { get; private set; }

    public int[] UnreadablePages { get; private set; } = [];

    public string? Identifiers { get; private set; }

    public string? RefusalCode { get; private set; }

    /// <summary>Taken off the run: kept for the record of its confirmation, never read again.</summary>
    public bool Removed { get; private set; }

    public static SetupDocument Confirmed(Guid setupRunId, Guid documentId, string? fileName, string sha256, long sizeBytes, string confirmedBy, DateTimeOffset at) => new()
    {
        Id = Guid.CreateVersion7(), SetupRunId = setupRunId, DocumentId = documentId, FileName = fileName is null ? null : Guard.MaxLength(fileName, 255), Sha256 = sha256,
        SizeBytes = sizeBytes, ConfirmedBy = confirmedBy, ConfirmedAt = at,
    };

    /// <summary>The same document confirmed again (a new upload has a new id): its confirmation now, for its content now.</summary>
    public void Reconfirm(string? fileName, string sha256, long sizeBytes, string confirmedBy, DateTimeOffset at)
    {
        (FileName, Sha256, SizeBytes, ConfirmedBy, ConfirmedAt, Removed) = (fileName is null ? null : Guard.MaxLength(fileName, 255), sha256, sizeBytes, confirmedBy, at, false);
        (Kind, UnreadablePages, Identifiers, RefusalCode) = (null, [], null, null);
        MarkChanged();
    }

    public void Remove()
    {
        Removed = true;
        MarkChanged();
    }

    /// <summary>What the last pass found reading it: its kind and unreadable pages, or why it was refused, with the identifiers' kinds and pages, never values.</summary>
    public void Read(string? kind, IReadOnlyList<int> unreadablePages, string? refusalCode, string? identifiers)
    {
        (Kind, UnreadablePages, RefusalCode, Identifiers) = (kind ?? Kind, [.. unreadablePages], refusalCode, identifiers);
        MarkChanged();
    }
}

/// <summary>One line the AI couldn't map, or one proposal INSYS's checks dropped, with why (AIA-083): document, page and words where there are any.</summary>
public sealed class SetupUnmapped : AggregateRoot<Guid>
{
    private SetupUnmapped()
    {
    }

    public Guid SetupRunId { get; private set; }

    public Guid AiRunId { get; private set; }

    public Guid? DocumentId { get; private set; }

    public int? Page { get; private set; }

    public string? Quote { get; private set; }

    public string? TargetTable { get; private set; }

    public string Reason { get; private set; } = UnmappedReasons.NotMapped;

    public static SetupUnmapped For(Guid setupRunId, Guid aiRunId, Guid? documentId, int? page, string? quote, string? targetTable, string reason) => new()
    {
        Id = Guid.CreateVersion7(), SetupRunId = setupRunId, AiRunId = aiRunId, DocumentId = documentId, Page = page,
        Quote = quote is null ? null : quote.Length > 2000 ? quote[..2000] : quote, TargetTable = targetTable is null ? null : Guard.MaxLength(targetTable, 80),
        Reason = Guard.MaxLength(reason, 40),
    };
}
