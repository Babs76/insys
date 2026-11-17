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

public static class ProductDraftStatuses
{
    public const string Queued = "QUEUED";
    public const string Running = "RUNNING";
    public const string Drafted = "DRAFTED";

    /// <summary>A document was refused for the run (§5.5): its kind, its identifiers or its changed content. Nothing reached a model.</summary>
    public const string Refused = "REFUSED";
    public const string Failed = "FAILED";
}

public static class DraftItemKinds
{
    public const string Value = "VALUE";
    public const string RateTable = "RATE_TABLE";

    /// <summary>A rule in plain words (AIA-016): never an expression, never written to the Draft.</summary>
    public const string Rule = "RULE";

    /// <summary>A proposed simulation case (AIA-018).</summary>
    public const string SimulationCase = "SIM_CASE";
}

public static class DraftItemFlags
{
    /// <summary>Quoted directly (AIA-014): the value is in its citation's text.</summary>
    public const string Found = "FOUND";

    /// <summary>Derived from wording; needs review.</summary>
    public const string Interpreted = "INTERPRETED";

    /// <summary>Required by the model and not found: INSYS sets it, never the AI.</summary>
    public const string Missing = "MISSING";
}

/// <summary>AI-1's run (AI Assist SRS §4): a Draft version of one product, from documents its user uploaded and confirmed (D-16).</summary>
public sealed class ProductDraftRun : AggregateRoot<Guid>
{
    private ProductDraftRun()
    {
    }

    public string ProductCode { get; private set; } = "";

    public string Status { get; private set; } = ProductDraftStatuses.Queued;

    /// <summary>The user the job acts for (AIA-017): the Draft is theirs, so they are its maker.</summary>
    public string RequestedBy { get; private set; } = "";

    /// <summary>The token subject they asked with: the job resolves their permissions from it again at each step, and stops if it is no longer theirs.</summary>
    public string RequestedSubject { get; private set; } = "";

    public DateTimeOffset RequestedAt { get; private set; }

    public Guid? CorrelationId { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>Why it was refused or failed: an error code, never document text.</summary>
    public string? OutcomeCode { get; private set; }

    public int? DraftVersion { get; private set; }

    /// <summary>The model call's run (assist.run), for metering.</summary>
    public Guid? AssistRunId { get; private set; }

    public static ProductDraftRun Queue(string productCode, string requestedBy, string requestedSubject, DateTimeOffset at, Guid? correlationId) => new()
    {
        Id = Guid.CreateVersion7(), ProductCode = Guard.MaxLength(Guard.NotNullOrWhiteSpace(productCode), 30), RequestedBy = requestedBy,
        RequestedSubject = Guard.MaxLength(Guard.NotNullOrWhiteSpace(requestedSubject), 255), RequestedAt = at, CorrelationId = correlationId,
    };

    /// <summary>The job claims the run; a run already claimed (another instance, or a retry) is refused, so it runs once.</summary>
    public Result Start(DateTimeOffset at)
    {
        if (Status != ProductDraftStatuses.Queued)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"The run is {Status}.");
        }

        Status = ProductDraftStatuses.Running;
        StartedAt = at;
        MarkChanged();
        return Result.Success();
    }

    public Result Drafted(int version, Guid? assistRunId, DateTimeOffset at)
    {
        var running = Running();
        if (running.IsFailure)
        {
            return running;
        }

        Status = ProductDraftStatuses.Drafted;
        DraftVersion = version;
        AssistRunId = assistRunId;
        FinishedAt = at;
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Ends the run without a Draft, or with one whose edit was refused (<paramref name="version"/>), with the refusal's code.</summary>
    public Result End(string status, string code, DateTimeOffset at, int? version = null, Guid? assistRunId = null)
    {
        if (status is not (ProductDraftStatuses.Refused or ProductDraftStatuses.Failed))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        // A queued run whose user can no longer act, or which stalled, ends too; a finished one never changes.
        if (Status is not (ProductDraftStatuses.Queued or ProductDraftStatuses.Running))
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"The run is {Status}.");
        }

        Status = status;
        StartedAt ??= at;
        OutcomeCode = Guard.MaxLength(Guard.NotNullOrWhiteSpace(code), 60);
        DraftVersion = version;
        AssistRunId = assistRunId;
        FinishedAt = at;
        MarkChanged();
        return Result.Success();
    }

    private Result Running() => Status == ProductDraftStatuses.Running
        ? Result.Success()
        : Error.Conflict(ErrorCodes.InvalidStateTransition, $"The run is {Status}.");
}

