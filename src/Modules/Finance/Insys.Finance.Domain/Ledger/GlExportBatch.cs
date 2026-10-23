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

/// <summary>finance.gl_export_batch.status (V0029).</summary>
public enum GlExportStatus
{
    Sent,
    Acknowledged,
    Rejected,
}

/// <summary>
/// A general ledger export batch (finance.gl_export_batch, F055; SRS §15.1): the posted journal lines of a date range,
/// summarised by accounting date, account and currency, sent to the insurer's GL in a layout. Posted entries never
/// change (FIN-002), so a batch's content is always the same and is rebuilt from them on request.
/// </summary>
public sealed class GlExportBatch : AggregateRoot<Guid>
{
    private GlExportBatch()
    {
    }

    public string LayoutCode { get; private set; } = "";

    public string PeriodCode { get; private set; } = "";

    public DateOnly FromDate { get; private set; }

    public DateOnly ToDate { get; private set; }

    public int LineCount { get; private set; }

    public GlExportStatus Status { get; private set; }

    /// <summary>TODO(spec): the export file as a document (document_id) needs a Documents storage contract; until then it is rebuilt.</summary>
    public Guid? DocumentId { get; private set; }

    /// <summary>The target system's reference for the batch, when it gave one.</summary>
    public string? TargetRef { get; private set; }

    public string? RejectionReason { get; private set; }

    /// <summary>A batch that the target system took: Sent, until it acknowledges or rejects it.</summary>
    public static GlExportBatch Sent(string layoutCode, string periodCode, DateOnly fromDate, DateOnly toDate, int lineCount, string? targetRef)
    {
        return new GlExportBatch
        {
            Id = Guid.CreateVersion7(),
            LayoutCode = Guard.MaxLength(Guard.NotNullOrWhiteSpace(layoutCode), 15),
            PeriodCode = Guard.MaxLength(Guard.NotNullOrWhiteSpace(periodCode), 7),
            FromDate = fromDate,
            ToDate = toDate >= fromDate ? toDate : throw new ArgumentException("toDate is before fromDate.", nameof(toDate)),
            LineCount = lineCount,
            Status = GlExportStatus.Sent,
            TargetRef = targetRef is { Length: > 40 } ? targetRef[..40] : targetRef,
        };
    }

    /// <summary>The target system's answer: acknowledged, or rejected with a reason (then the range may be exported again).</summary>
    public Result Acknowledge(bool accepted, string? reason)
    {
        if (Status != GlExportStatus.Sent)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"The batch is {Status}; only a sent batch is acknowledged or rejected.");
        }

        if (!accepted && string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A rejection needs a reason.");
        }

        Status = accepted ? GlExportStatus.Acknowledged : GlExportStatus.Rejected;
        RejectionReason = accepted ? null : Guard.MaxLength(reason!, 200);
        MarkChanged();
        return Result.Success();
    }
}
