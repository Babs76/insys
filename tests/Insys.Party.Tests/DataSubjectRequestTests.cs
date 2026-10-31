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
using Insys.Party.Application;
using Insys.Party.Domain.Parties;

namespace Insys.Party.Tests;

/// <summary>F059: identity by a person, then a second person, before anything leaves or is deleted; a refusal is the answer; the due date comes from the tenant's days.</summary>
public sealed class DataSubjectRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid First = Guid.CreateVersion7();
    private static readonly Guid Second = Guid.CreateVersion7();

    private static DataSubjectRequest Received(string type = DataSubjectRequestTypes.Access) =>
        DataSubjectRequest.Receive(Guid.CreateVersion7(), "DSR-1", Guid.CreateVersion7(), type, new DateOnly(2026, 2, 1), 30, First, Now);

    [Fact]
    public void TheDueDate_IsTheTenantsDaysFromReceipt_AcrossAMonthEnd_OQ159() => Received().DueOn.ShouldBe(new DateOnly(2026, 3, 3));

    [Fact]
    public void TheEvent_NamesNoParty_POPIA() => Received().DomainEvents.OfType<DataSubjectRequestReceived>().Single().ToString().ShouldNotContain("PartyId");

    [Fact]
    public void ASecondPerson_ApprovesAfterIdentityIsConfirmed_SRS002()
    {
        var request = Received();
        request.Approve(Second).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        request.Complete(null).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        request.ConfirmIdentity(First).IsSuccess.ShouldBeTrue();
        request.Approve(First).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);
        request.Approve(Second).IsSuccess.ShouldBeTrue();
        request.Complete(null).IsSuccess.ShouldBeTrue();
        request.Refuse("Too late").Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
    }

    [Fact]
    public void ARefusal_KeepsItsReason_AsTheAnswer()
    {
        var request = Received(DataSubjectRequestTypes.Deletion);
        request.Refuse("Records are still kept: IN_RETENTION until 2033-01-01").IsSuccess.ShouldBeTrue();
        (request.Status, request.Outcome, request.OutcomeReason).ShouldBe((DataSubjectRequestStatus.Refused, "REFUSED", "Records are still kept: IN_RETENTION until 2033-01-01"));
    }

    [Theory]
    [InlineData("HEALTH_CLAIM", true)]
    [InlineData("HEALTH_MEMBERSHIP", true)]
    [InlineData("LIFE_POLICY", false)]
    public void HealthRecords_AreClinical_InAnExport_HLT008(string recordType, bool clinical) => DataSubjectDefaults.IsClinical(recordType).ShouldBe(clinical);
}
