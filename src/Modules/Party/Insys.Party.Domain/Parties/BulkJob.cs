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
}

/// <summary>The BulkJob aggregate (F076): an import, all or nothing, with every rejected line kept; or an export of personal data, which a second person approves before it leaves.</summary>
public sealed class BulkJob : AggregateRoot<Guid>
{
    public const string Import = "PARTY_IMPORT";
    public const string Export = "PARTY_EXPORT";

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

    public Result ApproveExport(Guid by)
    {
        if (JobType != Export || Status != BulkJobStatus.ToApprove)
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
}
