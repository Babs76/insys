/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Party.Tests
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
using Insys.Party.Domain.Parties;

namespace Insys.Party.Tests;

/// <summary>F076: an import is all or nothing; a second person approves an export of personal data.</summary>
public sealed class BulkJobTests
{
    private static readonly Guid First = Guid.CreateVersion7();

    [Fact]
    public void AnImportWithARejectedLine_AcceptsNothing()
    {
        var job = BulkJob.Imported(Guid.CreateVersion7(), 3, "[]", 1, First);
        (job.Status, job.AcceptedCount, job.TotalCount).ShouldBe((BulkJobStatus.Rejected, 0, 3));
        BulkJob.Imported(Guid.CreateVersion7(), 3, null, 0, First).ShouldSatisfyAllConditions(j => j.Status.ShouldBe(BulkJobStatus.Done), j => j.AcceptedCount.ShouldBe(3));
    }

    [Fact]
    public void AnExport_NeedsASecondPerson_SRS002()
    {
        var job = BulkJob.ExportRequested(Guid.CreateVersion7(), "ACTIVE", 10, First);
        job.ApproveExport(First).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);
        job.ApproveExport(Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();
        job.Status.ShouldBe(BulkJobStatus.Approved);
        job.ApproveExport(Guid.CreateVersion7()).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
    }
}
