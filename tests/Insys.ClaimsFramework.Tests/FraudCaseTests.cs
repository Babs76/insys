/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ClaimsFramework.Tests
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
using Insys.ClaimsFramework.Domain.Claims;

namespace Insys.ClaimsFramework.Tests;

/// <summary>F058, OQ-160: a fraud case raises nothing that could tip off its subject, and the investigator concludes it.</summary>
public sealed class FraudCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Opener = Guid.CreateVersion7();
    private static readonly Guid Investigator = Guid.CreateVersion7();

    private static FraudCase Opened() => FraudCase.Open(Guid.CreateVersion7(), "FC-1", "GENERAL", "CL-1", null, ["LATE_CLAIM"], Opener).Value;

    [Fact]
    public void ACase_IsAboutAClaimOrAPolicy() =>
        FraudCase.Open(Guid.CreateVersion7(), "FC-1", "LIFE", null, null, [], Opener).Error!.Code.ShouldBe(ErrorCodes.ValidationFailed);

    [Fact]
    public void OnlyTheInvestigator_Concludes()
    {
        var fraudCase = Opened();
        fraudCase.Conclude(FraudOutcomes.Confirmed, "Evidence", Investigator, Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        fraudCase.Assign(Investigator).IsSuccess.ShouldBeTrue();
        fraudCase.Conclude(FraudOutcomes.Confirmed, "Evidence", Opener, Now).Error!.Code.ShouldBe(ErrorCodes.Forbidden);
        fraudCase.Conclude(FraudOutcomes.Confirmed, "Evidence", Investigator, Now).IsSuccess.ShouldBeTrue();
        fraudCase.Status.ShouldBe(FraudCaseStatus.Concluded);
        fraudCase.Assign(Opener).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
    }

    [Fact]
    public void ACase_RaisesNoEvent_SoNothingTipsOffItsSubject()
    {
        var fraudCase = Opened();
        fraudCase.Assign(Investigator);
        fraudCase.Conclude(FraudOutcomes.NotConfirmed, "No evidence", Investigator, Now);
        fraudCase.DomainEvents.ShouldBeEmpty();
    }
}
