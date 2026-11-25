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

public static class BulkJobStatus
{
    public const string Done = "DONE";
    public const string Rejected = "REJECTED";
    public const string ToApprove = "TO_APPROVE";
    public const string Approved = "APPROVED";

    /// <summary>DE-059: the approver refused the export; nothing leaves.</summary>
    public const string Refused = "REFUSED";

    /// <summary>DE-059: who asked for the export withdrew it before it was approved; nothing leaves.</summary>
    public const string Cancelled = "CANCELLED";
}

/// <summary>The BulkJob aggregate (F076): an import, all or nothing, with every rejected line kept; or an export of personal data, which a second person approves before it leaves.</summary>
public sealed class BulkJob : AggregateRoot<Guid>
{
    public const string Import = "PARTY_IMPORT";
    public const string Export = "PARTY_EXPORT";
    public const string PolicyExport = "POLICY_EXPORT";

    /// <summary>BR-129: what a job's type is called on a screen.</summary>
    public static string Label(string jobType) => jobType switch
    {
        Import => "Party import",
        Export => "Party export",
        PolicyExport => "Policy export",
        _ => jobType,
    };

    private BulkJob()
    {
    }

    public string JobType { get; private set; } = "";

    public string Status { get; private set; } = "";

    public string? Filter { get; private set; }

    public int TotalCount { get; private set; }

    public int AcceptedCount { get; private set; }

    public string? RejectedLines { get; private set; }

    public Guid RunBy { get; private set; }

    public Guid? ApprovedBy { get; private set; }

    /// <summary>DE-059: who refused the export (the approver), or null.</summary>
    public Guid? RefusedBy { get; private set; }

    /// <summary>DE-059: why the export was refused, in the approver's words.</summary>
    public string? RefusalReason { get; private set; }

    public static BulkJob Imported(Guid id, int total, string? rejectedLines, int rejected, Guid runBy)
    {
        var job = new BulkJob
        {
            Id = Guard.NotEmpty(id), JobType = Import, Status = rejected == 0 ? BulkJobStatus.Done : BulkJobStatus.Rejected, TotalCount = total, AcceptedCount = rejected == 0 ? total : 0,
            RejectedLines = rejectedLines, RunBy = Guard.NotEmpty(runBy),
        };
        job.MarkChanged();
        return job;
    }

    public static BulkJob ExportRequested(Guid id, string? filter, int rows, Guid runBy)
    {
        var job = new BulkJob { Id = Guard.NotEmpty(id), JobType = Export, Status = BulkJobStatus.ToApprove, Filter = filter, TotalCount = rows, RunBy = Guard.NotEmpty(runBy) };
        job.MarkChanged();
        return job;
    }

    /// <summary>OQ-158: policies leave the same way as parties: a second person approves first.</summary>
    public static BulkJob PolicyExportRequested(Guid id, string? domain, int rows, Guid runBy)
    {
        var job = new BulkJob { Id = Guard.NotEmpty(id), JobType = PolicyExport, Status = BulkJobStatus.ToApprove, Filter = domain, TotalCount = rows, RunBy = Guard.NotEmpty(runBy) };
        job.MarkChanged();
        return job;
    }

    public Result ApproveExport(Guid by)
    {
        if (JobType is not (Export or PolicyExport) || Status != BulkJobStatus.ToApprove)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"This job is {Status}.");
        }

        if (by == RunBy)
        {
            return Error.Conflict(ErrorCodes.MakerCheckerSamePerson, "A second person approves an export of personal data (SRS-002).");
        }

        Status = BulkJobStatus.Approved;
        ApprovedBy = by;
        MarkChanged();
        return Result.Success();
    }

    /// <summary>
    /// DE-059: the approver refuses an export waiting for approval (TO_APPROVE → REFUSED), with a reason; nothing leaves. As
    /// for approval, a second person decides (SRS-002): who asked withdraws it with <see cref="CancelExport"/> instead.
    /// </summary>
    public Result RefuseExport(Guid by, string reason)
    {
        if (JobType is not (Export or PolicyExport) || Status != BulkJobStatus.ToApprove)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"This job is {Status}; only an export waiting for approval is refused.");
        }

        if (by == RunBy)
        {
            return Error.Conflict(ErrorCodes.MakerCheckerSamePerson, "A second person refuses an export (SRS-002); who asked for it cancels it instead.");
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A refusal gives its reason, of at most 500 characters.");
        }

        Status = BulkJobStatus.Refused;
        RefusedBy = by;
        RefusalReason = reason.Trim();
        MarkChanged();
        return Result.Success();
    }

    /// <summary>DE-059: who asked for an export withdraws it while it waits for approval (TO_APPROVE → CANCELLED); nothing leaves.</summary>
    public Result CancelExport(Guid by)
    {
        if (JobType is not (Export or PolicyExport) || Status != BulkJobStatus.ToApprove)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"This job is {Status}; only an export waiting for approval is cancelled.");
        }

        if (by != RunBy)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only who asked for the export cancels it; the approver refuses it instead.");
        }

        Status = BulkJobStatus.Cancelled;
        MarkChanged();
        return Result.Success();
    }
}
