/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Finance.Tests
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
using Insys.Finance.Application;
using Insys.Finance.Domain.Ledger;

namespace Insys.Finance.Tests;

/// <summary>F094: a second person decides a return, and only an approved one leaves.</summary>
public sealed class TaxReturnTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Preparer = Guid.CreateVersion7();
    private static readonly Guid Checker = Guid.CreateVersion7();

    private static TaxReturn Prepared() => TaxReturn.Prepare(Guid.CreateVersion7(), "TR-1", "VAT", "2200", "ZAR", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), 1304.35m, Preparer);

    [Fact]
    public void WhoPreparedIt_CannotDecideIt_SRS002()
    {
        Prepared().Approve(Preparer, Now).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);
        Prepared().Reject("Wrong period", Preparer, Now).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);
    }

    [Fact]
    public void AReturn_IsDecidedOnce()
    {
        var taxReturn = Prepared();
        taxReturn.Approve(Checker, Now).IsSuccess.ShouldBeTrue();
        taxReturn.Reject("Too late", Checker, Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
    }

    [Fact]
    public void TheFile_HasTheTotals_ToTheCent() =>
        TaxReturnView.Csv(Prepared()).ShouldBe("return_number,tax_code,currency,period_start,period_end,output_total,input_total\nTR-1,VAT,ZAR,2026-10-01,2026-10-31,1304.35,0.00\n");
}