/// <summary>A document a run reads, with its uploader's confirmation (D-16): the person, the document (id, file name, content hash) and the date.</summary>
public sealed class ProductDraftDocument : AggregateRoot<Guid>
{
    private ProductDraftDocument()
    {
    }

    public Guid RunId { get; private set; }

    public Guid DocumentId { get; private set; }

    public string? FileName { get; private set; }

    /// <summary>The content confirmed: a document whose content then differs is refused, and needs a new confirmation.</summary>
    public string Sha256 { get; private set; } = "";

    public long SizeBytes { get; private set; }

    public string ConfirmedBy { get; private set; } = "";

    public DateTimeOffset ConfirmedAt { get; private set; }

    /// <summary>PDF, DOCX or XLSX, read from the content once the scan released it.</summary>
    public string? Kind { get; private set; }

    public int[] UnreadablePages { get; private set; } = [];

    /// <summary>The identifier check's findings (§5.5) as JSON, each kind and page, never a value.</summary>
    public string? Identifiers { get; private set; }

    public string? RefusalCode { get; private set; }

    public static ProductDraftDocument Confirmed(Guid runId, Guid documentId, string? fileName, string sha256, long sizeBytes, string confirmedBy, DateTimeOffset at) => new()
    {
        Id = Guid.CreateVersion7(), RunId = runId, DocumentId = documentId, FileName = fileName is null ? null : Guard.MaxLength(fileName, 255), Sha256 = sha256,
        SizeBytes = sizeBytes, ConfirmedBy = confirmedBy, ConfirmedAt = at,
    };

    public void Read(string kind, IReadOnlyList<int> unreadablePages)
    {
        Kind = kind;
        UnreadablePages = [.. unreadablePages];
        MarkChanged();
    }

    public void Refuse(string code, string? identifiers = null)
    {
        RefusalCode = code;
        Identifiers = identifiers;
        MarkChanged();
    }
}

/// <summary>One value AI-1 proposed (AIA-012 to AIA-016), or one INSYS found missing, with the outcome of INSYS's own checks on it.</summary>
public sealed class DraftItem : AggregateRoot<Guid>
{
    private DraftItem()
    {
    }

    public Guid RunId { get; private set; }

    public string Kind { get; private set; } = DraftItemKinds.Value;

    /// <summary>The field of the generated field list, such as <c>version.name</c> or <c>rating.cfg_rating_tables</c>.</summary>
    public string FieldPath { get; private set; } = "";

    /// <summary>The proposed value as JSON; a rate table is its rows of cells.</summary>
    public string? Value { get; private set; }

    public string Flag { get; private set; } = DraftItemFlags.Missing;

    public Guid? CitationDocumentId { get; private set; }

    public int? CitationPage { get; private set; }

    public string? CitationQuote { get; private set; }

    /// <summary>PASSED, or DROPPED with the reason: an item dropped never reaches the Draft (AIA-013, AIA-015).</summary>
    public string CheckResult { get; private set; } = "PASSED";

    public string? CheckReason { get; private set; }

    /// <summary>Written to the Draft version (OQ-184 (a): the version's own fields only).</summary>
    public bool InDraft { get; private set; }

    public static DraftItem Proposed(Guid runId, string kind, string fieldPath, string? value, string flag, Guid? documentId, int? page, string? quote, string? dropped,
        bool inDraft) => new()
    {
        Id = Guid.CreateVersion7(), RunId = runId, Kind = kind, FieldPath = Guard.MaxLength(fieldPath, 160), Value = value, Flag = flag, CitationDocumentId = documentId,
        CitationPage = page, CitationQuote = quote is null ? null : quote.Length > 2000 ? quote[..2000] : quote, CheckResult = dropped is null ? "PASSED" : "DROPPED",
        CheckReason = dropped, InDraft = inDraft && dropped is null && kind == DraftItemKinds.Value && flag != DraftItemFlags.Missing,
    };

    public static DraftItem Missing(Guid runId, string fieldPath) => new()
    {
        Id = Guid.CreateVersion7(), RunId = runId, Kind = DraftItemKinds.Value, FieldPath = Guard.MaxLength(fieldPath, 160), Flag = DraftItemFlags.Missing,
    };
}
